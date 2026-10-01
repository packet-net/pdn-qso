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
    public void Upgrade_Is_Not_An_Argument_Any_More()
    {
        // Updates come from the packet-net apt repository now, through apt like everything
        // else on the machine. A leftover script calling --upgrade is told so, not ignored.
        CommandLine.Parse(["--upgrade"]).Error.Should().Contain("unknown argument");
        CommandLine.HelpText("0.3.0").Should().NotContain("--upgrade");
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

    [Fact]
    public void Each_Headless_Action_Is_Read_With_What_It_Needs()
    {
        CommandLine.Parse(["--respond"]).Headless.Should().Be(new HeadlessCommand(HeadlessAction.Respond));
        CommandLine.Parse(["--listen", "--for", "60"]).Headless
            .Should().Be(new HeadlessCommand(HeadlessAction.Listen) { RunFor = TimeSpan.FromSeconds(60) });
        CommandLine.Parse(["--ping", "20"]).Headless
            .Should().Be(new HeadlessCommand(HeadlessAction.Ping) { Count = 20 });
        CommandLine.Parse(["--chat", "hello from the lab"]).Headless
            .Should().Be(new HeadlessCommand(HeadlessAction.Chat) { Text = "hello from the lab" });
        CommandLine.Parse(["--stream=50", "--payload", "64"]).Headless
            .Should().Be(new HeadlessCommand(HeadlessAction.Stream) { Count = 50, PayloadBytes = 64 });
        CommandLine.Parse([]).Headless.Should().BeNull("no action is the screen");
    }

    [Fact]
    public void A_Callsign_With_An_Ssid_Is_Taken_Whole_For_A_Headless_Run()
    {
        CommandLine parsed = CommandLine.Parse(
            ["--device", "tait:/dev/ttyUSB1", "--mode", "tait-sdm", "--callsign", "M0LTE-7", "--respond"]);

        parsed.Error.Should().BeNull();
        QsoConfig config = parsed.ApplyTo(new QsoConfig());
        config.Callsign.Should().Be("M0LTE-7");
        config.Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData(new[] { "--ping", "3", "--chat", "hi" }, "one of")]
    [InlineData(new[] { "--ping", "0" }, "1 or more")]
    [InlineData(new[] { "--ping", "lots" }, "1 or more")]
    [InlineData(new[] { "--payload", "64" }, "--payload goes with --stream")]
    [InlineData(new[] { "--ping", "3", "--for", "10" }, "--for goes with")]
    [InlineData(new[] { "--monitor-only", "--ping", "3" }, "never transmits")]
    [InlineData(new[] { "--monitor-only", "--respond" }, "never transmits")]
    public void A_Headless_Command_That_Does_Not_Make_Sense_Is_Refused(string[] args, string says)
    {
        CommandLine.Parse(args).Error.Should().NotBeNull().And.Contain(says);
    }

    [Fact]
    public void The_Help_Lists_The_Headless_Actions()
    {
        string help = CommandLine.HelpText("1.0.0");

        foreach (string option in new[] { "--respond", "--listen", "--ping", "--chat", "--stream", "--payload", "--for" })
        {
            help.Should().Contain(option);
        }
    }
}
