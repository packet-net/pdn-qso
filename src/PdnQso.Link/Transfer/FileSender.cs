using System.Globalization;
using Packet.SoundModem.Modems;
using PdnQso.Link.Fountain;

namespace PdnQso.Link.Transfer;

/// <summary>
/// The transmitting half of a file transfer: offer, pour symbols, listen, stop when the other
/// end says it has the file or when it stops answering at all.
/// </summary>
/// <remarks>
/// <para>
/// docs/design.md section 3. The shape of a transfer is:
/// </para>
/// <list type="number">
/// <item><description>a <see cref="LinkFrameType.FileOffer"/> naming the file, its length, its
/// CRC-32, and the fountain's K, block size, seed and parameters;</description></item>
/// <item><description>the K systematic symbols, which on a channel that loses nothing are the
/// file itself and are enough;</description></item>
/// <item><description>a pause, with the offer re-sent to ask for a status, because a
/// half-duplex station hears nothing while it transmits;</description></item>
/// <item><description>repair symbols until the receiver's <see cref="LinkFrameType.FileDone"/>
/// arrives, or until it has said nothing for <see cref="FileTransferOptions.Patience"/>.</description></item>
/// </list>
/// <para>
/// The sender never learns which blocks were lost and never asks. That is the whole bargain of
/// a fountain code: no selective repeat, no block map, no state to resynchronise after a
/// dropout, and a status frame that is twelve bytes whatever the size of the file.
/// </para>
/// <para>
/// <b>Threading.</b> Frames arrive on whatever thread the station's receive path is on - over
/// the hermetic <c>AudioLink</c> that is the far station's transmitting thread, which is to say
/// re-entrantly, inside this class's own <c>SendAsync</c>. The frame handler therefore only
/// sets fields; everything that transmits happens on the transfer's own loop.
/// </para>
/// </remarks>
public sealed class FileSender
{
    private readonly IStation _station;
    private readonly FileTransferOptions _options;
    private readonly TimeProvider _time;
    private readonly Random _ids;

    private TaskCompletionSource _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte _session;
    private uint _fileId;
    private long _lastHeardTicks;
    private int _reportedDecoded;
    private int _reportedBlocks;
    private volatile bool _complete;
    private int _receiverSymbols;

    /// <summary>Builds a sender over a station.</summary>
    /// <param name="station">The radio to send through.</param>
    /// <param name="options">Block size, fountain shape and patience;
    /// the defaults when omitted.</param>
    /// <param name="timeProvider">Wall clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <param name="idSeed">Seeds the file id, session id and fountain seed a transfer picks.
    /// Null for a random one, which is what a station wants; a number makes a test repeatable.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are not usable.</exception>
    public FileSender(
        IStation station,
        FileTransferOptions? options = null,
        TimeProvider? timeProvider = null,
        int? idSeed = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        _station = station;
        _options = options ?? new FileTransferOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _ids = idSeed is null ? new Random() : new Random(idSeed.Value);
    }

    /// <summary>Raised for every symbol sent, and for every status the receiver returns.</summary>
    public event Action<FileProgress>? Progress;

    /// <summary>Raised once, when the receiver has the file.</summary>
    public event Action<FileTransferResult>? Completed;

    /// <summary>Raised once, with the reason, when the transfer gives up.</summary>
    public event Action<string>? Failed;

    /// <summary>The session id of the transfer that is running or has just run.</summary>
    public byte Session => _session;

    /// <summary>The file id of the transfer that is running or has just run.</summary>
    public uint FileId => _fileId;

    /// <summary>The options this sender was built with.</summary>
    public FileTransferOptions Options => _options;

    /// <summary>
    /// How many symbols the receiver said it took in, or 0 if it never said. The result's own
    /// count is what this end put on air; this is the other end's, which is the one that
    /// measures the fountain rather than the channel.
    /// </summary>
    public int ReceiverSymbols => Volatile.Read(ref _receiverSymbols);

    /// <summary>Sends a file from disc.</summary>
    /// <param name="path">The file to send.</param>
    /// <param name="cancellationToken">Stops the transfer.</param>
    /// <returns>What the transfer came to; a failure is a result, not an exception.</returns>
    /// <exception cref="OperationCanceledException">The transfer was cancelled.</exception>
    public Task<FileTransferResult> SendAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] content = File.ReadAllBytes(path);
        return SendAsync(Path.GetFileName(path), content, cancellationToken);
    }

    /// <summary>Sends some bytes under a name.</summary>
    /// <param name="name">The name to offer the file under.</param>
    /// <param name="content">The bytes; at least one.</param>
    /// <param name="cancellationToken">Stops the transfer.</param>
    /// <returns>What the transfer came to; a failure is a result, not an exception.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="content"/> is empty, or
    /// too long for the wire's 32-bit length.</exception>
    /// <exception cref="OperationCanceledException">The transfer was cancelled.</exception>
    public async Task<FileTransferResult> SendAsync(
        string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (content.Length == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content), "there is no such thing as sending no bytes");
        }

        var parameters = _options.Fountain with { Seed = (uint)_ids.Next(int.MinValue, int.MaxValue) };
        var encoder = new LtEncoder(content, _options.BlockSize, parameters);
        _session = (byte)_ids.Next(256);
        _fileId = (uint)_ids.Next(int.MinValue, int.MaxValue);
        _complete = false;
        _reportedDecoded = 0;
        _reportedBlocks = encoder.BlockCount;
        _receiverSymbols = 0;
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var offer = new FileOfferPayload(
            _fileId, name, content.Length, encoder.BlockCount, encoder.BlockSize,
            Crc32.Compute(content.Span), parameters);
        byte[] offerPayload = offer.Encode();

        DateTimeOffset start = _time.GetUtcNow();
        Volatile.Write(ref _lastHeardTicks, start.UtcTicks);

        _station.FrameReceived += OnFrameReceived;
        try
        {
            return await RunAsync(offer, offerPayload, encoder, start, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _station.FrameReceived -= OnFrameReceived;
        }
    }

    private async Task<FileTransferResult> RunAsync(
        FileOfferPayload offer,
        byte[] offerPayload,
        LtEncoder encoder,
        DateTimeOffset start,
        CancellationToken cancellationToken)
    {
        // One buffer for the whole transfer: the index goes in the front, the symbol in the
        // rest, and nothing here allocates per symbol except the frame the station builds.
        byte[] body = new byte[FileSymbolPayload.HeaderLength + encoder.BlockSize];

        await _station.SendAsync(
            _station.Frame(LinkFrameType.FileOffer, _session, offerPayload), cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset lastOffer = _time.GetUtcNow();

        DateTimeOffset lastListen = lastOffer;
        int sent = 0;
        int index = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_complete)
            {
                return Finish(offer, sent, start, path: null, reason: null);
            }

            TimeSpan silence = _time.GetUtcNow() - new DateTimeOffset(
                Volatile.Read(ref _lastHeardTicks), TimeSpan.Zero);
            if (silence > _options.Patience)
            {
                return Finish(
                    offer, sent, start, path: null,
                    reason: string.Create(
                        CultureInfo.InvariantCulture,
                        $"no answer from the receiver for {silence.TotalSeconds:0.#} s "
                        + $"({_reportedDecoded} of {_reportedBlocks} blocks at the last report)"));
            }

            if (_options.MaxSymbols > 0 && sent >= _options.MaxSymbols)
            {
                return Finish(
                    offer, sent, start, path: null,
                    reason: string.Create(
                        CultureInfo.InvariantCulture,
                        $"stopped at the {_options.MaxSymbols} symbol ceiling with "
                        + $"{_reportedDecoded} of {_reportedBlocks} blocks acknowledged"));
            }

            if (index > encoder.Layout.BlockCount * 64)
            {
                return Finish(
                    offer, sent, start, path: null,
                    reason: "the fountain ran dry: sixty-four times the file has gone out "
                        + "and the receiver still has not decoded it");
            }

            FileSymbolPayload.WriteHeader(body, index);
            encoder.Symbol(index, body.AsSpan(FileSymbolPayload.HeaderLength));
            await _station.SendAsync(
                _station.Frame(LinkFrameType.FileSymbol, _session, body), cancellationToken)
                .ConfigureAwait(false);
            sent++;
            Report(offer, sent, start);

            DateTimeOffset now = _time.GetUtcNow();
            bool endOfSystematicPass = index == encoder.BlockCount - 1;
            bool offerDue = now - lastOffer >= _options.OfferInterval;
            bool listenDue = now - lastListen >= _options.StatusInterval;
            index++;

            if (!endOfSystematicPass && !offerDue && !listenDue)
            {
                continue;
            }

            if (_complete)
            {
                return Finish(offer, sent, start, path: null, reason: null);
            }

            if (endOfSystematicPass || offerDue)
            {
                // Re-sending the offer is two things at once: a second chance for a receiver
                // that never heard the first one, and the request that makes it answer now
                // rather than at the top of its next status interval. At the end of the
                // systematic pass that is the difference between a clean transfer costing
                // exactly K symbols and costing K plus however many go out before the news
                // arrives.
                await _station.SendAsync(
                    _station.Frame(LinkFrameType.FileOffer, _session, offerPayload), cancellationToken)
                    .ConfigureAwait(false);
                lastOffer = _time.GetUtcNow();
            }

            // Stop transmitting once per status interval whether or not there was an offer to
            // send. A half-duplex receiver can only be heard in a gap, and a sender that never
            // leaves one would run out of patience listening to its own transmitter.
            await ListenAsync(cancellationToken).ConfigureAwait(false);
            lastListen = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Stops transmitting for a while so the receiver can be heard, returning early the moment
    /// it says it is done.
    /// </summary>
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        Task done = _done.Task;
        Task waited = Task.Delay(_options.ListenInterval, _time, cancellationToken);
        await Task.WhenAny(done, waited).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Report(FileOfferPayload offer, int sent, DateTimeOffset start) =>
        Progress?.Invoke(new FileProgress(
            offer.FileId, offer.Name, FileTransferRole.Sender, sent,
            Volatile.Read(ref _reportedDecoded), offer.BlockCount, offer.BlockSize,
            _time.GetUtcNow() - start));

    private FileTransferResult Finish(
        FileOfferPayload offer, int sent, DateTimeOffset start, string? path, string? reason)
    {
        var result = new FileTransferResult
        {
            Success = reason is null,
            Role = FileTransferRole.Sender,
            FileId = offer.FileId,
            Name = offer.Name,
            Length = offer.Length,
            BlockCount = offer.BlockCount,
            BlockSize = offer.BlockSize,
            // What this end put on air. What the receiver had to take in is a different and
            // usually smaller number, and it is in ReceiverSymbols when the receiver said.
            Symbols = sent,
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

    /// <summary>
    /// Reads the receiver's answers. Runs on the receive thread, re-entrantly with this
    /// class's own transmit, so it sets fields and does nothing else.
    /// </summary>
    private void OnFrameReceived(LinkFrame frame, FrameQuality quality)
    {
        if (frame.Session != _session)
        {
            return;
        }

        switch (frame.Type)
        {
            case LinkFrameType.FileStatus
                when FileStatusPayload.TryDecode(frame.Payload.Span, out FileStatusPayload status):
                Volatile.Write(ref _lastHeardTicks, _time.GetUtcNow().UtcTicks);
                Volatile.Write(ref _reportedDecoded, status.Decoded);
                Volatile.Write(ref _reportedBlocks, status.BlockCount);
                Volatile.Write(ref _receiverSymbols, status.Received);
                if (status.IsComplete)
                {
                    // A status saying "K of K" is a Done whose Done was lost. Stopping on it
                    // costs nothing and saves a transfer that would otherwise run to patience.
                    _complete = true;
                    _done.TrySetResult();
                }

                break;

            case LinkFrameType.FileDone
                when FileDonePayload.TryDecode(frame.Payload.Span, out FileDonePayload done)
                    && done.FileId == _fileId:
                Volatile.Write(ref _lastHeardTicks, _time.GetUtcNow().UtcTicks);
                Volatile.Write(ref _reportedDecoded, Volatile.Read(ref _reportedBlocks));
                Volatile.Write(ref _receiverSymbols, done.Symbols);
                _complete = true;
                _done.TrySetResult();
                break;

            default:
                break;
        }
    }

}
