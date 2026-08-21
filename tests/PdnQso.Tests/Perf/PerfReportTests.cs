using System.Globalization;
using PdnQso.Link.Devices;
using PdnQso.Link.Perf;

namespace PdnQso.Tests.Perf;

/// <summary>The CSV and text export a <see cref="PerfReport"/> hands the UI's Export command.</summary>
public class PerfReportTests
{
    private static PerfReport SampleStreamReport() => new(
        Procedure: "stream",
        Mode: "bpsk300-il2pc",
        CentreHz: 1500,
        Device: "audiolink:A",
        PowerAtStart: new PowerReading(PowerUnit.Watts, 10, 9.6, "set 10 W, reading 9.6 W"),
        FramesSent: 20,
        FramesHeard: 19,
        FramesDelivered: 19,
        FramesLost: 1,
        Duplicates: 0,
        FrameErrorRate: 0.05,
        GoodputBytesPerSecond: 123.45,
        Elapsed: TimeSpan.FromSeconds(2.5),
        MeanSnrDb: 12.3,
        WorstSnrDb: 8.1,
        LastSnrDb: 12.5,
        MeanRttMs: null,
        WorstRttMs: null,
        Timestamp: new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_Csv_Header_And_Row_Have_The_Same_Column_Count_And_Parse_Back()
    {
        PerfReport report = SampleStreamReport();

        string[] header = PerfReport.CsvHeader.Split(',');
        string[] row = report.ToCsvRow().Split(',');

        row.Should().HaveSameCount(header, "every header column must have a matching value");

        int frameLostIndex = Array.IndexOf(header, "frames_lost");
        int goodputIndex = Array.IndexOf(header, "goodput_bytes_per_second");
        int ferIndex = Array.IndexOf(header, "frame_error_rate");
        int meanSnrIndex = Array.IndexOf(header, "mean_snr_db");
        int meanRttIndex = Array.IndexOf(header, "mean_rtt_ms");

        int.Parse(row[frameLostIndex], CultureInfo.InvariantCulture).Should().Be(1);
        double.Parse(row[goodputIndex], CultureInfo.InvariantCulture).Should().BeApproximately(123.45, 0.01);
        double.Parse(row[ferIndex], CultureInfo.InvariantCulture).Should().BeApproximately(0.05, 0.0001);
        double.Parse(row[meanSnrIndex], CultureInfo.InvariantCulture).Should().BeApproximately(12.3, 0.01);
        row[meanRttIndex].Should().BeEmpty("a stream report has no RTT to report");
    }

    [Fact]
    public void To_Text_Contains_Mode_Device_Fer_And_Goodput()
    {
        PerfReport report = SampleStreamReport();

        string text = report.ToText();

        text.Should().Contain(report.Mode);
        text.Should().Contain(report.Device);
        text.Should().Contain("fer=");
        text.Should().Contain("goodput=");
        text.Should().Contain("5.0%", "the frame error rate should read as a percentage");
    }

    [Fact]
    public void To_Text_Says_Not_Applicable_Rather_Than_Printing_Nothing_For_A_Ping_Pong_Report()
    {
        PerfReport report = SampleStreamReport() with
        {
            Procedure = "ping-pong",
            GoodputBytesPerSecond = 0,
            MeanSnrDb = null,
            WorstSnrDb = null,
            LastSnrDb = null,
            MeanRttMs = 87.5,
            WorstRttMs = 140.2,
        };

        string text = report.ToText();

        text.Should().Contain("snr: n/a");
        text.Should().Contain("87.5");
        text.Should().Contain("140.2");
    }
}
