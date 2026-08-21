using PdnQso.Config;

namespace PdnQso.Tests;

/// <summary>
/// The command line: a config file, three overrides for one session, and the switch that keeps
/// the transmitter off.
/// </summary>
public class CommandLineTests
{
    [Fact]
    public void Nothing_On_The_Command_Line_Means_The_Default_Config_And_Nothing_Overridden()
    {
        CommandLine parsed = CommandLine.Parse([]);

        parsed.Error.Should().BeNull();
        parsed.HasOverrides.Should().BeFalse();
        parsed.MonitorOnly.Should().BeFalse();
        parsed.ResolvedConfigPath.Should().Be(QsoConfig.DefaultPath);
    }

    [Fact]
    public void The_Three_Overrides_Are_Read_As_Separate_Arguments()
    {
        CommandLine parsed = CommandLine.Parse(
            ["--device", "flex:mock", "--mode", "qpsk2400", "--callsign", "M0LTE-7"]);

        parsed.Device.Should().Be("flex:mock");
        parsed.Mode.Should().Be("qpsk2400");
        parsed.Callsign.Should().Be("M0LTE-7");
        parsed.HasOverrides.Should().BeTrue();
    }

    [Fact]
    public void The_Three_Overrides_Are_Also_Read_With_An_Equals_Sign()
    {
        CommandLine parsed = CommandLine.Parse(
            ["--device=pipe:/tmp/a,/tmp/b", "--mode=bpsk300", "--callsign=G0OLD"]);

        parsed.Device.Should().Be("pipe:/tmp/a,/tmp/b");
        parsed.Mode.Should().Be("bpsk300");
        parsed.Callsign.Should().Be("G0OLD");
    }

    [Fact]
    public void An_Override_Applies_To_A_Config_Without_Changing_The_Rest_Of_It()
    {
        var config = new QsoConfig
        {
            Device = "default",
            Callsign = "M0LTE",
            Mode = "bpsk300",
            TxDelayMs = 250,
        };

        QsoConfig applied = CommandLine.Parse(["--mode", "afsk1200"]).ApplyTo(config);

        applied.Mode.Should().Be("afsk1200");
        applied.Device.Should().Be("default");
        applied.Callsign.Should().Be("M0LTE");
        applied.TxDelayMs.Should().Be(250);
    }

    [Fact]
    public void Monitor_Only_Is_A_Switch_And_Takes_No_Value()
    {
        CommandLine.Parse(["--monitor-only"]).MonitorOnly.Should().BeTrue();
    }

    [Fact]
    public void A_Config_Path_Replaces_The_Default()
    {
        CommandLine.Parse(["--config", "/etc/pdn-qso.json"]).ResolvedConfigPath
            .Should().Be("/etc/pdn-qso.json");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_Is_Asked_For_Either_Way(string argument) =>
        CommandLine.Parse([argument]).ShowHelp.Should().BeTrue();

    [Theory]
    [InlineData("--version")]
    [InlineData("-V")]
    public void The_Version_Is_Asked_For_Either_Way(string argument) =>
        CommandLine.Parse([argument]).ShowVersion.Should().BeTrue();

    [Fact]
    public void An_Argument_That_Is_Not_One_Says_So_Rather_Than_Being_Ignored()
    {
        CommandLine parsed = CommandLine.Parse(["--modem", "bpsk300"]);

        parsed.Error.Should().NotBeNull().And.Contain("--modem");
    }

    [Fact]
    public void An_Option_With_Its_Value_Missing_Says_Which_One()
    {
        CommandLine.Parse(["--device"]).Error.Should().NotBeNull().And.Contain("--device");
    }

    [Fact]
    public void The_Help_Text_Names_Every_Option_It_Takes()
    {
        string help = CommandLine.HelpText("1.2.3");

        help.Should().Contain("1.2.3");
        foreach (string option in new[]
                 { "--config", "--device", "--mode", "--callsign", "--monitor-only" })
        {
            help.Should().Contain(option);
        }
    }
}
