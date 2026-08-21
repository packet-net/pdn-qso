using System.Globalization;
using PdnQso.Link.Perf;
using PdnQso.Link.Transfer;

namespace PdnQso.Ui;

/// <summary>Which measurement Perf is set to run.</summary>
public enum PerfProcedure
{
    /// <summary>A one-way stream of numbered frames: loss and goodput.</summary>
    Stream,

    /// <summary>Probes answered one at a time: round-trip time.</summary>
    Ping,
}

/// <summary>
/// The Perf pane's model: which procedure with what parameters, what the run has found so far,
/// and the CSV and text export.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like the other two activity models. What is worth pinning here is that the table is
/// the report's own fields and not a second calculation of them, that the CSV file gets its
/// header exactly once so it opens in anything, and that a station acting as the far end shows
/// the numbers it measured without anybody having pressed a button on it.
/// </para>
/// <para>
/// <b>Both sides fill this in.</b> A run has a near end and a far end, and the far end's
/// <see cref="PerfRun.RunStreamReceiverAsync"/> produces a report of its own - what it heard, at
/// what SNR. That report goes through the same <see cref="NoteReport"/>, so a station left
/// running as somebody's far end has a screen worth reading rather than a blank pane.
/// </para>
/// </remarks>
public sealed class PerfActivityModel
{
    /// <summary>The smallest stream payload: the sequence, total and timestamp header.</summary>
    /// <remarks>
    /// <c>PerfWire.StreamHeaderLength</c>, which is internal to the link library. Named here so
    /// the dialog can refuse a payload that <see cref="PerfRun"/> would throw over.
    /// </remarks>
    public const int MinimumPayloadSize = 8;

    /// <summary>The largest payload one link frame carries.</summary>
    public static int MaximumPayloadSize => LinkCapacity.MaxPayloadBytes;

    /// <summary>Which measurement Start runs.</summary>
    public PerfProcedure Procedure { get; set; } = PerfProcedure.Stream;

    /// <summary>How many frames (or probes) a run sends.</summary>
    public int FrameCount { get; set; } = 20;

    /// <summary>How many payload bytes each stream frame carries.</summary>
    public int PayloadSize { get; set; } = 128;

    /// <summary>How long to wait between frames, in milliseconds.</summary>
    public int GapMilliseconds { get; set; }

    /// <summary>True while the responders are listening for somebody else's run.</summary>
    public bool ResponderRunning { get; private set; }

    /// <summary>True while this station is running a measurement of its own.</summary>
    public bool RunInProgress { get; private set; }

    /// <summary>The most recent report from either end, or null before there is one.</summary>
    public PerfReport? Latest { get; private set; }

    /// <summary>Drops everything: called when the station is replaced.</summary>
    public void Clear()
    {
        Latest = null;
        RunInProgress = false;
        ResponderRunning = false;
    }

    /// <summary>Says whether the far-end responders are listening.</summary>
    public void SetResponder(bool running) => ResponderRunning = running;

    /// <summary>Marks the start of a run this station is driving.</summary>
    public void StartRun()
    {
        RunInProgress = true;
        Latest = null;
    }

    /// <summary>Marks the end of a run this station was driving.</summary>
    public void FinishRun() => RunInProgress = false;

    /// <summary>Takes a report, from a progress tick or a completion, near end or far.</summary>
    public void NoteReport(PerfReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Latest = report;
    }

    /// <summary>Everything wrong with the parameters, in lines an operator can act on.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (FrameCount < 1)
        {
            problems.Add("Frames: a run of no frames measures nothing.");
        }

        if (PayloadSize < MinimumPayloadSize)
        {
            problems.Add(
                $"Payload: {MinimumPayloadSize} bytes is the sequence and timestamp header, so "
                + "that is the floor.");
        }

        if (PayloadSize > MaximumPayloadSize)
        {
            problems.Add($"Payload: {MaximumPayloadSize} bytes is all one link frame carries.");
        }

        if (GapMilliseconds < 0)
        {
            problems.Add("Gap: it cannot be negative.");
        }

        return problems;
    }

    /// <summary>The stream options these parameters ask for.</summary>
    /// <param name="txDelayMilliseconds">The station's TXDELAY, which is part of a frame's air
    /// time and therefore part of the goodput.</param>
    /// <param name="centreHz">The audio centre, for the record the report carries.</param>
    public PerfStreamOptions ToStreamOptions(int txDelayMilliseconds, double? centreHz) => new()
    {
        FrameCount = FrameCount,
        PayloadSize = PayloadSize,
        Gap = TimeSpan.FromMilliseconds(Math.Max(0, GapMilliseconds)),
        TxDelayMilliseconds = txDelayMilliseconds,
        CentreHz = centreHz,
    };

    /// <summary>The ping options these parameters ask for.</summary>
    /// <param name="centreHz">The audio centre, for the record the report carries.</param>
    public PerfPingOptions ToPingOptions(double? centreHz) => new()
    {
        PingCount = FrameCount,
        Gap = TimeSpan.FromMilliseconds(Math.Max(0, GapMilliseconds)),
        CentreHz = centreHz,
    };

    /// <summary>One line saying what Perf is doing.</summary>
    public string StatusLine
    {
        get
        {
            string responder = ResponderRunning
                ? "responder running (answering a stream or a ping from the far end)"
                : "responder stopped";
            if (!RunInProgress)
            {
                return $"idle, {responder}";
            }

            string sent = Latest is PerfReport report
                ? string.Create(CultureInfo.InvariantCulture, $", {report.FramesSent} of {FrameCount} sent")
                : "";
            return $"running {Name(Procedure)}{sent} - {responder}";
        }
    }

    /// <summary>The numbers table: every field of the latest report, one per line.</summary>
    public IReadOnlyList<string> Table => TableFor(Latest);

    /// <summary>The numbers table for a report, or the empty state when there is not one.</summary>
    public static IReadOnlyList<string> TableFor(PerfReport? report)
    {
        if (report is not PerfReport r)
        {
            return ["nothing measured yet - set the parameters and press Start,",
                    "or leave this station running as somebody else's far end."];
        }

        return
        [
            Row("procedure", r.Procedure),
            Row("mode", r.CentreHz is double centre
                ? string.Create(CultureInfo.InvariantCulture, $"{r.Mode} @ {centre:0} Hz")
                : r.Mode),
            Row("device", r.Device),
            Row("power", r.PowerAtStart?.Display ?? "n/a"),
            Row("sent", Count(r.FramesSent)),
            Row("heard", Count(r.FramesHeard)),
            Row("delivered", Count(r.FramesDelivered)),
            Row("lost", Count(r.FramesLost)),
            Row("duplicates", Count(r.Duplicates)),
            Row("frame errors", string.Create(CultureInfo.InvariantCulture, $"{r.FrameErrorRate:0.0%}")),
            Row("goodput", string.Create(CultureInfo.InvariantCulture, $"{r.GoodputBytesPerSecond:0.0} B/s")),
            Row("elapsed", string.Create(CultureInfo.InvariantCulture, $"{r.Elapsed.TotalSeconds:0.00} s")),
            Row("snr mean/worst/last",
                $"{Decimal1(r.MeanSnrDb)}/{Decimal1(r.WorstSnrDb)}/{Decimal1(r.LastSnrDb)} dB"),
            Row("rtt mean/worst", $"{Decimal1(r.MeanRttMs)}/{Decimal1(r.WorstRttMs)} ms"),
            Row("at", r.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>
    /// Appends the latest report to the CSV and returns the text summary for the log pane.
    /// </summary>
    /// <param name="path">The CSV file; its directory is created and the header line is
    /// written when the file is new, so the file stands on its own.</param>
    /// <returns>The text summary, or null when there is nothing measured to export.</returns>
    public string? Export(string path)
    {
        if (Latest is not PerfReport report)
        {
            return null;
        }

        AppendCsv(path, report);
        return report.ToText();
    }

    /// <summary>Appends one report to a CSV file, writing the header when the file is new.</summary>
    /// <param name="path">The file.</param>
    /// <param name="report">The report.</param>
    /// <returns>The row that was appended.</returns>
    public static string AppendCsv(string path, PerfReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The header goes in once, when the file is new or empty. A CSV whose header repeats
        // every time somebody presses Export is a CSV no spreadsheet will open, and one with no
        // header at all is a row of numbers nobody can read a year later.
        bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
        string row = report.ToCsvRow();
        using var writer = new StreamWriter(path, append: true);
        if (fresh)
        {
            writer.WriteLine(PerfReport.CsvHeader);
        }

        writer.WriteLine(row);
        return row;
    }

    /// <summary>The procedure's name, as the report and the status line spell it.</summary>
    public static string Name(PerfProcedure procedure) =>
        procedure == PerfProcedure.Ping ? "ping-pong" : "stream";

    private static string Row(string label, string value) => $"{label,-20} {value}";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Decimal1(double? value) =>
        value is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
}
