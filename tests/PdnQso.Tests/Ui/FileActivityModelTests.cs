using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Transfer;
using PdnQso.Tests.Transfer;
using PdnQso.Ui;

namespace PdnQso.Tests.Ui;

/// <summary>
/// The File pane's model, driven by a real transfer between two stations over an
/// <see cref="AudioLink"/>.
/// </summary>
/// <remarks>
/// The mode is <c>afsk1200-il2p</c> for the same reason
/// <see cref="PdnQso.Tests.Transfer.FileTransferTests"/> uses it: it is one of the cheapest in
/// the catalogue to simulate, so a whole transfer runs in about the time one 300 baud frame
/// would take to modulate. Nothing here is a claim about a modem; the claims are about what
/// reaches the screen.
/// </remarks>
public class FileActivityModelTests : IDisposable
{
    private const string Mode = "afsk1200-il2p";
    private const int BlockSize = 64;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pdn-qso-file-model-" + Guid.NewGuid().ToString("N"));

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
    public async Task A_Transfer_Fills_In_The_Bar_At_Both_Ends_And_Leaves_A_Result_Line()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build();
        var atSender = new FileActivityModel();
        var atReceiver = new FileActivityModel();

        var sender = new FileSender(rig.A, Options(), idSeed: 4242);
        var receiver = new FileReceiver(rig.B, _directory, Options());
        Wire(sender, atSender);
        Wire(receiver, atReceiver);

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        FileTransferResult sent = await sender.SendAsync("notes.txt", Content(512), cancel.Token);
        FileTransferResult received = await receiving;

        sent.Success.Should().BeTrue(sent.FailureReason);
        received.Success.Should().BeTrue(received.FailureReason);

        lock (atReceiver)
        {
            // The result clears the bar, so what is asserted is the line the transfer left.
            atReceiver.Receiving.Should().BeNull("the transfer is over");
            atReceiver.ReceiveLine.Should().Be("recv: idle");
            // The sender re-offers while it is pouring, so there is one taken offer and then
            // one line per repeat saying it was not taken again.
            atReceiver.Lines[0].Should().Contain("offer notes.txt")
                .And.Contain("512 bytes in 8 blocks of 64")
                .And.EndWith("- accepting");
            atReceiver.Lines.Skip(1).SkipLast(1).Should()
                .AllSatisfy(line => line.Should().EndWith("- not accepting"));
            atReceiver.Lines[^1].Should().Contain("received notes.txt: 512 bytes in 8 symbols");
        }

        lock (atSender)
        {
            atSender.Lines.Should().ContainSingle()
                .Which.Should().Contain("sent notes.txt: 512 bytes in 8 symbols (0 repair)");
        }
    }

    [Fact]
    public async Task The_Bar_Moves_With_The_Symbols_As_They_Arrive()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var rig = Rig.Build();
        var model = new FileActivityModel();
        var fractions = new List<double>();
        var lines = new List<string>();

        var sender = new FileSender(rig.A, Options(), idSeed: 99);
        var receiver = new FileReceiver(rig.B, _directory, Options());
        receiver.Progress += progress =>
        {
            lock (model)
            {
                model.Note(progress);
                fractions.Add(model.ReceiveFraction);
                lines.Add(model.ReceiveLine);
            }
        };

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        await sender.SendAsync("bar.bin", Content(256), cancel.Token);
        (await receiving).Success.Should().BeTrue();

        lock (model)
        {
            fractions.Should().HaveCount(4, "256 bytes in blocks of 64 is K = 4");
            fractions.Should().Equal(0.25, 0.5, 0.75, 1.0);
            lines[0].Should().StartWith("recv bar.bin  sym 1  decoded 1/4  25%");
            lines[^1].Should().StartWith("recv bar.bin  sym 4  decoded 4/4  100%");
            lines[^1].Should().EndWith("B/s");
        }
    }

    [Fact]
    public async Task An_Offer_Refused_Is_Still_Shown()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var rig = Rig.Build();
        var model = new FileActivityModel();

        var receiver = new FileReceiver(rig.B, _directory, Options())
        {
            AcceptOffer = _ => false,
        };
        receiver.OfferHeard += (offer, accepted) =>
        {
            lock (model)
            {
                model.NoteOffer(offer, accepted, DateTimeOffset.Now);
            }
        };

        Task<FileTransferResult> receiving = receiver.ReceiveAsync(cancel.Token);
        var sender = new FileSender(rig.A, Options() with { MaxSymbols = 2 }, idSeed: 7);
        await sender.SendAsync("unwanted.bin", Content(128), cancel.Token);

        (await ChatRigWait(() =>
        {
            lock (model)
            {
                return model.Lines.Count > 0;
            }
        })).Should().BeTrue("the offer was heard even though it was not taken");

        lock (model)
        {
            model.Lines[0].Should().Contain("offer unwanted.bin").And.Contain("not accepting");
        }

        await cancel.CancelAsync();
        Func<Task> waiting = () => receiving;
        await waiting.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void An_Idle_Direction_Says_So_Rather_Than_Showing_A_Bar_At_Nothing()
    {
        var model = new FileActivityModel();

        model.SendLine.Should().Be("send: idle");
        model.ReceiveLine.Should().Be("recv: idle");
        model.SendFraction.Should().Be(0);
        model.ReceiveFraction.Should().Be(0);
    }

    [Fact]
    public void A_Senders_Bar_Follows_The_Receivers_Count_And_Not_Its_Own_Optimism()
    {
        var model = new FileActivityModel();

        // Twelve symbols poured, four of eight blocks acknowledged: the bar is at a half.
        model.Note(new FileProgress(
            1, "big.bin", FileTransferRole.Sender,
            Symbols: 12, Decoded: 4, BlockCount: 8, BlockSize: 64,
            Elapsed: TimeSpan.FromSeconds(4)));

        model.SendFraction.Should().Be(0.5);
        model.SendLine.Should().Be("send big.bin  sym 12  decoded 4/8  50%  192.0 B/s");
    }

    [Fact]
    public void A_Transfer_That_Gave_Up_Leaves_A_Line_And_Clears_Its_Bar()
    {
        var model = new FileActivityModel();
        model.Note(new FileProgress(
            1, "gone.bin", FileTransferRole.Receiver, 3, 3, 8, 64, TimeSpan.FromSeconds(2)));

        model.NoteFailure(FileTransferRole.Receiver, "nothing heard for 90 s", DateTimeOffset.Now);

        model.Receiving.Should().BeNull();
        model.ReceiveLine.Should().Be("recv: idle");
        model.Lines.Should().ContainSingle()
            .Which.Should().Contain("receive failed: nothing heard for 90 s");
    }

    [Fact]
    public void Clearing_Drops_Both_Directions_And_The_Record()
    {
        var model = new FileActivityModel();
        model.Note(new FileProgress(1, "a", FileTransferRole.Sender, 1, 0, 4, 64, TimeSpan.FromSeconds(1)));
        model.Note(new FileProgress(2, "b", FileTransferRole.Receiver, 1, 1, 4, 64, TimeSpan.FromSeconds(1)));
        model.NoteLine("something happened", DateTimeOffset.Now);

        model.Clear();

        model.Sending.Should().BeNull();
        model.Receiving.Should().BeNull();
        model.Lines.Should().BeEmpty();
    }

    [Fact]
    public void The_Oldest_Lines_Fall_Off_When_The_Record_Is_Full()
    {
        var model = new FileActivityModel(capacity: 2);

        model.NoteLine("one", DateTimeOffset.Now);
        model.NoteLine("two", DateTimeOffset.Now);
        model.NoteLine("three", DateTimeOffset.Now);

        model.Lines.Should().HaveCount(2);
        model.Lines[0].Should().EndWith("two");
        model.Lines[1].Should().EndWith("three");
    }

    private static async Task<bool> ChatRigWait(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    private static void Wire(FileSender sender, FileActivityModel model)
    {
        sender.Progress += progress =>
        {
            lock (model)
            {
                model.Note(progress);
            }
        };
        sender.Completed += result =>
        {
            lock (model)
            {
                model.NoteResult(result, DateTimeOffset.Now);
            }
        };
        sender.Failed += reason =>
        {
            lock (model)
            {
                model.NoteFailure(FileTransferRole.Sender, reason, DateTimeOffset.Now);
            }
        };
    }

    private static void Wire(FileReceiver receiver, FileActivityModel model)
    {
        receiver.OfferHeard += (offer, accepted) =>
        {
            lock (model)
            {
                model.NoteOffer(offer, accepted, DateTimeOffset.Now);
            }
        };
        receiver.Progress += progress =>
        {
            lock (model)
            {
                model.Note(progress);
            }
        };
        receiver.Completed += result =>
        {
            lock (model)
            {
                model.NoteResult(result, DateTimeOffset.Now);
            }
        };
        receiver.Failed += reason =>
        {
            lock (model)
            {
                model.NoteFailure(FileTransferRole.Receiver, reason, DateTimeOffset.Now);
            }
        };
    }

    /// <summary>
    /// The transfer tests' own fast options: short intervals so a whole transfer runs in a
    /// second or two, and patience long enough that a slow machine is not read as a dead link.
    /// </summary>
    private static FileTransferOptions Options() => new()
    {
        BlockSize = BlockSize,
        StatusInterval = TimeSpan.FromMilliseconds(200),
        ListenInterval = TimeSpan.FromMilliseconds(400),
        OfferInterval = TimeSpan.FromMilliseconds(400),
        PollInterval = TimeSpan.FromMilliseconds(5),
        PatienceIntervals = 15,
        DoneLinger = TimeSpan.FromMilliseconds(100),
    };

    private static byte[] Content(int length)
    {
        var random = new Random(1);
        byte[] content = new byte[length];
        random.NextBytes(content);
        return content;
    }

    /// <summary>Two stations on one shared medium, as the transfer tests build them.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private AudioLink _link = null!;
        private Station _a = null!;
        private Station _b = null!;
        private HalfDuplexChannel _medium = null!;

        public IStation A { get; private set; } = null!;

        public IStation B { get; private set; } = null!;

        public static Rig Build()
        {
            var link = AudioLink.Create(Mode);
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
}
