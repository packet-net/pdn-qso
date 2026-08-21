using System.Globalization;
using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Transfer;
using PdnQso.Link.Fountain;
using PdnQso.Tests.Time;

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
        await using var rig = Rig.Build(AudioChannel.Clean);
        byte[] content = Content(512, seed: 1);

        var sender = new FileSender(rig.A, Fast(), idSeed: 4242, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, _directory, Fast(), timeProvider: rig.Clock);
        var senderProgress = new List<FileProgress>();
        var receiverProgress = new List<FileProgress>();
        sender.Progress += senderProgress.Add;
        receiver.Progress += receiverProgress.Add;

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("notes.txt", content, CancellationToken.None), receiver);
        // The receiver has its own linger to finish, on the same clock, so it is driven too.
        FileTransferResult received = await rig.RunAsync(receiving, receiver);

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

        // About a third of frames do not survive this: afsk1200-il2p's knee is at 4 dB in a
        // 3 kHz noise bandwidth, and the offers and status frames are eaten along with the
        // symbols, which is the point.
        var channel = new AudioChannel { SnrDb = 4.0, TailSamples = 8000 };
        await using var rig = Rig.Build(channel);
        byte[] content = Content(1280, seed: 2);

        FileTransferOptions options = Fast() with
        {
            StatusInterval = TimeSpan.FromSeconds(3),
            ListenInterval = TimeSpan.FromSeconds(3),
            PatienceIntervals = 40,
        };
        var sender = new FileSender(rig.A, options, idSeed: 99, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);
        int statuses = 0;
        rig.A.FrameReceived += (frame, _) =>
        {
            if (frame.Type == LinkFrameType.FileStatus)
            {
                statuses++;
            }
        };

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("payload.bin", content, CancellationToken.None), receiver);
        // The receiver has its own linger to finish, on the same clock, so it is driven too.
        FileTransferResult received = await rig.RunAsync(receiving, receiver);

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

        var receiver = new FileReceiver(rig.B, _directory, Fast(), timeProvider: rig.Clock);
        string? failure = null;
        receiver.Failed += reason => failure = reason;

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        await SendOfferAsync(rig.A, session: 0x11, offer, CancellationToken.None);
        for (int index = 0; index < encoder.BlockCount; index++)
        {
            await SendSymbolAsync(rig.A, session: 0x11, encoder, index, CancellationToken.None);
        }

        // The receiver has its own linger to finish, on the same clock, so it is driven too.
        FileTransferResult received = await rig.RunAsync(receiving, receiver);

        received.Success.Should().BeFalse();
        received.FailureReason.Should().Contain("CRC-32");
        received.Path.Should().BeNull();
        failure.Should().NotBeNull();
        Directory.Exists(_directory).Should().BeFalse("nothing at all should have been written");
    }

    [Fact]
    public async Task The_Receivers_Status_Frames_Drive_The_Senders_Stop()
    {
        await using var rig = Rig.Build(AudioChannel.Clean);

        // A station that answers the offer with "I already have all of it". The sender has no
        // business sending the file to somebody who says they have it, and this is what stops
        // it: a status, not a Done.
        var pretend = new PretendingReceiver(rig.B);
        Task pretending = pretend.RunAsync(CancellationToken.None);

        var sender = new FileSender(rig.A, Fast(), idSeed: 7, timeProvider: rig.Clock);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("big.bin", Content(BlockSize * 100, seed: 4), CancellationToken.None),
            alsoBusy: () => pretend.Busy);

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
        await using var rig = Rig.Build(AudioChannel.Clean);

        // Two blocks and a patience of fifteen status intervals, so that "more than the
        // systematic pass" is two symbols inside 1.5 s. The first cut asked for more than four
        // symbols inside 500 ms, which is the systematic pass of a four-block file plus one
        // listen gap: on an idle box it sent six and passed, and under the load of the rest of
        // the suite it sent exactly the four of the pass and failed. A frame costs about 50 ms
        // through this rig, so the claim is now clear of the noise by an order of magnitude
        // rather than by two frames.
        FileTransferOptions options = Fast() with { PatienceIntervals = 5 };
        var sender = new FileSender(rig.A, options, idSeed: 11, timeProvider: rig.Clock);
        string? failure = null;
        sender.Failed += reason => failure = reason;

        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("into-the-void.bin", Content(BlockSize * 2, seed: 5), CancellationToken.None));

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
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);
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

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        await SendOfferAsync(rig.A, session: 0x20, offer, CancellationToken.None);
        for (int index = 0; index < 3; index++)
        {
            await SendSymbolAsync(rig.A, session: 0x20, encoder, index, CancellationToken.None);
        }

        // Wait for the receiver to have acted on what it has, rather than for a tenth of a
        // second and a hope.
        await VirtualTime.WaitForAsync(() => !receiver.Busy);
        await SendOfferAsync(rig.A, session: 0x20, second, CancellationToken.None);
        await VirtualTime.WaitForAsync(() => !receiver.Busy);

        for (int index = 3; index < encoder.BlockCount; index++)
        {
            await SendSymbolAsync(rig.A, session: 0x20, encoder, index, CancellationToken.None);
        }

        // The receiver has its own linger to finish, on the same clock, so it is driven too.
        FileTransferResult received = await rig.RunAsync(receiving, receiver);

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
        await using var rig = Rig.Build(AudioChannel.Clean);

        FileTransferOptions options = Fast() with
        {
            StatusInterval = TimeSpan.FromSeconds(2),
            ListenInterval = TimeSpan.FromSeconds(2),
            PatienceIntervals = 5,
        };
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock)
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

        using var listen = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(listen.Token);
        var sender = new FileSender(rig.A, options, idSeed: 13, timeProvider: rig.Clock);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("too-big.bin", Content(512, seed: 8), CancellationToken.None), receiver);

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

        var receiver = new FileReceiver(rig.B, _directory, Fast(), timeProvider: rig.Clock);
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        var sender = new FileSender(rig.A, Fast(), idSeed: 17, timeProvider: rig.Clock);
        Task<FileTransferResult> sending = sender.SendAsync(
            "long.bin", Content(BlockSize * 400, seed: 9), cancel.Token);

        // Cancel once the transfer is genuinely under way, which is a fact about the rig and
        // not a length of time somebody guessed would be enough on this machine.
        await VirtualTime.WaitForAsync(() => rig.Crossings >= 3);
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
        var sender = new FileSender(rig.A, Fast(), timeProvider: rig.Clock);

        Func<Task> send = () => sender.SendAsync("empty.bin", ReadOnlyMemory<byte>.Empty);

        await send.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Intervals scaled to what this link actually costs. A frame of one block takes about a
    /// second on air at 1200 baud, and the rig now charges for that, so an interval of a couple
    /// of hundred milliseconds would have the sender giving up before its first symbol landed.
    /// These are the same shape as the shipped defaults, an order of magnitude quicker.
    /// </summary>
    private static FileTransferOptions Fast() => new()
    {
        BlockSize = BlockSize,
        StatusInterval = TimeSpan.FromSeconds(2),
        ListenInterval = TimeSpan.FromSeconds(2),
        OfferInterval = TimeSpan.FromSeconds(4),
        PollInterval = TimeSpan.FromMilliseconds(50),
        PatienceIntervals = 15,
        DoneLinger = TimeSpan.FromSeconds(2),
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

        /// <summary>The clock both stations run on.</summary>
        public VirtualClock Clock { get; } = new();

        /// <summary>True while a burst is in the air.</summary>
        public bool Carrying => _link.Carrying;

        /// <summary>A number that changes whenever a burst crosses.</summary>
        public long Crossings => _link.Crossings;

        /// <summary>
        /// Lets the clock run until a transfer finishes, moving it on only while nothing is
        /// happening.
        /// </summary>
        /// <param name="work">The transfer under test.</param>
        /// <param name="answering">Whatever owes the other end an answer: a receiver has heard
        /// frames it has not acted on, and the clock must not be run past that or the sender
        /// times out against a status that was already on its way.</param>
        public Task<FileTransferResult> RunAsync(
            Task<FileTransferResult> work,
            FileReceiver? answering = null,
            Func<bool>? alsoBusy = null) =>
            VirtualTime.RunAsync(
                Clock,
                work,
                () => Carrying || answering?.Busy == true || alsoBusy?.Invoke() == true,
                progress: () => Crossings);

        /// <summary>The transmitting station, on the shared medium.</summary>
        public IStation A { get; private set; } = null!;

        /// <summary>The receiving station, on the shared medium.</summary>
        public IStation B { get; private set; } = null!;

        public static Rig Build(AudioChannel channel)
        {
            var rig = new Rig();
            var link = AudioLink.Create(Mode, channel);
            var medium = new HalfDuplexChannel();
            var a = new Station(
                new StationOptions { Callsign = "M0LTE-7", TxDelayMilliseconds = 100 },
                link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: rig.Clock);
            var b = new Station(
                new StationOptions { Callsign = "G0OLD-3", TxDelayMilliseconds = 100 },
                link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: rig.Clock);
            // Transmitting costs what it costs: the clock moves by each burst's own air time.
            // Without that a sender's patience is unreachable, because pouring symbols on this
            // rig is free and no amount of it brings a timeout measured in seconds any closer.
            link.Carried += rig.Clock.Advance;

            a.Start();
            b.Start();
            rig._link = link;
            rig._a = a;
            rig._b = b;
            rig._medium = medium;
            rig.A = medium.Wrap(a);
            rig.B = medium.Wrap(b);
            return rig;
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
        private readonly System.Threading.Channels.Channel<byte> _wake =
            System.Threading.Channels.Channel.CreateUnbounded<byte>();

        private FileOfferPayload _offer;
        private byte _session;
        private int _owed;

        public PretendingReceiver(IStation station)
        {
            _station = station;
            _station.FrameReceived += OnFrame;
        }

        /// <summary>
        /// True from the instant an offer is taken in until its answer has gone out.
        /// </summary>
        /// <remarks>
        /// The loop used to poll every five milliseconds of real time, which both put the wall
        /// clock back into the suite and left a gap in which the sender could be timed out
        /// against an answer that was already coming. It is woken by the frame itself now, and
        /// this says an answer is owed from the moment the frame is heard.
        /// </remarks>
        public bool Busy => Volatile.Read(ref _owed) > 0;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            try
            {
                await foreach (byte _ in _wake.Reader.ReadAllAsync(linked.Token))
                {
                    try
                    {
                        var status = new FileStatusPayload(
                            _offer.BlockCount, _offer.BlockCount, _offer.BlockCount);
                        await _station.SendAsync(
                            _station.Frame(LinkFrameType.FileStatus, _session, status.Encode()),
                            linked.Token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _owed);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _station.FrameReceived -= OnFrame;
                Interlocked.Exchange(ref _owed, 0);
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
                Interlocked.Increment(ref _owed);
                _wake.Writer.TryWrite(0);
            }
        }
    }
}
