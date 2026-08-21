using System.Globalization;
using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Transfer;
using PdnQso.Link.Fountain;

namespace PdnQso.Tests.Transfer;

/// <summary>
/// The file transfer of docs/design.md section 3, end to end: two stations, one channel, and a
/// file that either arrives intact or is refused.
/// </summary>
/// <remarks>
/// <para>
/// The mode is <c>afsk1200-il2p</c>: an ordinary IL2P+CRC packet mode, and one of the cheapest
/// in the catalogue to simulate at about five milliseconds of CPU per frame, so a whole
/// transfer runs in the time one 300 baud frame would take to modulate. Nothing here is a
/// statement about a modem; the claims are all about the protocol.
/// </para>
/// <para>
/// Wall-clock intervals are real and short rather than faked. A fake clock cannot drive this:
/// a burst crosses the hermetic link synchronously on the transmitting thread, so the two ends
/// have to genuinely take turns, and a clock nobody is advancing while a station is inside a
/// transmit would stop the other one answering.
/// </para>
/// </remarks>
/// <param name="output">Where the symbol counts are printed.</param>
public class FileTransferTests(ITestOutputHelper output) : IDisposable
{
    private const string Mode = "afsk1200-il2p";
    private const int BlockSize = 64;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pdn-qso-file-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_File_Crosses_A_Clean_Channel_In_Exactly_K_Symbols()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build(AudioChannel.Clean);
        byte[] content = Content(512, seed: 1);

        var sender = new FileSender(rig.A, Fast(), idSeed: 4242);
        var receiver = new FileReceiver(rig.B, _directory, Fast());
        var senderProgress = new List<FileProgress>();
        var receiverProgress = new List<FileProgress>();
        sender.Progress += senderProgress.Add;
        receiver.Progress += receiverProgress.Add;

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        FileTransferResult sent = await sender.SendAsync("notes.txt", content, cancel.Token);
        FileTransferResult received = await receiving;

        sent.Success.Should().BeTrue(sent.FailureReason);
        sent.Symbols.Should().Be(8, "K is 512 / 64, and a clean channel needs no repair");
        sent.RepairSymbols.Should().Be(0);

        received.Success.Should().BeTrue(received.FailureReason);
        received.Symbols.Should().Be(8);
        received.BlockCount.Should().Be(8);
        received.Name.Should().Be("notes.txt");
        received.Path.Should().NotBeNull();
        File.ReadAllBytes(received.Path!).Should().Equal(content);

        senderProgress.Should().HaveCount(8);
        senderProgress[^1].Role.Should().Be(FileTransferRole.Sender);
        receiverProgress.Should().HaveCount(8);
        receiverProgress[^1].Decoded.Should().Be(8);
        receiverProgress[^1].Fraction.Should().Be(1.0);
    }

    [Fact]
    public async Task A_Lossy_Channel_Costs_Repair_Symbols_And_The_Count_Is_Reported()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // About a third of frames do not survive this: afsk1200-il2p's knee is at 4 dB in a
        // 3 kHz noise bandwidth, and the offers and status frames are eaten along with the
        // symbols, which is the point.
        var channel = new AudioChannel { SnrDb = 4.0, TailSamples = 8000 };
        await using var rig = Rig.Build(channel);
        byte[] content = Content(1280, seed: 2);

        FileTransferOptions options = Fast() with
        {
            StatusInterval = TimeSpan.FromMilliseconds(250),
            ListenInterval = TimeSpan.FromMilliseconds(250),
            PatienceIntervals = 40,
        };
        var sender = new FileSender(rig.A, options, idSeed: 99);
        var receiver = new FileReceiver(rig.B, _directory, options);
        int statuses = 0;
        rig.A.FrameReceived += (frame, _) =>
        {
            if (frame.Type == LinkFrameType.FileStatus)
            {
                statuses++;
            }
        };

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        FileTransferResult sent = await sender.SendAsync("payload.bin", content, cancel.Token);
        FileTransferResult received = await receiving;

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"K = {received.BlockCount}, sender put {sent.Symbols} symbols on air, receiver took "
            + $"in {received.Symbols} ({received.RepairSymbols} beyond K), {statuses} status "
            + $"frames got back, {sent.Elapsed.TotalSeconds:0.0} s"));

        received.Success.Should().BeTrue(received.FailureReason);
        received.BlockCount.Should().Be(20);
        File.ReadAllBytes(received.Path!).Should().Equal(content);

        sent.Success.Should().BeTrue(sent.FailureReason);
        sent.Symbols.Should().BeGreaterThan(20, "a third of the symbols never arrived");
        received.RepairSymbols.Should().BeGreaterThan(
            0, "the blocks the channel ate came back as repair symbols");
        sender.ReceiverSymbols.Should().Be(
            received.Symbols, "the sender is told what the transfer actually cost");
        statuses.Should().BeGreaterThan(0, "the receiver reported its progress");
    }

    [Fact]
    public async Task A_File_Whose_Bytes_Do_Not_Match_The_Offered_Crc_Is_Refused_And_Not_Written()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build(AudioChannel.Clean);

        byte[] content = Content(512, seed: 3);
        byte[] corrupt = (byte[])content.Clone();
        corrupt[200] ^= 0xFF;

        // The offer tells the truth about the file; the symbols carry a block that has been
        // damaged. Every frame passes the modem's own CRC, so only the whole-file check can
        // catch this - which is exactly the case it exists for.
        var parameters = new LtParameters { Seed = 0x1234 };
        var encoder = new LtEncoder(corrupt, BlockSize, parameters);
        var offer = new FileOfferPayload(
            0x0BADF00D, "damaged.bin", content.Length, encoder.BlockCount, BlockSize,
            Crc32.Compute(content), parameters);

        var receiver = new FileReceiver(rig.B, _directory, Fast());
        string? failure = null;
        receiver.Failed += reason => failure = reason;

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        await SendOfferAsync(rig.A, session: 0x11, offer, cancel.Token);
        for (int index = 0; index < encoder.BlockCount; index++)
        {
            await SendSymbolAsync(rig.A, session: 0x11, encoder, index, cancel.Token);
        }

        FileTransferResult received = await receiving;

        received.Success.Should().BeFalse();
        received.FailureReason.Should().Contain("CRC-32");
        received.Path.Should().BeNull();
        failure.Should().NotBeNull();
        Directory.Exists(_directory).Should().BeFalse("nothing at all should have been written");
    }

    [Fact]
    public async Task The_Receivers_Status_Frames_Drive_The_Senders_Stop()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build(AudioChannel.Clean);

        // A station that answers the offer with "I already have all of it". The sender has no
        // business sending the file to somebody who says they have it, and this is what stops
        // it: a status, not a Done.
        var pretend = new PretendingReceiver(rig.B);
        Task pretending = pretend.RunAsync(cancel.Token);

        var sender = new FileSender(rig.A, Fast(), idSeed: 7);
        FileTransferResult sent = await sender.SendAsync(
            "big.bin", Content(BlockSize * 100, seed: 4), cancel.Token);

        sent.Success.Should().BeTrue(sent.FailureReason);
        sent.BlockCount.Should().Be(100);
        sent.Symbols.Should().BeLessThan(
            50, "a status saying K of K stops the sender long before the systematic pass ends");
        output.WriteLine($"the sender stopped after {sent.Symbols} of 100 symbols");

        await pretend.StopAsync();
        await pretending;
    }

    [Fact]
    public async Task The_Sender_Gives_Up_When_Nothing_Answers()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build(AudioChannel.Clean);

        // Two blocks and a patience of fifteen status intervals, so that "more than the
        // systematic pass" is two symbols inside 1.5 s. The first cut asked for more than four
        // symbols inside 500 ms, which is the systematic pass of a four-block file plus one
        // listen gap: on an idle box it sent six and passed, and under the load of the rest of
        // the suite it sent exactly the four of the pass and failed. A frame costs about 50 ms
        // through this rig, so the claim is now clear of the noise by an order of magnitude
        // rather than by two frames.
        FileTransferOptions options = Fast() with
        {
            StatusInterval = TimeSpan.FromMilliseconds(100),
            ListenInterval = TimeSpan.FromMilliseconds(100),
            OfferInterval = TimeSpan.FromMilliseconds(200),
            PatienceIntervals = 15,
        };
        var sender = new FileSender(rig.A, options, idSeed: 11);
        string? failure = null;
        sender.Failed += reason => failure = reason;

        FileTransferResult sent = await sender.SendAsync(
            "into-the-void.bin", Content(BlockSize * 2, seed: 5), cancel.Token);

        sent.Success.Should().BeFalse();
        sent.FailureReason.Should().Contain("no answer from the receiver");
        failure.Should().Be(sent.FailureReason);
        sent.Elapsed.Should().BeGreaterThan(options.Patience);
        sent.BlockCount.Should().Be(2);
        output.WriteLine($"the sender poured {sent.Symbols} symbols into the void");
        sent.Symbols.Should().BeGreaterThan(
            sent.BlockCount,
            "it kept pouring repair symbols while it waited, rather than stopping at the "
            + "end of the systematic pass");
    }

    [Fact]
    public async Task A_Second_Offer_With_The_Same_File_Id_Does_Not_Restart_The_Transfer()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build(AudioChannel.Clean);

        byte[] content = Content(384, seed: 6);
        var parameters = new LtParameters { Seed = 0xABCD };
        var encoder = new LtEncoder(content, BlockSize, parameters);
        var offer = new FileOfferPayload(
            0x5555, "first.bin", content.Length, encoder.BlockCount, BlockSize,
            Crc32.Compute(content), parameters);

        // Same file id, same session, a different name and a different file behind it. A
        // receiver that acted on this would throw away the transfer it is halfway through.
        var second = new FileOfferPayload(
            0x5555, "second.bin", 4096, 64, BlockSize, 0x99999999,
            new LtParameters { Seed = 0x1111 });

        // A status interval longer than the test so that the only status frames heard are the
        // ones an offer asked for.
        FileTransferOptions options = Fast() with { StatusInterval = TimeSpan.FromSeconds(30) };
        var receiver = new FileReceiver(rig.B, _directory, options);
        var offersHeard = new List<(FileOfferPayload Offer, bool Accepted)>();
        receiver.OfferHeard += (o, accepted) => offersHeard.Add((o, accepted));
        int statuses = 0;
        rig.A.FrameReceived += (frame, _) =>
        {
            if (frame.Type == LinkFrameType.FileStatus)
            {
                statuses++;
            }
        };

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        await SendOfferAsync(rig.A, session: 0x20, offer, cancel.Token);
        for (int index = 0; index < 3; index++)
        {
            await SendSymbolAsync(rig.A, session: 0x20, encoder, index, cancel.Token);
        }

        await Task.Delay(100, cancel.Token);
        await SendOfferAsync(rig.A, session: 0x20, second, cancel.Token);
        await Task.Delay(100, cancel.Token);

        for (int index = 3; index < encoder.BlockCount; index++)
        {
            await SendSymbolAsync(rig.A, session: 0x20, encoder, index, cancel.Token);
        }

        FileTransferResult received = await receiving;

        received.Success.Should().BeTrue(received.FailureReason);
        received.Name.Should().Be("first.bin", "the second offer was ignored");
        received.Length.Should().Be(content.Length);
        File.ReadAllBytes(received.Path!).Should().Equal(content);

        offersHeard.Should().HaveCount(2);
        offersHeard[0].Accepted.Should().BeTrue();
        offersHeard[1].Accepted.Should().BeFalse("this station was already busy");
        statuses.Should().BeGreaterThanOrEqualTo(
            2, "an offer is also how the sender asks for a status");
    }

    [Fact]
    public async Task An_Offer_The_Receiver_Refuses_Is_Never_Written()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build(AudioChannel.Clean);

        FileTransferOptions options = Fast() with
        {
            StatusInterval = TimeSpan.FromMilliseconds(100),
            ListenInterval = TimeSpan.FromMilliseconds(100),
            PatienceIntervals = 5,
        };
        var receiver = new FileReceiver(rig.B, _directory, options)
        {
            AcceptOffer = offer => offer.Length < 100,
        };
        var refused = new List<FileOfferPayload>();
        receiver.OfferHeard += (offer, accepted) =>
        {
            if (!accepted)
            {
                refused.Add(offer);
            }
        };

        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(listen.Token);
        var sender = new FileSender(rig.A, options, idSeed: 13);
        FileTransferResult sent = await sender.SendAsync(
            "too-big.bin", Content(512, seed: 8), cancel.Token);

        sent.Success.Should().BeFalse("the other end wanted nothing to do with it");
        refused.Should().NotBeEmpty();
        refused[0].Name.Should().Be("too-big.bin");
        Directory.Exists(_directory).Should().BeFalse();

        // The receiver is still listening, which is the right thing for a station that said no
        // to one offer: it has not stopped being a station.
        receiving.IsCompleted.Should().BeFalse();
        await listen.CancelAsync();
        Func<Task> finish = () => receiving;
        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_Cancelled_Transfer_Stops_At_Both_Ends()
    {
        await using var rig = Rig.Build(AudioChannel.Clean);
        using var cancel = new CancellationTokenSource();

        var receiver = new FileReceiver(rig.B, _directory, Fast());
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        var sender = new FileSender(rig.A, Fast(), idSeed: 17);
        Task<FileTransferResult> sending = sender.SendAsync(
            "long.bin", Content(BlockSize * 400, seed: 9), cancel.Token);

        await Task.Delay(60);
        await cancel.CancelAsync();

        Func<Task> send = () => sending;
        Func<Task> receive = () => receiving;
        await send.Should().ThrowAsync<OperationCanceledException>();
        await receive.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(_directory).Should().BeFalse();
    }

    [Fact]
    public async Task A_Sender_Refuses_To_Send_Nothing()
    {
        await using var rig = Rig.Build(AudioChannel.Clean);
        var sender = new FileSender(rig.A, Fast());

        Func<Task> send = () => sender.SendAsync("empty.bin", ReadOnlyMemory<byte>.Empty);

        await send.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static FileTransferOptions Fast() => new()
    {
        BlockSize = BlockSize,
        StatusInterval = TimeSpan.FromMilliseconds(200),
        ListenInterval = TimeSpan.FromMilliseconds(400),
        OfferInterval = TimeSpan.FromMilliseconds(400),
        PollInterval = TimeSpan.FromMilliseconds(5),
        PatienceIntervals = 15,
        DoneLinger = TimeSpan.FromMilliseconds(100),
    };

    private static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static Task SendOfferAsync(
        IStation station, byte session, FileOfferPayload offer, CancellationToken cancellationToken) =>
        station.SendAsync(
            station.Frame(LinkFrameType.FileOffer, session, offer.Encode()), cancellationToken);

    private static Task SendSymbolAsync(
        IStation station, byte session, LtEncoder encoder, int index, CancellationToken cancellationToken)
    {
        byte[] body = new byte[FileSymbolPayload.HeaderLength + encoder.BlockSize];
        FileSymbolPayload.WriteHeader(body, index);
        encoder.Symbol(index, body.AsSpan(FileSymbolPayload.HeaderLength));
        return station.SendAsync(
            station.Frame(LinkFrameType.FileSymbol, session, body), cancellationToken);
    }

    /// <summary>
    /// Two stations, one shared medium, and the temporary directory received files land in.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private AudioLink _link = null!;
        private Station _a = null!;
        private Station _b = null!;
        private HalfDuplexChannel _medium = null!;

        /// <summary>The transmitting station, on the shared medium.</summary>
        public IStation A { get; private set; } = null!;

        /// <summary>The receiving station, on the shared medium.</summary>
        public IStation B { get; private set; } = null!;

        public static Rig Build(AudioChannel channel)
        {
            var link = AudioLink.Create(Mode, channel);
            var medium = new HalfDuplexChannel();
            var a = new Station(
                new StationOptions { Callsign = "M0LTE-7", TxDelayMilliseconds = 100 },
                link.DeviceA, link.ModemA, OpenBusyGate.Instance);
            var b = new Station(
                new StationOptions { Callsign = "G0OLD-3", TxDelayMilliseconds = 100 },
                link.DeviceB, link.ModemB, OpenBusyGate.Instance);
            a.Start();
            b.Start();
            return new Rig
            {
                _link = link,
                _a = a,
                _b = b,
                _medium = medium,
                A = medium.Wrap(a),
                B = medium.Wrap(b),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await _a.DisposeAsync();
            await _b.DisposeAsync();
            _medium.Dispose();
            _link.Dispose();
        }
    }

    /// <summary>
    /// A station that answers any file offer with "decoded K of K" and never decodes anything.
    /// It exists to pin one claim on its own: what stops the sender is the receiver's report.
    /// </summary>
    private sealed class PretendingReceiver
    {
        private readonly IStation _station;
        private readonly CancellationTokenSource _stop = new();
        private FileOfferPayload _offer;
        private byte _session;
        private volatile bool _heard;

        public PretendingReceiver(IStation station)
        {
            _station = station;
            _station.FrameReceived += OnFrame;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    if (_heard)
                    {
                        _heard = false;
                        var status = new FileStatusPayload(
                            _offer.BlockCount, _offer.BlockCount, _offer.BlockCount);
                        await _station.SendAsync(
                            _station.Frame(LinkFrameType.FileStatus, _session, status.Encode()),
                            linked.Token);
                    }

                    await Task.Delay(5, linked.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _station.FrameReceived -= OnFrame;
            }
        }

        public async Task StopAsync() => await _stop.CancelAsync();

        private void OnFrame(LinkFrame frame, FrameQuality quality)
        {
            if (frame.Type == LinkFrameType.FileOffer
                && FileOfferPayload.TryDecode(frame.Payload.Span, out FileOfferPayload offer))
            {
                _offer = offer;
                _session = frame.Session;
                _heard = true;
            }
        }
    }
}
