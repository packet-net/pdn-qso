using System.Globalization;
using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Transfer;
using PdnQso.Link.Fountain;
using PdnQso.Tests.Time;
using PdnQso.Tests.Rig;

namespace PdnQso.Tests.Transfer;

/// <summary>
/// The file transfer of docs/design.md section 3, end to end: two stations, one channel, and a
/// file that either arrives intact or is refused.
/// </summary>
/// <remarks>
/// <para>
/// Both stations, the channel between them and the clock they share are
/// <see cref="TransferRig"/>. Nothing here is a statement about a modem; the claims are all
/// about the protocol.
/// </para>
/// <para>
/// Every interval is the clock's rather than the machine's, and transmitting costs its own air
/// time, so what a claim about patience or a linger means here is what it means on air.
/// </para>
/// </remarks>
/// <param name="output">Where the symbol counts are printed.</param>
public class FileTransferTests(ITestOutputHelper output) : IDisposable
{
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
        await using var rig = TransferRig.Build(AudioChannel.Clean);
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
        await using var rig = TransferRig.Build(channel);
        byte[] content = Content(1280, seed: 2);

        // Patience well past anything this transfer can need. What is measured here is that
        // repair symbols are sent and counted, and a bad link that costs sixty symbols at a
        // second of air time each takes a couple of minutes of the protocol's own time.
        FileTransferOptions options = Fast() with
        {
            StatusInterval = TimeSpan.FromSeconds(3),
            ListenInterval = TimeSpan.FromSeconds(3),
            PatienceIntervals = 200,
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

        // Half an hour of the protocol's time to play with. A kilobyte and a bit over a link
        // that eats a third of what crosses it really does take ten minutes of modelled time,
        // and how long it takes is not what this is measuring.
        TimeSpan budget = TimeSpan.FromMinutes(30);
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("payload.bin", content, CancellationToken.None), receiver, budget: budget);
        // The receiver has its own linger to finish, on the same clock, so it is driven too.
        FileTransferResult received = await rig.RunAsync(receiving, receiver, budget: budget);

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
        await using var rig = TransferRig.Build(AudioChannel.Clean);

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
        await using var rig = TransferRig.Build(AudioChannel.Clean);

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
        await using var rig = TransferRig.Build(AudioChannel.Clean);

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
        await using var rig = TransferRig.Build(AudioChannel.Clean);

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
        // Its window is the patience these options ask for, and a thirty second status interval
        // makes that seven and a half minutes of the clock's time, so the default budget of
        // five would run out in the middle of it.
        FileTransferResult received = await rig.RunAsync(
            receiving, receiver, budget: options.Patience + TimeSpan.FromMinutes(1));

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
        await using var rig = TransferRig.Build(AudioChannel.Clean);

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
        await using var rig = TransferRig.Build(AudioChannel.Clean);
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
    public async Task A_Fade_That_Eats_Every_Answer_For_Two_Of_The_Senders_Turns_Costs_Nothing()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);
        byte[] content = Content(BlockSize * 4, seed: 21);

        // A sender that pours for a turn and then listens, which is the shape of the shipped
        // defaults. The fade is two of those turns long: for thirty seconds the sender hears
        // nothing the receiver says, and the receiver has no way to know that except that the
        // symbols keep coming.
        FileTransferOptions options = Fast() with
        {
            StatusInterval = TimeSpan.FromSeconds(6),
            ListenInterval = TimeSpan.FromSeconds(10),
            OfferInterval = TimeSpan.FromSeconds(30),

            // Patient enough that the sender is still there for a good few turns after the fade
            // lifts. It is the fade against the linger that is under test, and a sender that
            // gave up first would be measuring the patience instead.
            PatienceIntervals = 15,
        };
        TimeSpan fade = TimeSpan.FromSeconds(30);
        await using var deaf = new DeafToDone(rig.A, rig.Clock, fade);

        var sender = new FileSender(deaf, options, idSeed: 23, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("through-the-fade.bin", content, CancellationToken.None), receiver);
        FileTransferResult received = await rig.RunAsync(receiving, receiver);

        output.WriteLine(
            $"{deaf.Eaten} of the receiver's {deaf.Dones} answers went unheard, "
            + $"and the transfer took {sent.Elapsed.TotalSeconds:0.#} s");

        // How many answers a fade this long swallows is a matter of when the receiver's loop
        // happens to look at its inbox, so the claim is the shape and not the count: the first
        // answer went unheard, which is where issue #11 starts, and the receiver was still
        // answering when the fade lifted, which is what it used not to be.
        deaf.Eaten.Should().BeGreaterThan(0, "the fade swallowed the first answer");
        deaf.Dones.Should().BeGreaterThan(
            deaf.Eaten, "the receiver was still answering when the fade lifted");
        // A success is a sender that was told, and nothing else: running out of patience is a
        // failure with a reason attached. How long it took is printed above rather than
        // asserted, because it depends on where in the sender's turn the fade happened to lift.
        sent.Success.Should().BeTrue(sent.FailureReason);
        received.Success.Should().BeTrue(received.FailureReason);
        File.ReadAllBytes(received.Path!).Should().Equal(content);
    }

    [Fact]
    public async Task The_File_Is_Reported_When_It_Is_Written_And_Not_When_The_Linger_Is_Over()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);
        FileTransferOptions options = Fast();
        var sender = new FileSender(rig.A, options, idSeed: 29, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);

        TimeSpan? decoded = null;
        receiver.Progress += p =>
        {
            if (decoded is null && p.Decoded == p.BlockCount)
            {
                decoded = rig.Clock.Elapsed;
            }
        };

        TimeSpan? told = null;
        receiver.Completed += _ => told = rig.Clock.Elapsed;

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        await rig.RunAsync(
            sender.SendAsync("prompt.bin", Content(BlockSize * 4, seed: 31), CancellationToken.None),
            receiver);
        FileTransferResult received = await rig.RunAsync(receiving, receiver);
        TimeSpan finished = rig.Clock.Elapsed;

        told.Should().NotBeNull();
        decoded.Should().NotBeNull();
        (told!.Value - decoded!.Value).Should().BeLessThan(
            options.StatusInterval,
            "the operator hears about the file when it is on disc, not when the far end has "
            + "finally gone quiet");

        // And the receiver did go on lingering afterwards, which is the whole point of telling
        // the operator first: on the shipped defaults that is another minute and a half of a
        // transfer the operator would otherwise think had stalled.
        (finished - told.Value).Should().BeGreaterThan(options.Patience);
        received.Elapsed.Should().BeLessThan(
            finished - options.Patience, "the linger is not part of how long the file took");
    }

    [Fact]
    public async Task Another_Station_Offering_A_File_Ends_The_Linger_Rather_Than_Waiting_It_Out()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);
        FileTransferOptions options = Fast();
        var sender = new FileSender(rig.A, options, idSeed: 37, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);

        TimeSpan? told = null;
        receiver.Completed += _ => told = rig.Clock.Elapsed;

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        await rig.RunAsync(
            sender.SendAsync("first.bin", Content(BlockSize * 4, seed: 41), CancellationToken.None),
            receiver);

        // The sender heard the Done and stopped, so the receiver is now sitting out its linger.
        receiving.IsCompleted.Should().BeFalse();

        byte[] next = Content(BlockSize * 2, seed: 43);
        var another = new LtParameters { Seed = 0x2468 };
        var encoder = new LtEncoder(next, BlockSize, another);
        await SendOfferAsync(
            rig.A,
            session: 0x63,
            new FileOfferPayload(
                0x1234, "next.bin", next.Length, encoder.BlockCount, BlockSize,
                Crc32.Compute(next), another),
            CancellationToken.None);

        FileTransferResult received = await rig.RunAsync(receiving, receiver);

        received.Success.Should().BeTrue(received.FailureReason);
        received.Name.Should().Be("first.bin");
        told.Should().NotBeNull();
        (rig.Clock.Elapsed - told!.Value).Should().BeLessThan(
            options.Patience,
            "a station with a file to send is not kept waiting while this one repeats itself");
    }

    [Fact]
    public async Task A_Sender_Refuses_To_Send_Nothing()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);
        var sender = new FileSender(rig.A, Fast(), timeProvider: rig.Clock);

        Func<Task> send = () => sender.SendAsync("empty.bin", ReadOnlyMemory<byte>.Empty);

        await send.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_Sender_Is_Busy_Except_In_The_Gap_It_Leaves_For_The_Receiver()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);
        FileTransferOptions options = Fast();
        var sender = new FileSender(rig.A, options, idSeed: 31, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);

        // Read from inside the sender's own progress event, which is raised once a symbol has
        // gone on air and before the next decision is made. The sender has work in hand at
        // that moment: it has not reached its listening gap, and a clock run past it gives the
        // far end's patience a head start on a station that is about to transmit again.
        var busyWhileSending = new List<bool>();
        sender.Progress += _ =>
        {
            lock (busyWhileSending)
            {
                busyWhileSending.Add(sender.Busy);
            }
        };

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("busy.bin", Content(512, seed: 31), CancellationToken.None),
            receiver,
            sending: sender);
        await rig.RunAsync(receiving, receiver);

        sent.Success.Should().BeTrue(sent.FailureReason);
        lock (busyWhileSending)
        {
            busyWhileSending.Should().NotBeEmpty();
            busyWhileSending.Should().AllBeEquivalentTo(
                true, "a sender between symbols has not finished, it is deciding what to send next");
        }

        sender.Busy.Should().BeFalse("the transfer is over and nothing is in hand");
    }

    [Fact]
    public async Task A_Receiver_Is_Busy_While_It_Works_Out_What_It_Heard()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);
        FileTransferOptions options = Fast();
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);

        // Read from inside the receiver's own progress event, which is raised after the symbol
        // has come off the inbox and before any answer has gone out. That is the moment the
        // flag used to be down: nothing queued and nothing transmitting, but the symbol still
        // being peeled and the file still to be checked and written. A clock driven past that
        // moment times the sender out against a Done that was on its way, and the transfer
        // costs symbols it did not need.
        var busyWhileDeciding = new List<bool>();
        receiver.Progress += _ =>
        {
            lock (busyWhileDeciding)
            {
                busyWhileDeciding.Add(receiver.Busy);
            }
        };

        using var stop = new CancellationTokenSource();
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(stop.Token);

        // One offer and one symbol, by hand, so that the inbox is provably empty by the time
        // the symbol is being worked on. A whole transfer would do as well most of the time
        // and not always: two symbols can be waiting at once, and then the queue alone would
        // hold the flag up and the claim would not have been tested.
        byte[] content = Content(options.BlockSize, seed: 7);
        var encoder = new LtEncoder(content, options.BlockSize, new LtParameters());
        var offer = new FileOfferPayload(
            0x0BADF00D, "one.bin", content.Length, encoder.BlockCount, encoder.BlockSize,
            Crc32.Compute(content), encoder.Parameters);
        await rig.A.SendAsync(rig.A.Frame(LinkFrameType.FileOffer, session: 0x21, offer.Encode()));

        byte[] body = new byte[FileSymbolPayload.HeaderLength + encoder.BlockSize];
        FileSymbolPayload.WriteHeader(body, 0);
        encoder.Symbol(0, body.AsSpan(FileSymbolPayload.HeaderLength));
        await rig.A.SendAsync(rig.A.Frame(LinkFrameType.FileSymbol, session: 0x21, body));

        await VirtualTime.WaitForAsync(() =>
        {
            lock (busyWhileDeciding)
            {
                return busyWhileDeciding.Count > 0;
            }
        });

        lock (busyWhileDeciding)
        {
            busyWhileDeciding.Should().AllBeEquivalentTo(
                true, "a receiver that has taken a frame in owes an answer until it has sent one");
        }

        await stop.CancelAsync();
        Func<Task> finish = () => receiving;
        await finish.Should().ThrowAsync<OperationCanceledException>();
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
        PollInterval = TimeSpan.FromMilliseconds(500),
        PatienceIntervals = 15,
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
    /// A station that does not hear the far end's Done frames for a while: the fade, or the
    /// answer that arrived while this station was transmitting and could hear nothing at all.
    /// </summary>
    /// <remarks>
    /// It sits between the station and the transfer rather than between the station and the
    /// air, so the frames are really sent and really cost their air time; what is modelled is
    /// this end not hearing them, which is what a fade does and what half duplex does.
    /// </remarks>
    private sealed class DeafToDone : IStation
    {
        private readonly IStation _inner;
        private readonly TimeProvider _time;
        private readonly TimeSpan _fade;
        private DateTimeOffset? _first;

        public DeafToDone(IStation inner, TimeProvider time, TimeSpan fade)
        {
            _inner = inner;
            _time = time;
            _fade = fade;
            _inner.FrameReceived += OnFrame;
        }

        public event Action<LinkFrame, FrameQuality>? FrameReceived;

        public event Action<byte[], FrameQuality>? RawFrameReceived
        {
            add => _inner.RawFrameReceived += value;
            remove => _inner.RawFrameReceived -= value;
        }

        public event Action<LinkFrame?, byte[]>? FrameTransmitted
        {
            add => _inner.FrameTransmitted += value;
            remove => _inner.FrameTransmitted -= value;
        }

        /// <summary>How many Done frames arrived while this station could not hear them.</summary>
        public int Eaten { get; private set; }

        /// <summary>How many Done frames arrived at all.</summary>
        public int Dones { get; private set; }

        public string Callsign => _inner.Callsign;

        public string Mode => _inner.Mode;

        public string DeviceName => _inner.DeviceName;

        public bool CanTransmit => _inner.CanTransmit;

        public bool Busy => _inner.Busy;

        public bool Transmitting => _inner.Transmitting;

        public PdnQso.Link.Devices.IPowerControl Power => _inner.Power;

        public IModem Modem => _inner.Modem;

        public void Start() => _inner.Start();

        public LinkFrame Frame(LinkFrameType type, byte session, ReadOnlySpan<byte> payload = default) =>
            _inner.Frame(type, session, payload);

        public Task SendAsync(LinkFrame frame, CancellationToken cancellationToken = default) =>
            _inner.SendAsync(frame, cancellationToken);

        public Task SendRawAsync(
            ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default) =>
            _inner.SendRawAsync(ax25Frame, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _inner.FrameReceived -= OnFrame;
            return ValueTask.CompletedTask;
        }

        private void OnFrame(LinkFrame frame, FrameQuality quality)
        {
            if (frame.Type == LinkFrameType.FileDone)
            {
                Dones++;
                _first ??= _time.GetUtcNow();
                if (_time.GetUtcNow() - _first.Value < _fade)
                {
                    Eaten++;
                    return;
                }
            }

            FrameReceived?.Invoke(frame, quality);
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
