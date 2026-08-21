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
/// and immediately whenever the sender re-sends its offer, which is how the sender asks. A
/// second offer while a transfer is running never restarts it: same file id, and it is a
/// request for a status; different file id, and it is ignored, because this station is busy.
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
    private readonly SemaphoreSlim _arrived = new(0);
    private int _sending;
    private int _working;

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
    /// True while this receiver has heard something it has not acted on yet, or is putting an
    /// answer on air.
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
    /// deciding would leave exactly the gap the flag exists to close. So the turn the loop
    /// takes counts as busy too, from the moment it starts draining the inbox until it parks
    /// again.
    /// </para>
    /// </remarks>
    public bool Busy =>
        !_inbox.IsEmpty || Volatile.Read(ref _working) > 0 || Volatile.Read(ref _sending) > 0;

    /// <summary>
    /// Waits for the next thing to do: a frame arriving, or the poll interval passing.
    /// </summary>
    /// <remarks>
    /// It was the interval alone, which meant a frame already in the inbox sat there until the
    /// next tick even though there was nothing to wait for. That is latency for free on the
    /// air, and worse than that for a test on a clock of its own: "a frame is queued" could
    /// not be taken to mean "an answer is coming", because the answer needed time to pass
    /// first, and time was exactly what such a test was holding back.
    /// </remarks>
    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        // Never park with something already in hand. The inbox is the truth about whether there
        // is work to do; the semaphore is only how a sleeping loop gets woken, and it cannot be
        // trusted on its own: when the poll wins the race below, the wait that loses is
        // cancelled, and a permit a sender had just released can be consumed by that abandoned
        // wait and lost. The frame then sits in the queue with nobody coming back for it until
        // the next tick. On the real clock that is a wasted poll interval; in a test holding
        // the clock still while anything says it is busy, it is a deadlock, and it hung a run
        // for twenty minutes before it was found.
        if (!_inbox.IsEmpty)
        {
            return;
        }

        using var settled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task woken = _arrived.WaitAsync(settled.Token);
        Task waited = Task.Delay(_options.PollInterval, _time, settled.Token);
        await Task.WhenAny(woken, waited).ConfigureAwait(false);
        await settled.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Puts one frame on air, counted so <see cref="Busy"/> can see it.</summary>
    private async Task SendOnAirAsync(LinkFrame frame, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sending);
        try
        {
            await _station.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _sending);
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

        _station.FrameReceived += OnFrameReceived;
        try
        {
            DateTimeOffset start = _time.GetUtcNow();
            DateTimeOffset lastStatus = start;
            DateTimeOffset lastSymbol = start;
            bool statusAsked = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileTransferResult? finished = null;

                // Raised for the whole turn, not only while the inbox has something in it:
                // see Busy. Put down again for anything that waits for time to pass, which is
                // the only kind of moment where this receiver has nothing in hand.
                Interlocked.Increment(ref _working);
                try
                {
                    while (_inbox.TryDequeue(out LinkFrame? frame))
                    {
                        switch (frame.Type)
                        {
                            case LinkFrameType.FileOffer:
                                if (HandleOffer(frame, ref start, ref lastStatus, ref lastSymbol))
                                {
                                    statusAsked = true;
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
                            else if (statusAsked || now - lastStatus >= _options.StatusInterval)
                            {
                                await SendStatusAsync(cancellationToken).ConfigureAwait(false);
                                lastStatus = _time.GetUtcNow();
                                statusAsked = false;
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _working);
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
        return SendOnAirAsync(
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
            var done = new FileDonePayload(_offer.FileId, _symbols);
            byte[] donePayload = done.Encode();
            await SendOnAirAsync(
                _station.Frame(LinkFrameType.FileDone, _session, donePayload), cancellationToken)
                .ConfigureAwait(false);

            await LingerAsync(donePayload, cancellationToken).ConfigureAwait(false);
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
    /// Called from inside the loop's own turn, and the turn's <see cref="Busy"/> flag is put
    /// down for the duration: waiting to hear whether anyone is still out there is a wait for
    /// time to pass, and a receiver that held itself busy through it would be waiting for a
    /// clock that was waiting for the receiver (design.md 6e). Each round of actual work inside
    /// it picks the flag back up.
    /// </para>
    /// </remarks>
    private async Task LingerAsync(byte[] donePayload, CancellationToken cancellationToken)
    {
        Interlocked.Decrement(ref _working);
        try
        {
            DateTimeOffset lastHeard = _time.GetUtcNow();
            while (_time.GetUtcNow() - lastHeard < _options.Patience)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool somebodyElse = false;
                Interlocked.Increment(ref _working);
                try
                {
                    bool askedAgain = false;
                    while (_inbox.TryDequeue(out LinkFrame? frame))
                    {
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
                        await SendOnAirAsync(
                            _station.Frame(LinkFrameType.FileDone, _session, donePayload),
                            cancellationToken)
                            .ConfigureAwait(false);

                        // Counted from the end of the answer rather than from the frame that
                        // prompted it: a half-duplex station hears nothing at all while it is
                        // transmitting, and charging its own air time to the sender's silence
                        // is how a link that is busy in both directions comes to look quiet.
                        lastHeard = _time.GetUtcNow();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _working);
                }

                if (somebodyElse)
                {
                    return;
                }

                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Increment(ref _working);
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

    /// <summary>Queues a frame. Runs on the station's receive thread and does nothing else.</summary>
    private void OnFrameReceived(LinkFrame frame, FrameQuality quality)
    {
        if (frame.Type is LinkFrameType.FileOffer or LinkFrameType.FileSymbol)
        {
            _inbox.Enqueue(frame);
            _arrived.Release();
        }
    }
}
