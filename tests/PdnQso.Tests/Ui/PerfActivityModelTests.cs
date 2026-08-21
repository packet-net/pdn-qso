using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Devices;
using PdnQso.Link.Perf;
using PdnQso.Ui;
using PdnQso.Tests.Time;
using PdnQso.Tests.Rig;

namespace PdnQso.Tests.Ui;

/// <summary>
/// The Perf pane's model: the live table, the status line, and the CSV export.
/// </summary>
/// <remarks>
/// The two end-to-end tests run a real measurement between two stations over an
/// <see cref="AudioLink"/> and feed both ends' reports into a model each, because the claim
/// worth pinning is that the far end - which nobody pressed a button on - has a table of its
/// own to read. The rest is the model and the file it writes.
/// </remarks>
public class PerfActivityModelTests : IDisposable
{
    /// <summary>
    /// The instant every line in these tests is stamped with. Fixed, because what the
    /// model renders is being checked and the wall clock has no business deciding it.
    /// </summary>
    private static readonly DateTimeOffset At = VirtualClock.Epoch;

    private const string Mode = "bpsk300";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pdn-qso-perf-" + Guid.NewGuid().ToString("N"));

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
    public async Task A_Stream_Run_Fills_In_A_Table_At_Both_Ends()
    {
        var clock = new VirtualClock();
        using AudioLink link = AudioLink.Create(Mode);

        // Both ends on one medium, as the transfer rigs are. Without it the two stations can be
        // inside the same channel object at the same moment, which is not a collision but a data
        // race, and it cost this suite a stream frame about one run in ten.
        using var medium = new HalfDuplexChannel();
        await using var senderStation = new Station(
            Options("M0LTE-7"), link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: clock);
        await using var receiverStation = new Station(
            Options("G0OLD-1"), link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: clock);
        senderStation.Start();
        receiverStation.Start();
        IStation sender = medium.Wrap(senderStation);
        IStation receiver = medium.Wrap(receiverStation);

        var atSender = new PerfActivityModel { FrameCount = 8, PayloadSize = 40 };
        var atReceiver = new PerfActivityModel();
        var senderRun = new PerfRun(clock);
        var receiverRun = new PerfRun(clock);
        Wire(senderRun, atSender);
        Wire(receiverRun, atReceiver);

        // The far end: exactly what the pane starts on its own when nothing has been asked of
        // it, and nobody over there touches a key.
        atReceiver.SetResponder(true);
        Task<PerfReport> receiving = receiverRun.RunStreamReceiverAsync(
            receiver, CancellationToken.None);

        // The far end is started by the same keystroke as this one, which on the air it never
        // is. Wait until it is actually listening, or the first frame goes out to nobody.
        await VirtualTime.WaitForAsync(() => receiverRun.Listening);

        atSender.StartRun();
        PerfReport report = await senderRun.RunStreamSenderAsync(
            sender,
            sender.Modem,
            link.SampleRate,
            atSender.ToStreamOptions(txDelayMilliseconds: 300, centreHz: 1500),
            CancellationToken.None);
        atSender.FinishRun();
        await receiving;

        report.FramesSent.Should().Be(8);
        report.FramesHeard.Should().Be(8);

        ShouldHaveRow(atSender.Table, "procedure", "stream");
        ShouldHaveRow(atSender.Table, "sent", "8");
        ShouldHaveRow(atSender.Table, "heard", "8");
        ShouldHaveRow(atSender.Table, "lost", "0");
        ShouldHaveRow(atSender.Table, "frame errors", "0.0%");
        atSender.Table.Should().Contain(row => row.Contains("@ 1500 Hz", StringComparison.Ordinal));
        atSender.Table.Should().Contain(row => row.Contains("audiolink:A", StringComparison.Ordinal));
        atSender.StatusLine.Should().StartWith("idle, ");

        lock (atReceiver)
        {
            atReceiver.Latest.Should().NotBeNull("a far end with no operator still has numbers");
            ShouldHaveRow(atReceiver.Table, "heard", "8");
            atReceiver.Table.Should().Contain(row => row.Contains("audiolink:B", StringComparison.Ordinal));
            atReceiver.StatusLine.Should().Be(
                "idle, responder running (answering a stream or a ping from the far end)");
        }
    }

    [Fact]
    public async Task A_Ping_Run_Fills_In_The_Round_Trip_Rows()
    {
        var clock = new VirtualClock();
        using AudioLink link = AudioLink.Create(Mode);

        // Both ends on one medium, as the transfer rigs are. Without it the two stations can be
        // inside the same channel object at the same moment, which is not a collision but a data
        // race, and it cost this suite a stream frame about one run in ten.
        using var medium = new HalfDuplexChannel();
        await using var pingerStation = new Station(
            Options("M0LTE-7"), link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: clock);
        await using var responderStation = new Station(
            Options("G0OLD-1"), link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: clock);
        pingerStation.Start();
        responderStation.Start();
        IStation pinger = medium.Wrap(pingerStation);
        IStation responder = medium.Wrap(responderStation);

        var model = new PerfActivityModel { Procedure = PerfProcedure.Ping, FrameCount = 3 };
        var run = new PerfRun(clock);
        Wire(run, model);

        var responderRun = new PerfRun(clock);
        using var stopResponder = new CancellationTokenSource();
        Task responding = responderRun.RunPongResponderAsync(responder, stopResponder.Token);
        await VirtualTime.WaitForAsync(() => responderRun.Listening);

        model.StartRun();
        model.StatusLine.Should().StartWith("running ping-pong");

        PerfReport report = await run.RunPingAsync(
            pinger,
            model.ToPingOptions(centreHz: null),
            CancellationToken.None);
        model.FinishRun();

        await stopResponder.CancelAsync();
        await VirtualTime.WaitForAsync(() => responding.IsCompleted);
        await responding;

        report.FramesHeard.Should().Be(3);
        ShouldHaveRow(model.Table, "procedure", "ping-pong");
        model.Table.Should().Contain(row =>
            row.StartsWith("rtt mean/worst", StringComparison.Ordinal)
            && !row.Contains("n/a", StringComparison.Ordinal));
        model.Table.Should().Contain(row =>
            row.StartsWith("snr mean/worst/last", StringComparison.Ordinal)
            && row.Contains("n/a", StringComparison.Ordinal));
    }

    [Fact]
    public void With_Nothing_Measured_The_Table_Says_So_And_Export_Writes_Nothing()
    {
        var model = new PerfActivityModel();
        string path = Path.Combine(_directory, "perf.csv");

        model.Table.Should().HaveCount(2);
        model.Table[0].Should().StartWith("nothing measured yet");
        model.Export(path).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Export_Writes_The_Header_Once_And_A_Row_Every_Time()
    {
        var model = new PerfActivityModel();
        string path = Path.Combine(_directory, "nested", "perf.csv");

        model.NoteReport(Report("stream", 20, 19));
        model.Export(path).Should().NotBeNull().And.Contain("pdn-qso perf: stream");

        model.NoteReport(Report("ping-pong", 5, 5));
        model.Export(path).Should().NotBeNull();

        string[] written = File.ReadAllLines(path);
        written.Should().HaveCount(3);
        written[0].Should().Be(PerfReport.CsvHeader);
        written[1].Should().StartWith("stream,bpsk300,1500,pipe:test,");
        written[2].Should().StartWith("ping-pong,bpsk300,1500,pipe:test,");
        written[1].Split(',').Should().HaveCount(PerfReport.CsvHeader.Split(',').Length);
    }

    [Fact]
    public void The_Status_Line_Says_Whether_The_Responder_Is_Listening()
    {
        var model = new PerfActivityModel();

        model.StatusLine.Should().Be("idle, responder stopped");

        model.SetResponder(true);
        model.StatusLine.Should().Be(
            "idle, responder running (answering a stream or a ping from the far end)");

        model.FrameCount = 20;
        model.StartRun();
        model.NoteReport(Report("stream", 7, 0));
        model.StatusLine.Should().StartWith("running stream, 7 of 20 sent - responder running");
    }

    [Fact]
    public void Parameters_That_Would_Measure_Nothing_Are_Refused_Before_The_Transmitter_Comes_Up()
    {
        new PerfActivityModel { FrameCount = 0 }.Validate().Should()
            .ContainSingle().Which.Should().StartWith("Frames:");
        new PerfActivityModel { PayloadSize = 4 }.Validate().Should()
            .ContainSingle().Which.Should().StartWith("Payload:");
        new PerfActivityModel { PayloadSize = 5000 }.Validate().Should()
            .ContainSingle().Which.Should().StartWith("Payload:");
        new PerfActivityModel { GapMilliseconds = -1 }.Validate().Should()
            .ContainSingle().Which.Should().StartWith("Gap:");
        new PerfActivityModel().Validate().Should().BeEmpty("the defaults run");
    }

    [Fact]
    public void The_Parameters_Reach_The_Run_Options()
    {
        var model = new PerfActivityModel { FrameCount = 30, PayloadSize = 64, GapMilliseconds = 250 };

        PerfStreamOptions stream = model.ToStreamOptions(txDelayMilliseconds: 450, centreHz: 1700);
        stream.FrameCount.Should().Be(30);
        stream.PayloadSize.Should().Be(64);
        stream.Gap.Should().Be(TimeSpan.FromMilliseconds(250));
        stream.TxDelayMilliseconds.Should().Be(450);
        stream.CentreHz.Should().Be(1700);

        PerfPingOptions ping = model.ToPingOptions(centreHz: 1700);
        ping.PingCount.Should().Be(30);
        ping.Gap.Should().Be(TimeSpan.FromMilliseconds(250));
        ping.CentreHz.Should().Be(1700);
    }

    [Fact]
    public void Clearing_Drops_The_Numbers_And_Both_Flags()
    {
        var model = new PerfActivityModel();
        model.SetResponder(true);
        model.StartRun();
        model.NoteReport(Report("stream", 1, 1));

        model.Clear();

        model.Latest.Should().BeNull();
        model.RunInProgress.Should().BeFalse();
        model.ResponderRunning.Should().BeFalse();
        model.Table[0].Should().StartWith("nothing measured yet");
    }

    /// <summary>Asserts the table has a row for <paramref name="label"/> reading <paramref name="value"/>.</summary>
    private static void ShouldHaveRow(IReadOnlyList<string> table, string label, string value) =>
        table.Should().Contain(
            $"{label,-20} {value}",
            "the table shows the report's own {0}", label);

    private static void Wire(PerfRun run, PerfActivityModel model)
    {
        void Take(PerfReport report)
        {
            lock (model)
            {
                model.NoteReport(report);
            }
        }

        run.Progress += Take;
        run.Completed += Take;
    }

    private static StationOptions Options(string callsign) => new()
    {
        Callsign = callsign,
        BusyWaitTimeout = TimeSpan.FromSeconds(20),
    };

    private static PerfReport Report(string procedure, int sent, int heard) => new(
        procedure,
        "bpsk300",
        1500,
        "pipe:test",
        new PowerReading(PowerUnit.Watts, 10, 9.5, "set 10 W, last 9.5 W"),
        FramesSent: sent,
        FramesHeard: heard,
        FramesDelivered: heard,
        FramesLost: sent - heard,
        Duplicates: 0,
        FrameErrorRate: sent == 0 ? 0 : (double)(sent - heard) / sent,
        GoodputBytesPerSecond: 31.25,
        Elapsed: TimeSpan.FromSeconds(42),
        MeanSnrDb: 12.4,
        WorstSnrDb: 9.8,
        LastSnrDb: 11.1,
        MeanRttMs: null,
        WorstRttMs: null,
        Timestamp: DateTimeOffset.UnixEpoch);
}
