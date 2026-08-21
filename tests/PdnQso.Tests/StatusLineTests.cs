using PdnQso.Ui;

namespace PdnQso.Tests;

/// <summary>
/// The status bar of design.md section 6, as a pure function: device, mode, centre, power with
/// its read-back, the lamps, the last SNR and who we are working.
/// </summary>
public class StatusLineTests
{
    private static StatusSnapshot Working() => new(
        "alsa:plughw:CARD=Device,DEV=0",
        "bpsk300",
        1500,
        14_105_000,
        "set 10 W, last 9.5 W",
        Ptt: false,
        Dcd: false,
        LastSnrDb: 12.4,
        Correspondent: "G0OLD-1",
        MonitorOnly: false);

    [Fact]
    public void Everything_The_Operator_Needs_Is_On_The_One_Line()
    {
        string line = StatusLine.Format(Working());

        line.Should().Contain("alsa:plughw:CARD=Device,DEV=0");
        line.Should().Contain("bpsk300");
        line.Should().Contain("1500 Hz");
        line.Should().Contain("14.105000 MHz");
        line.Should().Contain("set 10 W, last 9.5 W");
        line.Should().Contain("12.4 dB");
        line.Should().Contain("G0OLD-1");
    }

    [Fact]
    public void The_Lamps_Keep_Their_Width_So_The_Line_Does_Not_Shuffle_On_Every_Keyup()
    {
        string idle = StatusLine.Format(Working());
        string keyed = StatusLine.Format(Working() with { Ptt = true });
        string busy = StatusLine.Format(Working() with { Dcd = true });

        keyed.Should().Contain("[TX ]");
        busy.Should().Contain("[DCD]");
        keyed.Length.Should().Be(idle.Length);
        busy.Length.Should().Be(idle.Length);
    }

    [Fact]
    public void A_Monitor_Only_Session_Says_So_Before_Anything_Else()
    {
        StatusLine.Format(Working() with { MonitorOnly = true })
            .Should().StartWith("[MONITOR]");
    }

    [Fact]
    public void Nothing_Heard_Yet_Shows_A_Dash_Rather_Than_An_Snr_Of_Zero()
    {
        StatusLine.Format(Working() with { LastSnrDb = null })
            .Should().Contain("snr -").And.NotContain("0.0 dB");
    }

    [Fact]
    public void A_Rig_This_Tool_Does_Not_Tune_Simply_Has_No_Frequency_On_The_Line()
    {
        string line = StatusLine.Format(Working() with { RfHz = null, AudioCentreHz = null });

        line.Should().NotContain("MHz");
        line.Should().Contain("bpsk300");
    }

    [Fact]
    public void A_Device_With_No_Power_Control_Does_Not_Get_An_Empty_Power_Field()
    {
        StatusLine.Format(Working() with { Power = "" }).Should().NotContain("pwr");
    }

    [Fact]
    public void Nobody_Worked_Yet_Means_No_Correspondent_On_The_Line()
    {
        StatusLine.Format(Working() with { Correspondent = null }).Should().NotContain("with");
    }

    [Fact]
    public void The_Whole_Line_Is_Ascii_So_A_Serial_Console_Reads_It_The_Same()
    {
        string line = StatusLine.Format(Working() with { Ptt = true, Dcd = true });

        line.ToCharArray().Should().OnlyContain(c => c >= 0x20 && c <= 0x7E);
    }
}
