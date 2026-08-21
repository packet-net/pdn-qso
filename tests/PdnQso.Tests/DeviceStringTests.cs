using PdnQso.Link.Devices;

namespace PdnQso.Tests;

/// <summary>
/// The four device-string forms, which are pdn-soundmodem's: a string that works for the
/// daemon has to work here, or an operator has two things to learn instead of one.
/// </summary>
public class DeviceStringTests
{
    [Theory]
    [InlineData("default", "default")]
    [InlineData("hw:1,0", "hw:1,0")]
    [InlineData("plughw:CARD=Device,DEV=0", "plughw:CARD=Device,DEV=0")]
    [InlineData("alsa:plughw:1,0", "plughw:1,0")]
    [InlineData("alsa:default", "default")]
    public void An_Alsa_Card_Is_Parsed_With_Its_Prefix_Stripped(string text, string card)
    {
        var device = (AlsaDeviceString)DeviceString.Parse(text);

        device.Kind.Should().Be(DeviceKind.Alsa);
        device.Card.Should().Be(card);
        device.Text.Should().Be(text);
        device.CanTransmit.Should().BeTrue();
    }

    [Theory]
    [InlineData("flex:discover", "discover", "A", null)]
    [InlineData("flex:10.45.0.20", "10.45.0.20", "A", null)]
    [InlineData("flex:10.45.0.20:B", "10.45.0.20", "B", null)]
    [InlineData("flex:serial=1234-5678:C", "serial=1234-5678", "C", null)]
    [InlineData("flex:discover@shack", "discover", "A", "shack")]
    [InlineData("flex:10.45.0.20:b@shack", "10.45.0.20", "B", "shack")]
    [InlineData("flex:mock", "mock", "A", null)]
    public void A_Flex_Is_Split_Into_Radio_Slice_And_Station(
        string text, string radio, string slice, string? station)
    {
        var device = (FlexDeviceString)DeviceString.Parse(text);

        device.Kind.Should().Be(DeviceKind.Flex);
        device.Radio.Should().Be(radio);
        device.Slice.Should().Be(slice);
        device.Station.Should().Be(station);
        device.Headless.Should().Be(station is null);
        device.CanTransmit.Should().BeTrue();
    }

    [Theory]
    [InlineData("ubersdr:m9psy-1.instance.ubersdr.org", "m9psy-1.instance.ubersdr.org", 443, true)]
    // host:port keeps HTTPS: the library assumes TLS unless the string carries a scheme,
    // because every public instance is behind it.
    [InlineData("ubersdr:localhost:8080", "localhost", 8080, true)]
    [InlineData("ubersdr:https://sdr.example.org/", "sdr.example.org", 443, true)]
    [InlineData("ubersdr:http://sdr.example.org:8073/", "sdr.example.org", 8073, false)]
    public void An_UberSdr_Is_Split_Into_Host_Port_And_Scheme(
        string text, string host, int port, bool ssl)
    {
        var device = (UberSdrDeviceString)DeviceString.Parse(text);

        device.Kind.Should().Be(DeviceKind.UberSdr);
        device.Host.Should().Be(host);
        device.Port.Should().Be(port);
        device.Ssl.Should().Be(ssl);
    }

    [Fact]
    public void An_UberSdr_Cannot_Transmit()
    {
        // Somebody else's receiver. This is the flag that stops a station keying a transmitter
        // that does not exist and believing it has been heard.
        DeviceString.Parse("ubersdr:m9psy-1.instance.ubersdr.org").CanTransmit.Should().BeFalse();
    }

    [Theory]
    [InlineData("pipe:/tmp/a-to-b,/tmp/b-to-a", "/tmp/a-to-b", "/tmp/b-to-a", 48000)]
    [InlineData("pipe:/tmp/in,/tmp/out,12000", "/tmp/in", "/tmp/out", 12000)]
    public void A_Pipe_Pair_Is_Split_Into_In_Out_And_Rate(
        string text, string input, string output, int rate)
    {
        var device = (PipeDeviceString)DeviceString.Parse(text);

        device.Kind.Should().Be(DeviceKind.Pipe);
        device.In.Should().Be(input);
        device.Out.Should().Be(output);
        device.Rate.Should().Be(rate);
        device.CanTransmit.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "no device")]
    [InlineData("   ", "no device")]
    [InlineData("flex:", "names no radio")]
    [InlineData("flex:@shack", "names no radio")]
    [InlineData("ubersdr:", "names no instance")]
    [InlineData("ubersdr:ftp://sdr.example.org", "not an http")]
    [InlineData("ubersdr:host:notaport", "not a TCP port")]
    [InlineData("pipe:", "not a pipe device")]
    [InlineData("pipe:only-one", "not a pipe device")]
    [InlineData("pipe:,", "not a pipe device")]
    [InlineData("pipe:a,b,c,d", "not a pipe device")]
    [InlineData("pipe:a,b,notarate", "not a sample rate")]
    [InlineData("pipe:a,b,0", "not a sample rate")]
    public void Nonsense_Is_Refused_With_A_Line_An_Operator_Can_Act_On(string text, string says)
    {
        DeviceString.TryParse(text, out DeviceString? device, out string? error).Should().BeFalse();

        device.Should().BeNull();
        error.Should().Contain(says);
        error.Should().MatchRegex("^[\\x20-\\x7E\\r\\n]*$", "printable strings stay ASCII");
    }

    [Fact]
    public void Parse_Throws_What_TryParse_Would_Have_Said()
    {
        Action parse = () => DeviceString.Parse("pipe:only-one");

        parse.Should().Throw<FormatException>().WithMessage("*pipe:<in>,<out>*");
    }

    [Fact]
    public void The_String_Is_Kept_As_Written_So_It_Can_Go_Straight_Back_In_The_Settings_File()
    {
        DeviceString device = DeviceString.Parse("flex:10.45.0.20:B@shack");

        device.Text.Should().Be("flex:10.45.0.20:B@shack");
        device.ToString().Should().Be("flex:10.45.0.20:B@shack");
    }
}
