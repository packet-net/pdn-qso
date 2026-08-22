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
            sender.SendAsync("notes.txt", content, CancellationToken.None), receiver,
            sending: sender);
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
            sender.SendAsync("payload.bin", content, CancellationToken.None), receiver,
            budget: budget, sending: sender);
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
            alsoBusy: () => pretend.Busy, sending: sender);

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
            sender.SendAsync("into-the-void.bin", Content(BlockSize * 2, seed: 5), CancellationToken.None),
            sending: sender);

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
            sender.SendAsync("too-big.bin", Content(512, seed: 8), CancellationToken.None), receiver,
            sending: sender);

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

        // Every answer the receiver put on air, counted where it left rather than where it
        // arrived. A count one short at the far end is two findings and not one - the receiver
        // answered a turn fewer, or it answered and the answer was not counted - and the
        // assertion below could not tell them apart until this was here (issue #18).
        int answered = 0;
        rig.B.FrameTransmitted += (frame, _) =>
        {
            if (frame?.Type == LinkFrameType.FileDone)
            {
                Interlocked.Increment(ref answered);
            }
        };

        // The sender is driven as well as the receiver. It has work in hand for everything but
        // its listening gap, and a clock moved across the rest of it runs ahead of the station
        // that is doing the asking: its own patience then comes due while the receiver is
        // still answering, and the receiver's linger ends because the sender really has been
        // silent for a whole patience of the clock's time. That is issue #18, and it is not a
        // small effect - driven with a gap injected after each transmission the clock ran
        // ninety seconds ahead of a sender mid-turn, and the transfer failed with "no answer
        // from the receiver for 106 s" and the file already on disc.
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("through-the-fade.bin", content, CancellationToken.None), receiver,
            sending: sender);
        FileTransferResult received = await rig.RunAsync(receiving, receiver);

        output.WriteLine(
            $"{deaf.Eaten} of the receiver's {deaf.Dones} answers went unheard, "
            + $"and the transfer took {sent.Elapsed.TotalSeconds:0.#} s");

        // How many answers a fade this long swallows is a matter of when the receiver's loop
        // happens to look at its inbox, so the claim is the shape and not the count: the first
        // answer went unheard, which is where issue #11 starts, and the receiver was still
        // answering when the fade lifted, which is what it used not to be.
        deaf.Dones.Should().Be(
            Volatile.Read(ref answered),
            "this channel loses nothing, so every Done the receiver put on air arrived and was "
            + "counted");
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
            receiver, sending: sender);
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
            receiver, sending: sender);

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
    public async Task A_Sender_Has_Work_In_Hand_Again_The_Moment_Its_Gap_Is_Over()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);

        // Nobody is listening, so this sender pours its systematic pass and then stops to
        // listen. Patient enough that the gap is a gap and not the end of the transfer.
        FileTransferOptions options = Fast() with { PatienceIntervals = 30 };
        var sender = new FileSender(rig.A, options, idSeed: 47, timeProvider: rig.Clock);

        using var stop = new CancellationTokenSource();
        Task<FileTransferResult> sending = sender.SendAsync(
            "gap.bin", Content(BlockSize * 2, seed: 53), stop.Token);

        // The gap is the only moment this sender has nothing in hand, so this is the fact that
        // it has reached one. Nothing else on the rig holds a timer, so the clock is standing
        // still by the time this returns.
        await VirtualTime.WaitForAsync(() => !sender.Busy && !rig.Carrying);

        // Moved by hand, exactly as the settle loop moves it, and read straight afterwards.
        // The claim is that the gap is over where its timer fires: a flag that waits for the
        // sender's own loop to be given a thread leaves the clock free to run on across a
        // station that is about to transmit, and under load it ran on by eighty seconds of the
        // protocol's time until the sender's patience came due against a receiver that was
        // still answering (issue #18).
        //
        // This states the rule rather than reproducing the failure, and it is worth saying
        // which: on a quiet box the runtime runs the sender's continuation inline inside the
        // advance below, so the flag is back up before it is read whichever way it is put back
        // up, and the shape this replaced passed here too. What caught that shape was the rate
        // under eight CPU burners, one run in sixty with everything else about the test right,
        // and a trace of that run showing a ten second gap that lasted forty-eight.
        rig.Clock.Advance(options.ListenInterval);
        sender.Busy.Should().BeTrue(
            "the listening gap ended with the timer, not with the thread pool");

        await stop.CancelAsync();
        Func<Task> finish = () => sending;
        await finish.Should().ThrowAsync<OperationCanceledException>();
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

    [Fact]
    public async Task A_Receiver_Has_Work_In_Hand_Again_The_Moment_Its_Poll_Comes_Due()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean);

        // A status interval the length of the poll, so that the tick this test fires by hand
        // is one with something to do: a status is owed the moment it lands, and a clock run
        // past it walks off across an answer the sender's patience is fed by.
        FileTransferOptions options = Fast() with { StatusInterval = TimeSpan.FromMilliseconds(500) };

        // The receiver's station can be held: its transmit path waits for the test before a
        // frame goes on air. Without this the assertion below is a race the test sometimes
        // loses, and it says nothing about the fix when it does: the tick's turn can drain,
        // answer and park again between the advance returning and the very next line, and
        // Busy then reads false because the work is done, not because it was dropped. Under
        // sixteen burners that was about one sample in twenty, every one of them with the
        // status already on air. Held, the turn cannot end before the assertion looks,
        // whichever way the machine schedules it.
        var held = new HoldableStation(rig.B);
        var receiver = new FileReceiver(held, _directory, options, timeProvider: rig.Clock);

        using var stop = new CancellationTokenSource();
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(stop.Token);

        // One offer and one symbol of a two-block file, by hand, so the transfer is mid-flight
        // with the decoder live and the inbox provably empty: the only thing left on the rig
        // is the receiver's own poll. The gate stays open through this, so the offer and the
        // symbol are answered normally.
        byte[] content = Content(options.BlockSize * 2, seed: 61);
        var encoder = new LtEncoder(content, options.BlockSize, new LtParameters());
        var offer = new FileOfferPayload(
            0x0D15EA5E, "half.bin", content.Length, encoder.BlockCount, encoder.BlockSize,
            Crc32.Compute(content), encoder.Parameters);
        await rig.A.SendAsync(rig.A.Frame(LinkFrameType.FileOffer, session: 0x2E, offer.Encode()));
        await SendSymbolAsync(rig.A, session: 0x2E, encoder, 0, CancellationToken.None);

        // The poll is the only moment this receiver has nothing in hand, so this is the fact
        // that it has reached one. Nothing else on the rig holds a timer, so the clock is
        // standing still by the time this returns, and the status the last turn sent means a
        // status falls due again exactly one poll from the park.
        await VirtualTime.WaitForAsync(() => !receiver.Busy && !rig.Carrying);
        held.Hold();

        // Moved by hand, exactly as the settle loop moves it, and read straight afterwards.
        // The claim is that the wait is over where its timer fires: a flag that waits for the
        // receiver's own loop to be given a thread leaves the clock free to run on across a
        // station that is about to answer, and under load it ran on by nearly nine seconds of
        // the protocol's time in the worst event measured (issue #20). With the station held,
        // the fixed shape passes this under every scheduling: the callback has said busy
        // before the advance returns, and the turn cannot finish and take the flag down
        // because its answer is waiting on the gate. The old shape still fails here whenever
        // the machine queues the continuation, which is the case that does the damage; run
        // inline, its turn blocks at the gate holding its own per-turn flag, and the old
        // shape passes, as it always did on a quiet box.
        rig.Clock.Advance(options.PollInterval);
        receiver.Busy.Should().BeTrue(
            "the poll ended with the timer, not with the thread pool");

        // Let the held answer go, then stop the transfer.
        held.Release();
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
    /// <summary>
    /// Issue #8: a clean transfer over a medium where two stations on air at once lose both
    /// frames still costs exactly the file, and the first Done the receiver sends is one the
    /// sender hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one transfer test the queueing medium cannot make.</b> Everything else
    /// here runs over a medium where a station that wants the channel while the other has it
    /// waits its turn and is then heard; there is no such thing as a collision on it, so a
    /// receiver that answers into the sender's own transmission looks exactly like one that
    /// answered into silence. Scaling the intervals down does not help either, because
    /// <c>Fast</c> scales the collision window and the recovery window together and the
    /// transfer still closes inside the test's patience. Built to collide, the same rig prices
    /// the fault: on this file, answering the instant there is something to say cost thirty
    /// seconds of air and twelve symbols against six seconds and two.
    /// </para>
    /// <para>
    /// What each assertion is for. <b>No repair</b> says nothing was lost, and a symbol that
    /// went out while the receiver was transmitting over it is lost as surely as one the
    /// channel ate. <b>One Done</b> says the first one was heard, which is the whole of the
    /// issue: every further one is a turn of the sender's spent pouring at a station that had
    /// finished. <b>The air budget</b> says the transfer did not have to wait for a second
    /// listening gap to close.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_Clean_Transfer_Costs_Only_Its_Own_Air_When_The_Medium_Collides()
    {
        await using var rig = TransferRig.Build(AudioChannel.Clean, colliding: true);
        byte[] content = Content(BlockSize * 2, seed: 8);
        FileTransferOptions options = Fast();

        var sender = new FileSender(rig.A, options, idSeed: 8, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, _directory, options, timeProvider: rig.Clock);
        rig.WorkInHand(() => sender.Busy, () => receiver.Busy);

        int donesOnAir = 0;
        rig.B.FrameTransmitted += (frame, _) =>
        {
            if (frame?.Type == LinkFrameType.FileDone)
            {
                donesOnAir++;
            }
        };

        // Both moments are read where they happen rather than where this test is next given a
        // thread: the settle loop can walk the clock on across a poll or two between a
        // transfer ending and the await returning, and that would be charged to the protocol.
        TimeSpan? decodedAt = null;
        receiver.Completed += _ => decodedAt ??= rig.Clock.Elapsed;
        TimeSpan? senderStopped = null;
        sender.Completed += _ => senderStopped ??= rig.Clock.Elapsed;

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(CancellationToken.None);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("hello.bin", content, CancellationToken.None), receiver,
            sending: sender);
        FileTransferResult received = await rig.RunAsync(receiving, receiver);

        TimeSpan wasted = senderStopped!.Value - decodedAt!.Value;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{sent.Symbols} symbols and {donesOnAir} Done frames in "
            + $"{sent.Elapsed.TotalSeconds:0.0} s of air, of which {wasted.TotalSeconds:0.0} s "
            + $"after the receiver had the file"));

        sent.Success.Should().BeTrue(sent.FailureReason);
        received.Success.Should().BeTrue(received.FailureReason);
        File.ReadAllBytes(received.Path!).Should().Equal(content);

        sent.RepairSymbols.Should().Be(
            0,
            "nothing was lost on a clean link, and a symbol the receiver transmitted over is "
            + "lost as surely as one the channel ate");
        donesOnAir.Should().Be(
            1,
            "the sender heard the receiver's first Done, so there was never a second to send");

        // One turn of a sender is a status interval of pouring and a listening gap. A receiver
        // whose Done lands in the gap costs it the transmission it was already making and no
        // more; one whose Done is talked over costs it another whole turn, and that is the
        // seventeen and a half seconds of the issue.
        wasted.Should().BeLessThan(
            options.StatusInterval + options.ListenInterval,
            "the sender learned inside the transmission it was already making and the gap it "
            + "was already going to leave, rather than on its next turn");
    }

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
    /// A station whose transmit path can be held shut by the test: SendAsync waits at the gate
    /// until <see cref="Release"/>. Everything else passes straight through. It exists so a
    /// test can pin a flag while a turn is provably still in flight, instead of racing the
    /// turn to the assertion.
    /// </summary>
    private sealed class HoldableStation(IStation inner) : IStation
    {
        private volatile TaskCompletionSource? _held;

        /// <summary>Shuts the gate: the next SendAsync waits until <see cref="Release"/>.</summary>
        public void Hold() =>
            _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Opens the gate and lets anything waiting at it go on air.</summary>
        public void Release() => _held?.TrySetResult();

        public event Action<LinkFrame, FrameQuality>? FrameReceived
        {
            add => inner.FrameReceived += value;
            remove => inner.FrameReceived -= value;
        }

        public event Action<byte[], FrameQuality>? RawFrameReceived
        {
            add => inner.RawFrameReceived += value;
            remove => inner.RawFrameReceived -= value;
        }

        public event Action<LinkFrame?, byte[]>? FrameTransmitted
        {
            add => inner.FrameTransmitted += value;
            remove => inner.FrameTransmitted -= value;
        }

        public string Callsign => inner.Callsign;

        public string Mode => inner.Mode;

        public string DeviceName => inner.DeviceName;

        public bool CanTransmit => inner.CanTransmit;

        public bool Busy => inner.Busy;

        public bool Transmitting => inner.Transmitting;

        public PdnQso.Link.Devices.IPowerControl Power => inner.Power;

        public IModem Modem => inner.Modem;

        public void Start() => inner.Start();

        public LinkFrame Frame(LinkFrameType type, byte session, ReadOnlySpan<byte> payload = default) =>
            inner.Frame(type, session, payload);

        public async Task SendAsync(LinkFrame frame, CancellationToken cancellationToken = default)
        {
            if (_held is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            await inner.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendRawAsync(
            ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default)
        {
            if (_held is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            await inner.SendRawAsync(ax25Frame, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
