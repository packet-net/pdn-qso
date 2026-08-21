using System.Text;
using System.Threading.Channels;
using Packet.SoundModem.Modems;

namespace PdnQso.Link.Chat;

/// <summary>
/// Keyboard-to-keyboard chat over a station: one line at a time, sent until it is
/// acknowledged or the attempts run out, with the MS110D waveform stepped down when the link
/// stops working and back up when it starts again.
/// </summary>
/// <remarks>
/// <para>
/// docs/design.md section 3. Stop-and-wait: a <see cref="LinkFrameType.Chat"/> frame carrying
/// <c>seq(1) | waveform(1) | text</c>, then a wait for the <see cref="LinkFrameType.ChatAck"/>
/// that echoes the sequence number, then a retry after a backoff that waits for the channel
/// and then for a random number of slots. There is no window and no selective repeat: one
/// line of typing per keyup is what an HF path can carry, and an operator who cannot see
/// whether their last line landed has no use for a second one in flight.
/// </para>
/// <para>
/// <b>Incoming lines are always acknowledged</b>, duplicates included. A lost acknowledgement
/// and a lost line look identical from the sending end, so the far station retries either
/// way; answering only the first copy would leave it retrying something we already have. The
/// duplicate is suppressed here instead, by <c>(source, session, seq)</c>, so it is
/// acknowledged twice and shown once.
/// </para>
/// <para>
/// <b>Two stations, or name the one.</b> Every frame is addressed to the destination
/// <c>QSO</c> rather than to a callsign, because Monitor and the frame log want it that way,
/// so with no <see cref="ChatOptions.Correspondent"/> set this session answers whoever calls
/// and takes an acknowledgement from whoever sends one. That is what a point-to-point test
/// wants. Put three of these on one frequency and every station will answer every line, which
/// is what <see cref="ChatOptions.Correspondent"/> is for.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="MessageReceived"/> and <see cref="CorrespondentSeen"/> are
/// raised on whatever thread the audio arrived on, which is the capture thread on a real
/// device. Nothing is transmitted from inside that handler: replies are posted to this
/// session's own transmit pump, because a station's receive path can be re-entered by its own
/// transmit and an acknowledgement sent from a decode callback would do exactly that.
/// A UI must marshal these events to its own thread.
/// </para>
/// <para>
/// <b>Allocation.</b> Per-message, not per-frame-of-audio: a line builds a payload, a frame
/// and its encoded bytes, and nothing in the receive path allocates until a frame has
/// actually decoded. The duplicate window is a fixed ring.
/// </para>
/// </remarks>
public sealed class ChatSession : IAsyncDisposable
{
    private readonly IStation _station;
    private readonly ChatOptions _options;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly WaveformLadder _ladder;
    private readonly SeenLines _seen;
    private readonly SemaphoreSlim _oneLineAtATime = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Channel<Outbound> _outbox =
        Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });

    private Task _pump = Task.CompletedTask;

    // Written by the task running the ARQ, read on the receive thread that decodes the
    // acknowledgement, and the other way about for the disposal flag. Volatile rather than
    // locked: every one of them is a single word whose latest value is the whole question.
    private volatile Pending? _pending;
    private volatile bool _waitingForChannel;
    private int _owed;
    private volatile bool _started;
    private volatile bool _disposed;
    private byte _nextSeq;
    private int _consecutiveFailures;
    private int _consecutiveCleanDeliveries;
    private int _sent;
    private int _delivered;
    private int _failed;
    private int _retries;
    private int _received;
    private int _duplicates;

    /// <summary>
    /// Builds a conversation over a station.
    /// </summary>
    /// <param name="station">The radio. It must be started separately; this session neither
    /// opens nor closes it.</param>
    /// <param name="options">Timeouts, retries and the waveform rules; the defaults when omitted.</param>
    /// <param name="timeProvider">Every timeout and every measurement goes through this.</param>
    /// <param name="random">The session id and the backoff slots come from here; pass a seeded
    /// one to make a test repeat exactly.</param>
    /// <param name="ladder">The waveform ladder; built from the station's own modem when
    /// omitted, which is what a real station wants.</param>
    /// <exception cref="ArgumentException"><paramref name="options"/> is not workable.</exception>
    public ChatSession(
        IStation station,
        ChatOptions? options = null,
        TimeProvider? timeProvider = null,
        Random? random = null,
        WaveformLadder? ladder = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        _station = station;
        _options = options ?? new ChatOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        _ladder = ladder ?? WaveformLadder.ForStation(station, _options.WaveformSteps);
        _seen = new SeenLines(_options.DuplicateWindow);
        SessionId = _options.SessionId ?? (byte)_random.Next(0, 256);
        _ladder.Changed += OnWaveformChanged;
    }

    /// <summary>A line arrived from the other end. Raised on the receive thread.</summary>
    public event Action<ChatMessage>? MessageReceived;

    /// <summary>Somebody said hello. Raised on the receive thread.</summary>
    public event Action<string>? CorrespondentSeen;

    /// <summary>The transmit waveform moved: the new number, and why.</summary>
    public event Action<int, string>? WaveformChanged;

    /// <summary>An attempt at a line went unacknowledged and will be tried again if it can be.</summary>
    public event Action<ChatAttempt>? AttemptFailed;

    /// <summary>
    /// A frame this session queued could not be transmitted at all - the channel never
    /// cleared, or the device failed. Surfaced rather than swallowed: a station that believes
    /// it is being heard and is not is the worst failure this tool has.
    /// </summary>
    public event Action<Exception>? TransmitFailed;

    /// <summary>This conversation's id, which our chat frames carry and our correspondent's
    /// acknowledgements echo back.</summary>
    public byte SessionId { get; }

    /// <summary>The one station this conversation is with, or null for whoever calls.</summary>
    public string? Correspondent => _options.Correspondent;

    /// <summary>The line in flight, or null when nothing is.</summary>
    public ChatOutstanding? Outstanding => _pending?.Snapshot();

    /// <summary>The transmit waveform now, or null on a modem with no ladder.</summary>
    public int? CurrentWaveform => _ladder.CurrentOrNull;

    /// <summary>The waveform ladder this session is stepping, disabled or not.</summary>
    public WaveformLadder Ladder => _ladder;

    /// <summary>True while a backoff is waiting for somebody else to stop transmitting.</summary>
    public bool WaitingForChannel => _waitingForChannel;

    /// <summary>
    /// True while this session has something to put on air, or is putting it there.
    /// </summary>
    /// <remarks>
    /// An acknowledgement is queued the instant the line it answers is decoded, inside the far
    /// station's own transmit, and goes out from the pump a moment later. Between those two
    /// points this session owes the other end an answer, and this says so. A test driving a
    /// clock of its own must not move time across that gap, or it fires the sender's ack
    /// timeout against an acknowledgement that was already on its way.
    /// </remarks>
    public bool Sending => Volatile.Read(ref _owed) > 0;

    /// <summary>True between <see cref="Start"/> and disposal.</summary>
    public bool IsRunning => _started && !_disposed;

    /// <summary>What the conversation has cost so far.</summary>
    public ChatStats Stats => new(
        Volatile.Read(ref _sent),
        Volatile.Read(ref _delivered),
        Volatile.Read(ref _failed),
        Volatile.Read(ref _retries),
        Volatile.Read(ref _received),
        Volatile.Read(ref _duplicates));

    /// <summary>
    /// Starts listening, starts the transmit pump, and says hello.
    /// </summary>
    /// <remarks>The station must already be started; this session does not own it.</remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _station.FrameReceived += OnFrameReceived;
        _pump = Task.Run(PumpAsync);
        if (_station.CanTransmit)
        {
            Post(_station.Frame(LinkFrameType.Hello, SessionId), completion: null);
        }
    }

    /// <summary>
    /// Says hello again, so the other end can put our callsign in its status bar.
    /// </summary>
    /// <returns>A task that completes when the frame has left the transmitter.</returns>
    public Task SendHelloAsync(CancellationToken cancellationToken = default)
    {
        RequireRunningAndTransmitCapable();
        var completion = new TaskCompletionSource<(TimeSpan Air, long Left)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_station.Frame(LinkFrameType.Hello, SessionId), completion);
        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Sends one line and waits for it to be acknowledged, retrying until it is or until the
    /// attempts run out.
    /// </summary>
    /// <param name="text">The line. Control characters are stripped before it goes out.</param>
    /// <param name="cancellationToken">Gives up on the line; the frames already sent stay sent.</param>
    /// <returns>Delivered with the attempt count and the round trip, or failed with the
    /// attempt count. Never throws for an unacknowledged line: not being heard is an outcome,
    /// not an error.</returns>
    /// <exception cref="ArgumentException">The line is empty or longer than
    /// <see cref="ChatOptions.MaxTextBytes"/>.</exception>
    /// <exception cref="InvalidOperationException">The session is not started, or the station
    /// cannot transmit.</exception>
    public async Task<ChatDelivery> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        RequireRunningAndTransmitCapable();
        string line = ChatPayload.Sanitise(text);
        if (line.Length == 0)
        {
            throw new ArgumentException("an empty line is not a message", nameof(text));
        }

        int bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes > _options.MaxTextBytes)
        {
            throw new ArgumentException(
                $"the line is {bytes} bytes and the limit is {_options.MaxTextBytes} - a chat "
                + "line is a line, and a long one belongs in a file transfer",
                nameof(text));
        }

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        await _oneLineAtATime.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await SendLineAsync(line, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _oneLineAtATime.Release();
        }
    }

    /// <summary>Stops the pump, stops listening, and abandons anything still queued.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _station.FrameReceived -= OnFrameReceived;
        }

        _ladder.Changed -= OnWaveformChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _outbox.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
        _oneLineAtATime.Dispose();
    }

    /// <summary>One line, from the first attempt to the last.</summary>
    private async Task<ChatDelivery> SendLineAsync(string line, CancellationToken cancellationToken)
    {
        byte seq = _nextSeq++;
        Interlocked.Increment(ref _sent);
        int attempts = 1 + _options.MaxRetries;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                Interlocked.Increment(ref _retries);
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }

            int? waveform = _ladder.CurrentOrNull;
            var pending = new Pending(seq, line, attempt, waveform);

            // Registered before the frame goes out, never after. On a link whose receive path
            // runs on the transmitting thread - the in-process test rig, and any device whose
            // capture callback is fast - the acknowledgement can be decoded before the send
            // call has even returned, and an ARQ that registered afterwards would wait out a
            // timeout for an answer it had already been given.
            _pending = pending;
            try
            {
                byte[] payload = ChatPayload.Encode(seq, waveform, line);
                LinkFrame frame = _station.Frame(LinkFrameType.Chat, SessionId, payload);
                (TimeSpan air, long left) = await TransmitAsync(frame, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await pending.Acknowledged.Task
                        .WaitAsync(AckTimeoutFor(air), _time, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) when (pending.AcknowledgedAt is not null)
                {
                    // The answer was already in hand. A timeout and the acknowledgement it is
                    // waiting for can land at the same moment, and the wait is then decided by
                    // which continuation the machine happens to run first, which is not a fact
                    // about the link at all. An answer this station has already decoded beats
                    // its own stopwatch; retransmitting a line the far end has acknowledged
                    // costs air time and reports a retry that never needed to happen.
                }

                // Stamped in the receive handler on both the ordinary path and the one above,
                // so the fallback is only there to keep this total: an answer with no time on
                // it is one that cost nothing but the air time.
                long answered = pending.AcknowledgedAt ?? left;

                OnDelivered(attempt);
                return ChatDelivery.Delivered(seq, attempt, air + RoundTripFrom(left, answered));
            }
            catch (TimeoutException)
            {
                // No acknowledgement, or the channel never cleared for this attempt. Both are
                // the same fact for the ladder: the far end did not answer.
                OnAttemptFailed(seq, attempt, waveform);
            }
            finally
            {
                _pending = null;
            }
        }

        Interlocked.Increment(ref _failed);
        return ChatDelivery.Failed(seq, attempts);
    }

    /// <summary>
    /// How long the answer took, from the moment the line left the transmitter to the moment
    /// the acknowledgement was decoded.
    /// </summary>
    /// <remarks>
    /// Both ends of it are stamped where the thing happened - in the transmit pump and in the
    /// receive handler - and not where this task noticed. A figure taken when the waiting task
    /// is next given a thread measures the machine's queue as well as the link, and on a busy
    /// box the machine's queue is the larger of the two. Never negative: the two stamps come
    /// from different threads and a clock a caller drives can move between them.
    /// </remarks>
    private TimeSpan RoundTripFrom(long left, long answered)
    {
        TimeSpan trip = _time.GetElapsedTime(left, answered);
        return trip < TimeSpan.Zero ? TimeSpan.Zero : trip;
    }

    /// <summary>
    /// The patience for one attempt: the caller's fixed figure, or the base plus the air time
    /// the burst that has just gone out actually took.
    /// </summary>
    private TimeSpan AckTimeoutFor(TimeSpan airTime)
    {
        if (_options.AckTimeout is TimeSpan fixedTimeout)
        {
            return fixedTimeout;
        }

        TimeSpan derived = _options.AckTimeoutBase + airTime;
        return derived > _options.MaxAckTimeout ? _options.MaxAckTimeout : derived;
    }

    /// <summary>
    /// Waits for the channel, then for a random whole number of backoff slots.
    /// </summary>
    /// <remarks>
    /// The station underneath waits for a clear channel too, and refuses to transmit if it
    /// never comes. This wait is the polite half of the same rule: it is over before the frame
    /// is even queued, so a retry does not sit at the head of the transmit queue holding up an
    /// acknowledgement somebody else is waiting on.
    /// </remarks>
    private async Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        _waitingForChannel = true;
        try
        {
            DateTimeOffset deadline = _time.GetUtcNow() + _options.BusyWaitTimeout;
            while (_station.Busy && _time.GetUtcNow() < deadline)
            {
                await Task.Delay(_options.BusyPollInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _waitingForChannel = false;
        }

        int slots = _random.Next(1, Math.Min(attempt, _options.MaxBackoffSlots) + 1);
        TimeSpan wait = _options.BackoffSlot * slots;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Queues a frame and waits for it to leave the transmitter, timing it.</summary>
    /// <returns>How long the transmission took, which is the burst's air time on a real device,
    /// and the timestamp at which it finished - stamped in the pump, so that what follows can
    /// time an answer from when the frame left rather than from when this task woke up.</returns>
    private async Task<(TimeSpan Air, long Left)> TransmitAsync(
        LinkFrame frame, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<(TimeSpan, long)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Post(frame, completion);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Post(LinkFrame frame, TaskCompletionSource<(TimeSpan Air, long Left)>? completion)
    {
        // Counted here rather than asked of the channel: an unbounded channel with a single
        // reader does not support being counted, and this has to be true from the moment the
        // frame is posted anyway. See Sending.
        Interlocked.Increment(ref _owed);
        if (!_outbox.Writer.TryWrite(new Outbound(frame, completion)))
        {
            Interlocked.Decrement(ref _owed);
            completion?.TrySetCanceled();
        }
    }

    /// <summary>
    /// The transmit pump: one frame at a time, off the receive thread.
    /// </summary>
    private async Task PumpAsync()
    {
        CancellationToken token = _stopping.Token;
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out Outbound? item))
                {
                    await SendOneAsync(item, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (_outbox.Reader.TryRead(out Outbound? abandoned))
            {
                Interlocked.Decrement(ref _owed);
                abandoned.Completion?.TrySetCanceled();
            }
        }
    }

    private async Task SendOneAsync(Outbound item, CancellationToken token)
    {
        long started = _time.GetTimestamp();
        try
        {
            await _station.SendAsync(item.Frame, token).ConfigureAwait(false);
            item.Completion?.TrySetResult((_time.GetElapsedTime(started), _time.GetTimestamp()));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            item.Completion?.TrySetCanceled(token);
            throw;
        }
        catch (Exception failure)
        {
            // One frame that would not go out must not take the pump with it: the next one
            // may well go, and an ARQ whose acknowledgements had quietly stopped being sent
            // would look to both ends like a dead band.
            if (item.Completion is null)
            {
                TransmitFailed?.Invoke(failure);
            }
            else
            {
                item.Completion.TrySetException(failure);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _owed);
        }
    }

    /// <summary>Every link frame this station heard. Raised on the receive thread.</summary>
    private void OnFrameReceived(LinkFrame frame, FrameQuality quality)
    {
        if (_disposed
            || string.Equals(frame.Source, _station.Callsign, StringComparison.Ordinal)
            || (_options.Correspondent is string only
                && !string.Equals(frame.Source, only, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        switch (frame.Type)
        {
            case LinkFrameType.Hello:
                CorrespondentSeen?.Invoke(frame.Source);
                break;

            case LinkFrameType.Chat:
                OnChat(frame, quality);
                break;

            case LinkFrameType.ChatAck:
                OnChatAck(frame);
                break;

            default:
                // Somebody else's activity on the same channel - a file transfer, a perf run.
                // Monitor shows it; this conversation has no opinion about it.
                break;
        }
    }

    private void OnChat(LinkFrame frame, FrameQuality quality)
    {
        if (!ChatPayload.TryDecode(frame.Payload.Span, out byte seq, out int? waveform, out string text))
        {
            return;
        }

        // Acknowledged first and always, duplicate or not: the far station is retrying because
        // it did not hear us, and telling it again is the only thing that stops it.
        if (_station.CanTransmit)
        {
            Post(_station.Frame(LinkFrameType.ChatAck, frame.Session, ChatPayload.EncodeAck(seq)), completion: null);
        }

        if (!_seen.AddIfNew(frame.Source, frame.Session, seq))
        {
            Interlocked.Increment(ref _duplicates);
            return;
        }

        Interlocked.Increment(ref _received);

        // Delivered even when the modem marked it monitor-only (plain IL2P on an IL2P+CRC
        // link, standing on Reed-Solomon alone). The station did hear the line, so the
        // operator should see it, and the quality travels with it for the UI to badge.
        MessageReceived?.Invoke(new ChatMessage(
            frame.Source, frame.Session, seq, text, waveform, quality, _time.GetUtcNow()));
    }

    private void OnChatAck(LinkFrame frame)
    {
        if (frame.Session != SessionId
            || !ChatPayload.TryDecodeAck(frame.Payload.Span, out byte seq))
        {
            return;
        }

        Pending? pending = _pending;
        if (pending is not null && pending.Seq == seq && pending.Answer(_time.GetTimestamp()))
        {
            pending.Acknowledged.TrySetResult(true);
        }
    }

    private void OnDelivered(int attempt)
    {
        Interlocked.Increment(ref _delivered);
        _consecutiveFailures = 0;
        if (attempt > 1)
        {
            // A line that needed a retry is not evidence of a link with room to spare.
            _consecutiveCleanDeliveries = 0;
            return;
        }

        _consecutiveCleanDeliveries++;
        if (_options.StepWaveform
            && _consecutiveCleanDeliveries >= _options.StepUpAfter
            && _ladder.TryStepUp($"{_consecutiveCleanDeliveries} lines delivered first time"))
        {
            _consecutiveCleanDeliveries = 0;
        }
    }

    private void OnAttemptFailed(byte seq, int attempt, int? waveform)
    {
        _consecutiveCleanDeliveries = 0;
        _consecutiveFailures++;
        AttemptFailed?.Invoke(new ChatAttempt(seq, attempt, waveform));
        if (_options.StepWaveform
            && _consecutiveFailures >= _options.StepDownAfter
            && _ladder.TryStepDown($"{_consecutiveFailures} attempts unacknowledged"))
        {
            _consecutiveFailures = 0;
        }
    }

    private void OnWaveformChanged(int waveform, string reason) =>
        WaveformChanged?.Invoke(waveform, reason);

    private void RequireRunningAndTransmitCapable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new InvalidOperationException("call Start() before sending: nothing is listening yet");
        }

        if (!_station.CanTransmit)
        {
            throw new InvalidOperationException(
                $"'{_station.DeviceName}' is a receive-only device - this station can read a "
                + "QSO and cannot join one");
        }
    }

    /// <summary>A frame waiting its turn on the transmit pump.</summary>
    private sealed record Outbound(
        LinkFrame Frame, TaskCompletionSource<(TimeSpan Air, long Left)>? Completion);

    /// <summary>The line in flight and the acknowledgement it is waiting for.</summary>
    private sealed class Pending(byte seq, string text, int attempt, int? waveform)
    {
        private long _acknowledgedAt;
        private int _claimed;
        private int _answered;

        public byte Seq { get; } = seq;

        public TaskCompletionSource<bool> Acknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The timestamp at which the acknowledgement was decoded, or null while none has been.
        /// </summary>
        /// <remarks>
        /// Set in the receive handler, before the waiting task is woken, so that "has it been
        /// answered" is a fact from the moment the frame arrives rather than from the moment
        /// something got round to noticing. The task's own completion is a poor substitute:
        /// its continuation is queued, and a machine with nothing spare can leave it queued
        /// for longer than the patience it is racing.
        /// </remarks>
        public long? AcknowledgedAt => Volatile.Read(ref _answered) == 0 ? null : Volatile.Read(ref _acknowledgedAt);

        /// <summary>Records the arrival, once; returns whether this call was the first.</summary>
        /// <remarks>The stamp is written before the flag that publishes it, so a reader that
        /// sees the flag sees the time that goes with it and never a zero.</remarks>
        public bool Answer(long at)
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                return false;
            }

            Volatile.Write(ref _acknowledgedAt, at);
            Volatile.Write(ref _answered, 1);
            return true;
        }

        public ChatOutstanding Snapshot() => new(Seq, text, attempt, waveform);
    }
}
