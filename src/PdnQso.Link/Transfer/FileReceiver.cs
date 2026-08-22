using System.Collections.Concurrent;
using System.Globalization;
using Packet.SoundModem.Modems;
using PdnQso.Link.Fountain;

namespace PdnQso.Link.Transfer;

/// <summary>
/// The receiving half of a file transfer: take the offer, decode whatever symbols arrive, say
/// how far along it is, and write the file when the CRC agrees that it is the right file.
/// </summary>
/// <remarks>
/// <para>
/// docs/design.md section 3. Offers are accepted by default; set <see cref="AcceptOffer"/> to
/// refuse one, in which case it is ignored and this goes on listening for the next.
/// </para>
/// <para>
/// The receiver reports "decoded n of K" every <see cref="FileTransferOptions.StatusInterval"/>
/// and whenever the sender re-sends its offer, which is how the sender asks. A second offer
/// while a transfer is running never restarts it: same file id, and it is a request for a
/// status; different file id, and it is ignored, because this station is busy.
/// </para>
/// <para>
/// <b>No answer of this station's goes out until the channel has been quiet for a beat.</b>
/// That is <see cref="AnswerHold"/>, and it applies to a status and to a Done alike: over a
/// half-duplex link the instant this receiver has something to say is the instant the sender is
/// transmitting, so an answer made at once is an answer neither station hears (issue #8).
/// </para>
/// <para>
/// A decoded file is written and reported at once, and the receiver then stays on the air
/// repeating its Done for as long as the sender is still audible rather than for a fixed span;
/// <see cref="LingerAsync"/> says why, at some length, because the length of that window is
/// what one lost frame at the end of a transfer costs.
/// </para>
/// <para>
/// <b>Threading.</b> Frames arrive on the station's receive thread and are queued, not acted
/// on; everything that decodes, writes or transmits happens on the loop inside
/// <see cref="ReceiveAsync"/>. That matters over a half-duplex link, where answering from
/// inside the frame handler would mean transmitting into the middle of the burst that
/// delivered it.
/// </para>
/// </remarks>
public sealed class FileReceiver
{
    private readonly IStation _station;
    private readonly string _directory;
    private readonly FileTransferOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<LinkFrame> _inbox = new();
    private TaskCompletionSource? _arrived;
    private int _working;
    private int _parked;
    private int _parkGeneration;

    private LtDecoder? _decoder;
    private FileOfferPayload _offer;
    private byte _session;
    private int _symbols;

    /// <summary>Builds a receiver over a station.</summary>
    /// <param name="station">The radio to listen on and answer through.</param>
    /// <param name="targetDirectory">Where received files are written; created if missing.</param>
    /// <param name="options">Status interval, patience and the rest; the defaults when
    /// omitted. The block size and fountain shape come from the sender's offer, not from
    /// here.</param>
    /// <param name="timeProvider">Wall clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are not usable.</exception>
    public FileReceiver(
        IStation station,
        string targetDirectory,
        FileTransferOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        _station = station;
        _directory = targetDirectory;
        _options = options ?? new FileTransferOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Decides whether to take an offer. Null accepts everything, which is the default: this
    /// is a tool two operators point at each other on purpose.
    /// </summary>
    public Func<FileOfferPayload, bool>? AcceptOffer { get; set; }

    /// <summary>
    /// True while this receiver has heard something it has not acted on yet, or has a turn of
    /// its own in hand: deciding what a frame meant, checking and writing the file, or putting
    /// an answer on air.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The queue side of this is true from the instant a frame is taken in, which happens
    /// inside the sender's own transmit. A test driving a clock of its own uses it to know that
    /// an answer is owed, so it does not move time on and time the sender out against a status
    /// or a Done that was already on its way.
    /// </para>
    /// <para>
    /// An empty inbox is not the end of the work. What the frames meant is worked out after
    /// they come off the queue - the last symbol is peeled, the file is checked and written,
    /// and only then does a Done go on air - and a receiver that let this drop while it was
    /// deciding would leave exactly the gap the flag exists to close. So the whole of a
    /// receiving run counts as busy, and the flag is put down only for the waits: the poll,
    /// which is over when the clock says so and would otherwise be waiting for a clock that
    /// was waiting for it.
    /// </para>
    /// <para>
    /// The wait it does put the flag down for ends where its timer fires or where a frame is
    /// taken in, and not where this receiver's loop is next given a thread. Those are the same
    /// instant on the wall clock and a long way apart on a clock a loaded test drives: with the
    /// flag coming back up in the continuation after the await, the settle loop went on moving
    /// time while the receiver's turn sat in the thread pool's queue, by nearly nine seconds of
    /// the protocol's time in the worst run measured, across a receiver that was about to
    /// answer (issue #20; the same fault <see cref="FileSender.Busy"/> had one level up, issue
    /// #18).
    /// </para>
    /// </remarks>
    public bool Busy =>
        !_inbox.IsEmpty
        || (Volatile.Read(ref _working) > 0 && Volatile.Read(ref _parked) == 0);

    /// <summary>
    /// Waits for the next thing to do: a frame arriving, or the poll interval passing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was the interval alone, which meant a frame already in the inbox sat there until the
    /// next tick even though there was nothing to wait for. That is latency for free on the
    /// air, and worse than that for a test on a clock of its own: "a frame is queued" could
    /// not be taken to mean "an answer is coming", because the answer needed time to pass
    /// first, and time was exactly what such a test was holding back.
    /// </para>
    /// <para>
    /// The poll has a timer of its own rather than a <see cref="Task.Delay(TimeSpan)"/> for
    /// the same reason <c>FileSender.ListenAsync</c> does: what <see cref="Busy"/> has to say
    /// is "the wait is over and a turn is owed", and the only place that is known at the
    /// instant it happens is the callback, which runs where the clock is moved and before
    /// whoever moved it can look again. The continuation after the await runs whenever the
    /// machine next gives it a thread, and on a loaded box the clock ran nine seconds past a
    /// receiver waiting for exactly that (issue #20). The timer is disposed on the way out, so
    /// a timer nobody is waiting on is not left in a test clock's queue moving time along.
    /// </para>
    /// </remarks>
    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        // Published before the inbox is checked, and completed by the frame handler after it
        // has enqueued: a frame either lands before the check below and is seen, or lands
        // after it and completes this, and there is no order in which it does neither. It used
        // to be a semaphore, and a semaphore's permit is a promise that can be broken: a
        // Release grants the permit to the head waiter through a queued completion, a waiter
        // being cancelled wins that race, and the permit evaporates with a frame already in
        // the queue. The receiver then parks with work in hand and nothing to wake it before
        // the next tick. On the real clock that is a wasted poll interval; in a test holding
        // the clock still while anything reads busy, it is a deadlock, because the frame keeps
        // Busy true, the settle loop therefore never moves the clock, and the tick that would
        // have rescued it never comes. That state was caught in a dump on a loaded run: one
        // frame queued, no permit, both waits pending, the settle loop spinning (issue #20).
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A full fence, not a plain write: the publish has to be visible before the look at
        // the inbox below is taken, and the handler puts the same fence between its enqueue
        // and its read of this field, so at least one side always sees the other.
        Interlocked.Exchange(ref _arrived, arrived);
        try
        {
            // Never park with something already in hand. The inbox is the truth about whether
            // there is work to do; the signal above covers everything that arrives after this
            // look.
            if (!_inbox.IsEmpty)
            {
                return;
            }

            var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // The generation pins the callback's write to this park and no other. A timer is
            // disposed on the way out, but a callback already past the disposed check can
            // still be in flight, and one landing after a later park's flag went up would read
            // as "the wait is over" for a wait that had not begun: the settle loop then never
            // moves the clock, and the receiver never comes off a timer that needs it moved.
            int generation = unchecked(_parkGeneration + 1);
            Volatile.Write(ref _parkGeneration, generation);

            using ITimer wake = _time.CreateTimer(
                _ =>
                {
                    if (Volatile.Read(ref _parkGeneration) == generation)
                    {
                        Volatile.Write(ref _parked, 0);
                    }

                    elapsed.TrySetResult();
                },
                state: null,
                _options.PollInterval,
                Timeout.InfiniteTimeSpan);
            using CancellationTokenRegistration stopped = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(), arrived);

            // The one kind of moment this receiver has nothing in hand, so the one kind of
            // moment a test's clock may move. The flag goes down only once the timer exists,
            // so a wait that is already over by the time the flag goes down is one this can
            // see and undo rather than one it races: the callback's own write cannot be the
            // earlier of the two.
            Volatile.Write(ref _parked, 1);
            if (elapsed.Task.IsCompleted || arrived.Task.IsCompleted)
            {
                // Over before it began: a frame or the stop arrived while the timer was being
                // created, or the air time of somebody's burst moved the clock past the whole
                // poll. Nothing is being waited for, so nothing may move the clock.
                Volatile.Write(ref _parked, 0);
            }

            try
            {
                await Task.WhenAny(arrived.Task, elapsed.Task).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _parked, 0);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Volatile.Write(ref _arrived, null);
        }
    }

    /// <summary>Raised for every offer heard, with whether it was accepted.</summary>
    public event Action<FileOfferPayload, bool>? OfferHeard;

    /// <summary>Raised for every symbol taken in.</summary>
    public event Action<FileProgress>? Progress;

    /// <summary>Raised once, when the file is decoded, checked and written.</summary>
    public event Action<FileTransferResult>? Completed;

    /// <summary>Raised once, with the reason, when the transfer gives up.</summary>
    public event Action<string>? Failed;

    /// <summary>The options this receiver was built with.</summary>
    public FileTransferOptions Options => _options;

    /// <summary>
    /// Waits for one file and receives it.
    /// </summary>
    /// <param name="cancellationToken">Stops listening.</param>
    /// <returns>What the transfer came to; a failure is a result, not an exception.</returns>
    /// <exception cref="OperationCanceledException">Cancelled while waiting or receiving.</exception>
    public async Task<FileTransferResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        _inbox.Clear();
        _decoder = null;
        _symbols = 0;
        Volatile.Write(ref _parked, 0);

        _station.FrameReceived += OnFrameReceived;

        // Work in hand for the whole run: see Busy. The poll puts it down for as long as it
        // waits, which is the only kind of moment this receiver has nothing in hand.
        Interlocked.Increment(ref _working);
        try
        {
            DateTimeOffset start = _time.GetUtcNow();
            DateTimeOffset lastStatus = start;
            DateTimeOffset lastSymbol = start;
            var status = new AnswerHold(_options, _time);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileTransferResult? finished = null;

                while (_inbox.TryDequeue(out LinkFrame? frame))
                {
                    // Anything at all off the inbox is proof the channel was in use a moment
                    // ago, whatever this station's DCD makes of it now.
                    status.Heard();
                    switch (frame.Type)
                    {
                        case LinkFrameType.FileOffer:
                            if (HandleOffer(frame, ref start, ref lastStatus, ref lastSymbol))
                            {
                                status.Owe();
                            }

                            break;

                        case LinkFrameType.FileSymbol when _decoder is not null
                            && frame.Session == _session:
                            if (HandleSymbol(frame, start))
                            {
                                lastSymbol = _time.GetUtcNow();
                            }

                            break;

                        default:
                            break;
                    }
                }

                if (_decoder is not null)
                {
                    if (_decoder.IsComplete)
                    {
                        finished = await CompleteAsync(start, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        DateTimeOffset now = _time.GetUtcNow();
                        if (now - lastSymbol > _options.Patience)
                        {
                            finished = Finish(
                                start, path: null,
                                reason: string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"the sender stopped: no symbol for {(now - lastSymbol).TotalSeconds:0.#} s "
                                    + $"with {_decoder.Decoded} of {_decoder.BlockCount} blocks decoded"));
                        }
                        else
                        {
                            if (now - lastStatus >= _options.StatusInterval)
                            {
                                status.Owe();
                            }

                            if (status.Ready(_station.Busy))
                            {
                                await SendStatusAsync(cancellationToken).ConfigureAwait(false);
                                lastStatus = _time.GetUtcNow();
                                status.Sent();
                            }
                        }
                    }
                }

                if (finished is { } result)
                {
                    return result;
                }

                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _working);
            _station.FrameReceived -= OnFrameReceived;
        }
    }

    /// <summary>
    /// Acts on an offer. Returns true when the sender is asking for a status rather than
    /// starting something.
    /// </summary>
    private bool HandleOffer(
        LinkFrame frame,
        ref DateTimeOffset start,
        ref DateTimeOffset lastStatus,
        ref DateTimeOffset lastSymbol)
    {
        if (!FileOfferPayload.TryDecode(frame.Payload.Span, out FileOfferPayload offer))
        {
            return false;
        }

        if (_decoder is not null)
        {
            // Busy. A repeat of the offer we are already working on is the sender asking how
            // far along we are; anything else is another station's transfer, and this receiver
            // does one at a time.
            OfferHeard?.Invoke(offer, false);
            return offer.FileId == _offer.FileId && frame.Session == _session;
        }

        bool accepted = AcceptOffer?.Invoke(offer) ?? true;
        OfferHeard?.Invoke(offer, accepted);
        if (!accepted)
        {
            return false;
        }

        _offer = offer;
        _session = frame.Session;
        _symbols = 0;
        _decoder = new LtDecoder(offer.BlockCount, offer.BlockSize, offer.Parameters);
        start = _time.GetUtcNow();
        lastStatus = start;
        lastSymbol = start;

        // Answer the offer at once rather than at the top of the next interval: the sender is
        // listening for exactly this before it decides what to send next.
        return true;
    }

    /// <summary>Feeds one symbol to the decoder. Returns true when it was one of ours.</summary>
    private bool HandleSymbol(LinkFrame frame, DateTimeOffset start)
    {
        if (!FileSymbolPayload.TryRead(frame.Payload.Span, out int index, out ReadOnlySpan<byte> symbol))
        {
            return false;
        }

        _symbols++;
        _decoder!.Add(index, symbol);
        Progress?.Invoke(new FileProgress(
            _offer.FileId, _offer.Name, FileTransferRole.Receiver, _symbols,
            _decoder.Decoded, _decoder.BlockCount, _decoder.BlockSize,
            _time.GetUtcNow() - start));
        return true;
    }

    /// <summary>
    /// Reports progress, unless this station cannot transmit at all. A web receiver can hear a
    /// transfer and decode it perfectly well; it simply cannot say so, and the sender's
    /// patience is what ends that transfer rather than a Done.
    /// </summary>
    private Task SendStatusAsync(CancellationToken cancellationToken)
    {
        if (!_station.CanTransmit)
        {
            return Task.CompletedTask;
        }

        var status = new FileStatusPayload(_decoder!.Decoded, _decoder.BlockCount, _symbols);
        return _station.SendAsync(
            _station.Frame(LinkFrameType.FileStatus, _session, status.Encode()), cancellationToken);
    }

    /// <summary>
    /// Checks the decode against the offered CRC-32, writes the file, says Done, and goes on
    /// saying Done for as long as the sender is still there in case the first one was lost.
    /// </summary>
    private async Task<FileTransferResult> CompleteAsync(
        DateTimeOffset start, CancellationToken cancellationToken)
    {
        byte[] decoded = _decoder!.Data;
        var content = new ReadOnlyMemory<byte>(decoded, 0, (int)_offer.Length);
        uint crc = Crc32.Compute(content.Span);
        if (crc != _offer.Crc32)
        {
            // Nothing is written. A decode that finished on the wrong bytes is exactly the
            // case where writing "most of a file" and calling it received is the worst thing
            // this could do.
            return Finish(
                start, path: null,
                reason: string.Create(
                    CultureInfo.InvariantCulture,
                    $"the decoded file does not match the offered CRC-32 "
                    + $"(0x{crc:X8} against 0x{_offer.Crc32:X8}); nothing was written"));
        }

        Directory.CreateDirectory(_directory);
        string path = SafeFileName.UniquePath(_directory, _offer.Name);
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);

        // Said the moment the file is on disc, rather than when the linger below is over. The
        // linger lasts as long as the far end does, which on a bad link is minutes; a receiver
        // that sat on the news for that long would be telling its operator about a transfer
        // that finished a long time ago, and the elapsed time it reported would be mostly
        // politeness. Nothing after this line changes the result.
        FileTransferResult result = Result(start, path, reason: null);
        Completed?.Invoke(result);

        if (_station.CanTransmit)
        {
            // The first Done is the linger's own first turn rather than a send of its own, so
            // that it waits for the channel exactly as every repeat of it does. Sending it
            // here instead is issue #8: this is the instant the sender is transmitting the
            // offer that ends its systematic pass, and neither station hears a word of the
            // other's.
            var done = new FileDonePayload(_offer.FileId, _symbols);
            await LingerAsync(done.Encode(), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Goes on answering after the file is on disc, until the sender has been quiet for a whole
    /// <see cref="FileTransferOptions.Patience"/>: a sender whose Done was eaten by the channel
    /// is still transmitting, and one more Done stops it far sooner than its own patience
    /// would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The window is measured from the last thing heard, not from the first Done.</b> It
    /// used to be a fixed span, twenty seconds by default, and that is issue #11: what it has
    /// to cover is a number of the sender's turns rather than a number of seconds. The sender
    /// can only hear anything at the end of a listen gap, once per status interval, so on a
    /// link where a frame is a second and an interval is fifteen, twenty seconds is about one
    /// chance; a channel that eats that too loses the transfer with the file already on disc,
    /// and the sender spends its whole patience pouring at a station that has finished and
    /// gone.
    /// </para>
    /// <para>
    /// A fixed number of status intervals - the other shape the issue offered - would still be
    /// a guess, because the thing it has to survive is the gap between one frame of the
    /// sender's arriving and the next, and that gap is not a constant. Measured over the
    /// channel rig at 1200 baud (the ladder in <c>DoneLingerLadderTests</c>): about two status
    /// intervals on a link losing one frame in fifty, which is the sender's own listening gap
    /// plus this receiver's answers; three when a quarter are going missing; sixteen and more
    /// when four in five are. Any span short enough to be reasonable on the good link is far
    /// too short on the bad one, and the bad one is the only place any of this matters.
    /// </para>
    /// <para>
    /// Silence for a whole patience is the same rule this receiver already applies on the way
    /// in ("no symbol for that long and the sender has stopped"), and it is the right length by
    /// construction rather than by choice: the sender's patience is exactly how long it may go
    /// on pouring. It costs nothing when the sender really has gone, because a station that
    /// hears nothing transmits nothing, and the operator was told the file had arrived before
    /// this started.
    /// </para>
    /// <para>
    /// One thing other than silence ends it: an offer belonging to another transfer. A window
    /// this long would otherwise be a window in which this station is deaf to the next file
    /// somebody wants to send it, which on a link where two operators are trying things is not
    /// a theoretical objection.
    /// </para>
    /// <para>
    /// <b>The first Done is this loop's own first turn.</b> It used to be sent from
    /// <see cref="CompleteAsync"/>, the instant the file was on disc, which is the instant the
    /// sender is transmitting the offer that ends its systematic pass: issue #8. Sending it
    /// from here instead means it waits for the channel exactly as every repeat of it does,
    /// and there is one rule about when this station answers rather than two.
    /// </para>
    /// <para>
    /// Called from inside the loop's own turn, and <see cref="Busy"/> stays up for everything
    /// in it except the polls themselves: waiting to hear whether anyone is still out there is
    /// a wait for time to pass, and a receiver that held itself busy through it would be
    /// waiting for a clock that was waiting for the receiver (design.md 6e). The poll puts the
    /// flag down for exactly as long as it waits, and its timer puts it back up.
    /// </para>
    /// </remarks>
    private async Task LingerAsync(byte[] donePayload, CancellationToken cancellationToken)
    {
        var hold = new AnswerHold(_options, _time);
        hold.Owe();
        bool answered = false;
        DateTimeOffset lastHeard = _time.GetUtcNow();
        while (hold.Owed || _time.GetUtcNow() - lastHeard < _options.Patience)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool somebodyElse = false;
            bool askedAgain = false;
            while (_inbox.TryDequeue(out LinkFrame? frame))
            {
                // Anything at all off the inbox is proof the channel was in use a moment ago,
                // whatever this station's DCD makes of it now.
                hold.Heard();
                if (frame.Session == _session)
                {
                    askedAgain |=
                        frame.Type is LinkFrameType.FileSymbol or LinkFrameType.FileOffer;
                }
                else if (frame.Type == LinkFrameType.FileOffer)
                {
                    // Somebody is offering something else. The file this receiver has
                    // is on disc and the station that sent it has either heard the Done
                    // or is about to run out of patience; going on repeating ourselves
                    // at it while a fresh transfer is being offered would miss the
                    // fresh one, which is the cost that would otherwise come with a
                    // window this long.
                    somebodyElse = true;
                }
            }

            if (askedAgain)
            {
                hold.Owe();
            }

            if (somebodyElse && answered)
            {
                // A fresh offer ends this window, but never before the first Done has gone
                // out: the file is on disc and the station that sent it is owed the news.
                return;
            }

            if (hold.Ready(_station.Busy))
            {
                await _station.SendAsync(
                    _station.Frame(LinkFrameType.FileDone, _session, donePayload),
                    cancellationToken)
                    .ConfigureAwait(false);

                // Counted from the end of the answer rather than from the frame that
                // prompted it: a half-duplex station hears nothing at all while it is
                // transmitting, and charging its own air time to the sender's silence
                // is how a link that is busy in both directions comes to look quiet.
                hold.Sent();
                answered = true;
                lastHeard = _time.GetUtcNow();
            }

            if (somebodyElse)
            {
                return;
            }

            await PauseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the result and tells whoever is listening what became of the transfer.</summary>
    private FileTransferResult Finish(DateTimeOffset start, string? path, string? reason)
    {
        FileTransferResult result = Result(start, path, reason);
        if (reason is null)
        {
            Completed?.Invoke(result);
        }
        else
        {
            Failed?.Invoke(reason);
        }

        return result;
    }

    /// <summary>What the transfer came to, raising nothing.</summary>
    private FileTransferResult Result(DateTimeOffset start, string? path, string? reason) =>
        new()
        {
            Success = reason is null,
            Role = FileTransferRole.Receiver,
            FileId = _offer.FileId,
            Name = _offer.Name,
            Length = _offer.Length,
            BlockCount = _offer.BlockCount,
            BlockSize = _offer.BlockSize,
            Symbols = _symbols,

            // The transfer's own time, ending when the file was written: the linger that
            // follows a success is this station being polite to the far end and is not part of
            // how long the file took.
            Elapsed = _time.GetUtcNow() - start,
            Path = path,
            FailureReason = reason,
        };

    /// <summary>
    /// When an answer this station owes may go on air.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiver's two answers - a status, and the Done it repeats - used to go out the
    /// instant there was something to say, and over a half-duplex link that is the one instant
    /// they cannot be heard. The sender pours symbols back to back and only stops once per
    /// status interval, so the moment the receiver has something to say is a moment the sender
    /// is transmitting: the answer lands inside the sender's own transmission, and each station
    /// misses the other's frame entirely. That is issue #8, and on the colliding rig it costs a
    /// clean two-block transfer four times its own air time and eight symbols it did not need.
    /// </para>
    /// <para>
    /// So an answer is <b>owed</b> when there is something to say and <b>ready</b> when the
    /// channel has been quiet for <see cref="FileTransferOptions.QuietBeforeAnswering"/>. On a
    /// half-duplex link the only quiet there is is the gap the sender leaves to listen in, so
    /// waiting for quiet is waiting for the gap, and an answer put into the gap is one the
    /// sender hears at the first opportunity it had to hear anything. It costs nothing on a
    /// link where nothing was colliding: the sender could not have heard the answer any sooner
    /// than the end of its own transmission either way.
    /// </para>
    /// <para>
    /// Quiet is the channel's own <see cref="IStation.Busy"/> - the modem's DCD and its
    /// in-band energy, which is what a person would look at - and a frame coming off the inbox,
    /// which is proof the channel was in use whatever the DCD says about it now. The beat has
    /// to be longer than the sender's own turnaround between two bursts, or the gap between
    /// them reads as quiet and the answer goes into the next one; a quarter of a second is
    /// several times a keyed transmitter's rise and settle and is a small fraction of any
    /// mode's frame time.
    /// </para>
    /// <para>
    /// <b>It gives up after one whole turn of the sender.</b> A channel some third station is
    /// sitting on is a channel that never reads quiet, and a receiver that waited for it would
    /// never answer at all; the sender would spend its whole patience pouring at a station that
    /// had the file. A sender pours for a status interval and then listens for a listen
    /// interval, so an answer held for both of those together has been offered a gap and has
    /// not found one, and the channel belongs to somebody else. Past that it goes out and takes
    /// its chances, which is what it used to do every time.
    /// </para>
    /// </remarks>
    /// <param name="options">Where the beat and the cap come from.</param>
    /// <param name="time">The clock.</param>
    private sealed class AnswerHold(FileTransferOptions options, TimeProvider time)
    {
        private DateTimeOffset _owedSince;
        private DateTimeOffset _quietSince = time.GetUtcNow();
        private bool _owed;

        /// <summary>True while an answer is owed and has not gone out.</summary>
        public bool Owed => _owed;

        /// <summary>There is something to say. Does nothing if there already was.</summary>
        public void Owe()
        {
            if (!_owed)
            {
                _owed = true;
                _owedSince = time.GetUtcNow();
            }
        }

        /// <summary>Something was heard, so the channel was in use at this instant.</summary>
        public void Heard() => _quietSince = time.GetUtcNow();

        /// <summary>Whether the answer owed may go on air now.</summary>
        /// <param name="channelBusy">What this station's DCD says.</param>
        public bool Ready(bool channelBusy)
        {
            DateTimeOffset now = time.GetUtcNow();
            if (channelBusy)
            {
                _quietSince = now;
            }

            if (!_owed)
            {
                return false;
            }

            return now - _quietSince >= options.QuietBeforeAnswering
                || now - _owedSince >= options.StatusInterval + options.ListenInterval;
        }

        /// <summary>The answer has gone out, and this station's own burst was the last thing
        /// on the channel.</summary>
        public void Sent()
        {
            _owed = false;
            _quietSince = time.GetUtcNow();
        }
    }

    /// <summary>Queues a frame. Runs on the station's receive thread and does nothing else.</summary>
    private void OnFrameReceived(LinkFrame frame, FrameQuality quality)
    {
        if (frame.Type is LinkFrameType.FileOffer or LinkFrameType.FileSymbol)
        {
            _inbox.Enqueue(frame);

            // After the enqueue, so that a loop this wakes finds the frame; and the park
            // publishes its signal with a fence before it looks at the inbox, so the frame is
            // either seen by that look or wakes this signal, with no order in which it does
            // neither. The fence here is the other half of that pair. Waking a park that has
            // already ended, or finding no park at all, costs nothing: the next park's look
            // at the inbox is what covers a frame that arrives between two of them.
            Interlocked.MemoryBarrier();
            Volatile.Read(ref _arrived)?.TrySetResult();
        }
    }
}
