using System.Globalization;
using PdnQso.Link.Devices;

namespace PdnQso.Link.Perf;

/// <summary>
/// One measurement, from a <see cref="PerfRun"/> stream or ping-pong run: everything the numbers
/// pane and its CSV/text export need, in one place. Design.md section 3's "sent, heard,
/// delivered, frame error rate, goodput, mean/worst/last SNR, RTT mean/worst, mode, centre,
/// device".
/// </summary>
/// <remarks>
/// <para>
/// One shape serves both procedures rather than two, because a UI numbers pane wants to show
/// whichever one the operator ran without a type switch: a stream report leaves
/// <see cref="MeanRttMs"/>/<see cref="WorstRttMs"/> null, a ping-pong report leaves
/// <see cref="GoodputBytesPerSecond"/> at zero and <see cref="MeanSnrDb"/>/<see cref="WorstSnrDb"/>/
/// <see cref="LastSnrDb"/> null, and both fill in the fields that are common to any measurement
/// of a link (mode, device, elapsed time, the frame counts).
/// </para>
/// <para>
/// <see cref="FramesDelivered"/> and <see cref="FramesHeard"/> are always the same number in
/// this protocol: IL2P+CRC guarantees a heard frame is a whole, correct one, so there is no
/// "heard but not delivered" state for a partial frame to occupy. The field exists anyway
/// because design.md names it separately ("delivered (acked)") - it is the number the far end
/// actually acknowledged, as opposed to a number this end merely counted, and a future
/// transport with partial credit would have somewhere of its own to put a different answer.
/// </para>
/// </remarks>
/// <param name="Procedure">Which measurement this is: <c>"stream"</c> or <c>"ping-pong"</c>.</param>
/// <param name="Mode">The modem mode, as the modem reports itself.</param>
/// <param name="CentreHz">The audio centre, when the caller supplied one.</param>
/// <param name="Device">The device this station ran on.</param>
/// <param name="PowerAtStart">This station's transmit power reading when the run started.</param>
/// <param name="FramesSent">Frames (stream frames, or pings) this end transmitted.</param>
/// <param name="FramesHeard">Frames actually heard at the far end of this measurement.</param>
/// <param name="FramesDelivered">Frames the far end acknowledged - see remarks.</param>
/// <param name="FramesLost">Sequence gaps (stream) or timed-out probes (ping-pong).</param>
/// <param name="Duplicates">Frames heard more than once - a diversity bank decoding the same
/// burst twice, most likely.</param>
/// <param name="FrameErrorRate"><see cref="FramesLost"/> over <see cref="FramesSent"/>; zero
/// when nothing was sent.</param>
/// <param name="GoodputBytesPerSecond">Payload bytes delivered per second of air time -
/// air time measured from the modem's own modulated burst length, not estimated. Zero for a
/// ping-pong report, to which the idea does not apply.</param>
/// <param name="Elapsed">Wall time from the start of the run to this report.</param>
/// <param name="MeanSnrDb">Mean per-frame SNR of the frames heard; null for a ping-pong report
/// or when nothing carried an SNR reading.</param>
/// <param name="WorstSnrDb">The lowest per-frame SNR heard.</param>
/// <param name="LastSnrDb">The most recent per-frame SNR heard.</param>
/// <param name="MeanRttMs">Mean round-trip time of the pings answered; null for a stream report.</param>
/// <param name="WorstRttMs">The longest round-trip time of the pings answered.</param>
/// <param name="Timestamp">When this report was produced.</param>
public sealed record PerfReport(
    string Procedure,
    string Mode,
    double? CentreHz,
    string Device,
    PowerReading? PowerAtStart,
    int FramesSent,
    int FramesHeard,
    int FramesDelivered,
    int FramesLost,
    int Duplicates,
    double FrameErrorRate,
    double GoodputBytesPerSecond,
    TimeSpan Elapsed,
    double? MeanSnrDb,
    double? WorstSnrDb,
    double? LastSnrDb,
    double? MeanRttMs,
    double? WorstRttMs,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// The fixed CSV header line <see cref="ToCsvRow"/> agrees with, column for column. No
    /// trailing newline.
    /// </summary>
    public const string CsvHeader =
        "procedure,mode,centre_hz,device,power_unit,power_setting,power_measured,"
        + "frames_sent,frames_heard,frames_delivered,frames_lost,duplicates,frame_error_rate,"
        + "goodput_bytes_per_second,elapsed_seconds,mean_snr_db,worst_snr_db,last_snr_db,"
        + "mean_rtt_ms,worst_rtt_ms,timestamp";

    /// <summary>
    /// One CSV row matching <see cref="CsvHeader"/> exactly, field for field. Plain ASCII, comma
    /// separated: nothing this record holds can itself contain a comma, so there is no quoting
    /// to get wrong. A null number is an empty field, not the literal text "null".
    /// </summary>
    public string ToCsvRow() => string.Join(
        ',',
        Procedure,
        Mode,
        Number(CentreHz),
        Device,
        PowerAtStart?.Unit.ToString() ?? "",
        PowerAtStart is { } power ? Number(power.Setting) : "",
        PowerAtStart is { Measured: { } measured } ? Number(measured) : "",
        FramesSent.ToString(CultureInfo.InvariantCulture),
        FramesHeard.ToString(CultureInfo.InvariantCulture),
        FramesDelivered.ToString(CultureInfo.InvariantCulture),
        FramesLost.ToString(CultureInfo.InvariantCulture),
        Duplicates.ToString(CultureInfo.InvariantCulture),
        Number(FrameErrorRate),
        Number(GoodputBytesPerSecond),
        Number(Elapsed.TotalSeconds),
        Number(MeanSnrDb),
        Number(WorstSnrDb),
        Number(LastSnrDb),
        Number(MeanRttMs),
        Number(WorstRttMs),
        Timestamp.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>
    /// A short plain-text summary that stands on its own: everything a person reading it later,
    /// with nothing else to hand, needs to know what was measured and what it found.
    /// </summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            $"pdn-qso perf: {Procedure} mode={Mode} device={Device}"
                + (CentreHz is { } centre ? $" centre={centre:0} Hz" : " centre=n/a"),
            $"sent={FramesSent} heard={FramesHeard} delivered={FramesDelivered} "
                + $"lost={FramesLost} dup={Duplicates} fer={FrameErrorRate:0.0%}",
            $"goodput={GoodputBytesPerSecond:0.0} B/s elapsed={Elapsed.TotalSeconds:0.00} s",
        };

        lines.Add(MeanSnrDb is null && WorstSnrDb is null && LastSnrDb is null
            ? "snr: n/a"
            : $"snr mean/worst/last = {Text(MeanSnrDb)}/{Text(WorstSnrDb)}/{Text(LastSnrDb)} dB");

        lines.Add(MeanRttMs is null && WorstRttMs is null
            ? "rtt: n/a"
            : $"rtt mean/worst = {Text(MeanRttMs)}/{Text(WorstRttMs)} ms");

        lines.Add(PowerAtStart is { } power ? $"power {power.Display}" : "power n/a");
        lines.Add(Timestamp.ToString("O", CultureInfo.InvariantCulture));
        return string.Join('\n', lines);
    }

    private static string Number(double? value) =>
        value is double v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";

    private static string Text(double? value) =>
        value is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
}
