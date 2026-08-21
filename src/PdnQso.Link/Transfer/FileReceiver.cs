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

                if (_decoder is null)
                {
                    await Task.Delay(_options.PollInterval, _time, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_decoder.IsComplete)
                {
                    return await CompleteAsync(start, cancellationToken).ConfigureAwait(false);
                }

                DateTimeOffset now = _time.GetUtcNow();
                if (now - lastSymbol > _options.Patience)
                {
                    return Finish(
                        start, path: null,
                        reason: string.Create(
                            CultureInfo.InvariantCulture,
                            $"the sender stopped: no symbol for {(now - lastSymbol).TotalSeconds:0.#} s "
                            + $"with {_decoder.Decoded} of {_decoder.BlockCount} blocks decoded"));
                }

                if (statusAsked || now - lastStatus >= _options.StatusInterval)
                {
                    await SendStatusAsync(cancellationToken).ConfigureAwait(false);
                    lastStatus = _time.GetUtcNow();
                    statusAsked = false;
                }

                await Task.Delay(_options.PollInterval, _time, cancellationToken)
                    .ConfigureAwait(false);
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
        return _station.SendAsync(
            _station.Frame(LinkFrameType.FileStatus, _session, status.Encode()), cancellationToken);
    }

    /// <summary>
    /// Checks the decode against the offered CRC-32, writes the file, says Done, and goes on
    /// saying Done for a while in case the first one was lost.
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

        if (_station.CanTransmit)
        {
            var done = new FileDonePayload(_offer.FileId, _symbols);
            byte[] donePayload = done.Encode();
            await _station.SendAsync(
                _station.Frame(LinkFrameType.FileDone, _session, donePayload), cancellationToken)
                .ConfigureAwait(false);

            await LingerAsync(donePayload, cancellationToken).ConfigureAwait(false);
        }

        return Finish(start, path, reason: null);
    }

    /// <summary>
    /// Goes on answering for <see cref="FileTransferOptions.DoneLinger"/> after the file is on
    /// disc: a sender whose Done was eaten by the channel is still transmitting, and one more
    /// Done stops it far sooner than its patience would.
    /// </summary>
    private async Task LingerAsync(byte[] donePayload, CancellationToken cancellationToken)
    {
        if (_options.DoneLinger <= TimeSpan.Zero)
        {
            return;
        }

        DateTimeOffset until = _time.GetUtcNow() + _options.DoneLinger;
        while (_time.GetUtcNow() < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool askedAgain = false;
            while (_inbox.TryDequeue(out LinkFrame? frame))
            {
                if (frame.Session == _session
                    && frame.Type is LinkFrameType.FileSymbol or LinkFrameType.FileOffer)
                {
                    askedAgain = true;
                }
            }

            if (askedAgain)
            {
                await _station.SendAsync(
                    _station.Frame(LinkFrameType.FileDone, _session, donePayload), cancellationToken)
                    .ConfigureAwait(false);
            }

            await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private FileTransferResult Finish(DateTimeOffset start, string? path, string? reason)
    {
        var result = new FileTransferResult
        {
            Success = reason is null,
            Role = FileTransferRole.Receiver,
            FileId = _offer.FileId,
            Name = _offer.Name,
            Length = _offer.Length,
            BlockCount = _offer.BlockCount,
            BlockSize = _offer.BlockSize,
            Symbols = _symbols,
            Elapsed = _time.GetUtcNow() - start,
            Path = path,
            FailureReason = reason,
        };

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

    /// <summary>Queues a frame. Runs on the station's receive thread and does nothing else.</summary>
    private void OnFrameReceived(LinkFrame frame, FrameQuality quality)
    {
        if (frame.Type is LinkFrameType.FileOffer or LinkFrameType.FileSymbol)
        {
            _inbox.Enqueue(frame);
        }
    }
}
