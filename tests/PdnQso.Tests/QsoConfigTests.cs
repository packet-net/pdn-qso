using PdnQso.Config;
using PdnQso.Link.Devices;

namespace PdnQso.Tests;

/// <summary>
/// The settings of design.md section 6: what they are worth on disk, and what the dialog
/// refuses to save.
/// </summary>
/// <remarks>
/// Validation is a pure function so the same check runs in the settings dialog, at start-up
/// and here. Every message is written for an operator to act on, which is why several of these
/// assert on the wording and not only on the count.
/// </remarks>
public class QsoConfigTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"pdn-qso-config-{Guid.NewGuid():N}");

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static QsoConfig Good() => new()
    {
        Device = "plughw:CARD=Device,DEV=0",
        Callsign = "M0LTE-7",
        Mode = "bpsk300",
        AudioCentreHz = 1500,
        RfFrequencyHz = 14_105_000,
        Power = 10,
        PttType = "cm108",
        PttDevice = "/dev/hidraw0",
    };

    [Fact]
    public void A_Saved_Config_Comes_Back_Exactly()
    {
        string path = Path_("config.json");
        QsoConfig written = Good() with { FrameLogPath = "/tmp/frames.db", IdentCallsign = "M0LTE" };

        written.Save(path);
        QsoConfig? read = QsoConfig.Load(path);

        read.Should().Be(written);
    }

    [Fact]
    public void The_Written_File_Holds_Settings_And_Not_Anything_Worked_Out_From_Them()
    {
        string path = Path_("config.json");
        Good().Save(path);

        string json = File.ReadAllText(path);

        json.Should().Contain("\"callsign\"");
        foreach (string computed in new[]
                 { "resolved_frame_log_path", "resolved_ident_callsign", "resolved_audio_centre_hz" })
        {
            json.Should().NotContain(
                computed,
                "a file full of things the program works out for itself invites somebody to edit "
                + "one and wonder why nothing happened");
        }
    }

    [Fact]
    public void A_Config_That_Is_Not_There_Is_Not_An_Error()
    {
        QsoConfig.Load(Path_("nothing.json")).Should().BeNull();
    }

    [Fact]
    public void A_Config_With_One_Line_In_It_Is_Still_A_Config()
    {
        string path = Path_("sparse.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """{ "callsign": "M0LTE" }""");

        QsoConfig? read = QsoConfig.Load(path);

        read.Should().NotBeNull();
        read!.Callsign.Should().Be("M0LTE");
        read.Mode.Should().Be("bpsk300", "everything else keeps its default");
        read.TxDelayMs.Should().Be(300);
    }

    [Fact]
    public void A_Config_That_Has_Been_Hand_Edited_Into_A_Corner_Says_So_Rather_Than_Being_Replaced()
    {
        string path = Path_("broken.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ this is not json");

        Action load = () => QsoConfig.Load(path);

        load.Should().Throw<InvalidDataException>().WithMessage("*not readable as a pdn-qso config*");
    }

    [Fact]
    public void Saving_Over_An_Existing_Config_Leaves_No_Half_File_Behind()
    {
        string path = Path_("config.json");
        Good().Save(path);

        (Good() with { Callsign = "G0OLD" }).Save(path);

        QsoConfig.Load(path)!.Callsign.Should().Be("G0OLD");
        File.Exists(path + ".new").Should().BeFalse("the temporary file is moved, not left");
    }

    [Fact]
    public void A_Config_That_Will_Start_A_Station_Has_Nothing_To_Say()
    {
        Good().Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_Station_Without_A_Callsign_Is_Refused()
    {
        (Good() with { Callsign = "" }).Validate()
            .Should().ContainSingle().Which.Should().Contain("Callsign");
    }

    [Fact]
    public void A_Callsign_With_A_Slash_In_It_Is_Refused_With_The_Grammar()
    {
        (Good() with { Callsign = "M0LTE/P" }).Validate()
            .Should().ContainSingle().Which.Should().Contain("CALL or CALL-SSID");
    }

    [Fact]
    public void A_Mode_That_Does_Not_Exist_Gets_A_Did_You_Mean()
    {
        IReadOnlyList<string> problems = (Good() with { Mode = "bpsk3000" }).Validate();

        problems.Should().ContainSingle();
        problems[0].Should().Contain("bpsk300");
    }

    [Fact]
    public void A_Centre_Outside_The_Modes_Nyquist_Is_Refused()
    {
        (Good() with { AudioCentreHz = 9000 }).Validate()
            .Should().ContainSingle().Which.Should().Contain("Nyquist");
    }

    [Fact]
    public void An_UberSdr_With_No_Frequency_Is_Refused_Because_A_Receiver_Cannot_Guess()
    {
        (Good() with { Device = "ubersdr:example.org", RfFrequencyHz = null }).Validate()
            .Should().ContainSingle().Which.Should().Contain("where to listen");
    }

    [Fact]
    public void A_Capture_Rate_The_Mode_Does_Not_Divide_Is_Refused_With_The_Fix()
    {
        (Good() with { CaptureRateHz = 44100 }).Validate()
            .Should().ContainSingle().Which.Should().Contain("48000");
    }

    [Fact]
    public void A_Ptt_Kind_With_No_Device_To_Key_Is_Refused()
    {
        (Good() with { PttDevice = null }).Validate()
            .Should().ContainSingle().Which.Should().Contain("/dev/hidraw0");
    }

    [Fact]
    public void Everything_Wrong_Is_Listed_At_Once_Rather_Than_One_Thing_At_A_Time()
    {
        IReadOnlyList<string> problems = (Good() with
        {
            Callsign = "",
            Mode = "nonsense",
            TxDelayMs = -1,
            MaxRetries = -2,
        }).Validate();

        problems.Should().HaveCount(4);
    }

    [Fact]
    public void An_Empty_Frame_Log_Path_Means_No_Log_And_A_Missing_One_Means_The_Default()
    {
        (Good() with { FrameLogPath = "" }).ResolvedFrameLogPath.Should().BeNull();
        (Good() with { FrameLogPath = null }).ResolvedFrameLogPath
            .Should().Be(QsoConfig.DefaultFrameLogPath);
        (Good() with { FrameLogPath = "/tmp/x.db" }).ResolvedFrameLogPath.Should().Be("/tmp/x.db");
    }

    [Fact]
    public void The_Ident_Falls_Back_To_The_Station_Callsign()
    {
        (Good() with { IdentCallsign = null }).ResolvedIdentCallsign.Should().Be("M0LTE-7");
        (Good() with { IdentCallsign = "M0LTE" }).ResolvedIdentCallsign.Should().Be("M0LTE");
    }

    [Fact]
    public void A_Mode_With_A_Centre_Fixed_By_Its_Spec_Reports_No_Centre_At_All()
    {
        // fsk9600 is baseband: it occupies DC upwards and has no centre to speak of, and the
        // library throws if one is handed to it.
        QsoConfig config = Good() with { Mode = "fsk9600", AudioCentreHz = null };

        config.ResolvedAudioCentreHz.Should().BeNull(
            "a baseband mode occupies DC upwards, and the modem is not asked for a centre");
    }

    [Fact]
    public void The_Config_Turns_Into_The_Device_Options_It_Describes()
    {
        DeviceOptions options = Good().ToDeviceOptions();

        options.Ptt.Should().Be(PttKind.Cm108);
        options.PttDevice.Should().Be("/dev/hidraw0");
        options.AudioCentreHz.Should().Be(1500);
        options.RfFrequencyHz.Should().Be(14_105_000);
        options.CaptureRateHz.Should().Be(48_000);
    }
}
