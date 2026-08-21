using Packet.SoundModem.Modems;
using PdnQso.Link.Audio;
using PdnQso.Link.Devices;

namespace PdnQso.Tests;

/// <summary>
/// What <see cref="DeviceFactory"/> builds from each of the four device strings, and what it
/// refuses. Only the pipe path can be opened on a machine with no radio, so that is the one
/// that is opened; the rest are pinned at the point where the factory decides they cannot work
/// - which is the decision that matters, because a device opened at the wrong rate decodes
/// nothing and says nothing about why.
/// </summary>
public class DeviceFactoryTests
{
    private static string Fifo(string name) =>
        Path.Combine(Path.GetTempPath(), $"pdn-qso-test-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void A_Pipe_Device_Opens_At_The_Modes_Own_Rate()
    {
        string a = Fifo("a");
        string b = Fifo("b");
        try
        {
            int rate = ModemCatalog.DspRateFor("bpsk300");
            var device = (PipeDeviceString)DeviceString.Parse($"pipe:{a},{b},{rate}");

            using IAudioDevice opened = DeviceFactory.Create(device, rate);

            opened.SampleRate.Should().Be(rate);
            opened.CanTransmit.Should().BeTrue();
            opened.Power.Unit.Should().Be(PowerUnit.None, "there is no transmitter on a FIFO");
            opened.Name.Should().Be(device.Text);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void A_Pipe_Running_Faster_Than_The_Mode_Is_Decimated_To_It()
    {
        string a = Fifo("a");
        string b = Fifo("b");
        try
        {
            var device = (PipeDeviceString)DeviceString.Parse($"pipe:{a},{b},48000");

            using IAudioDevice opened = DeviceFactory.Create(device, 12000);

            opened.SampleRate.Should().Be(12000, "the station refuses anything else");
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void A_Rate_That_Is_Not_A_Whole_Multiple_Is_Refused_With_The_Fix_In_The_Message()
    {
        var device = (PipeDeviceString)DeviceString.Parse($"pipe:{Fifo("a")},{Fifo("b")},44100");

        Action open = () => DeviceFactory.Create(device, 12000);

        open.Should().Throw<ArgumentException>().WithMessage("*whole number*");
    }

    [Fact]
    public void An_Alsa_Card_At_A_Rate_The_Mode_Does_Not_Divide_Is_Refused_Before_The_Card_Is_Touched()
    {
        var device = (AlsaDeviceString)DeviceString.Parse("alsa:plughw:99,0");

        Action open = () => DeviceFactory.Create(
            device, 12000, new DeviceOptions { CaptureRateHz = 44100 });

        open.Should().Throw<ArgumentException>().WithMessage("*whole number*");
    }

    [Fact]
    public async Task An_UberSdr_With_Nowhere_To_Listen_Is_Refused_Before_The_Instance_Is_Called()
    {
        var device = (UberSdrDeviceString)DeviceString.Parse("ubersdr:example.invalid");

        Func<Task> open = async () => await DeviceFactory.CreateAsync(device, 12000);

        await open.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*has to be told where to listen*");
    }

    [Fact]
    public void A_Ptt_Kind_With_No_Device_To_Key_Is_Refused()
    {
        Action build = () => DeviceFactory.CreatePtt(new DeviceOptions { Ptt = PttKind.Cm108 });

        build.Should().Throw<ArgumentException>().WithMessage("*hidraw*");
    }

    [Fact]
    public void No_Ptt_Means_No_Ptt_Rather_Than_A_Broken_One() =>
        DeviceFactory.CreatePtt(new DeviceOptions()).Should().BeNull();

    [Theory]
    [InlineData(14_105_000, 1500, false, 14_103_500)]
    [InlineData(14_105_000, 1500, true, 14_106_500)]
    [InlineData(7_047_500, 1000, false, 7_046_500)]
    public void The_Dial_Puts_The_Modems_Audio_Centre_On_The_Frequency_Asked_For(
        double rf, double centre, bool lsb, double dial) =>
        DialFrequency.For(rf, centre, lsb).Should().Be(dial);
}
