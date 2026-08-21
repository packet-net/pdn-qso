using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Devices;

namespace PdnQso.Tests;

/// <summary>
/// Two stations on one machine, joined by a pair of named pipes: the way two copies of pdn-qso
/// are tested against each other without a radio, and the thing that proves the transmit path
/// and the receive path agree about more than a buffer round trip.
/// </summary>
/// <remarks>
/// This runs in real time - the capture side paces itself to wall clock exactly as a sound card
/// does - so it is deliberately one short frame at the fastest audio mode rather than a sweep.
/// It is not a channel: there is no noise and no propagation here, and nothing measured through
/// it is a statement about a modem.
/// </remarks>
public class PipeDeviceTests
{
    private const string Mode = "afsk1200";

    [Fact]
    public async Task Two_Stations_Talk_To_Each_Other_Through_A_Pipe_Pair()
    {
        string atob = Fifo("atob");
        string btoa = Fifo("btoa");
        int rate = ModemCatalog.DspRateFor(Mode);

        // Reversed: what one writes, the other reads. Same spelling pdn-soundmodem's daemon
        // takes, so one end of a pair can be either program.
        DeviceString forA = DeviceString.Parse($"pipe:{btoa},{atob},{rate}");
        DeviceString forB = DeviceString.Parse($"pipe:{atob},{btoa},{rate}");

        try
        {
            using IAudioDevice deviceA = DeviceFactory.Create(forA, rate);
            using IAudioDevice deviceB = DeviceFactory.Create(forB, rate);

            await using var a = Station.Create(Options("M0LTE-7"), deviceA, Mode);
            await using var b = Station.Create(Options("G0OLD-1"), deviceB, Mode);

            var heard = new TaskCompletionSource<LinkFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            b.FrameReceived += (frame, _) => heard.TrySetResult(frame);

            a.Start();
            b.Start();

            await a.SendAsync(a.Frame(LinkFrameType.Hello, 0x5A, "hello over a pipe"u8));

            LinkFrame received = await heard.Task.WaitAsync(TimeSpan.FromSeconds(30));

            received.Source.Should().Be("M0LTE-7");
            received.Type.Should().Be(LinkFrameType.Hello);
            received.Session.Should().Be(0x5A);
            System.Text.Encoding.UTF8.GetString(received.Payload.Span)
                .Should().Be("hello over a pipe");
        }
        finally
        {
            File.Delete(atob);
            File.Delete(btoa);
        }
    }

    [Fact]
    public async Task A_Station_On_A_Pipe_Keys_And_Unkeys_Around_Its_Burst()
    {
        string atob = Fifo("keyed-atob");
        string btoa = Fifo("keyed-btoa");
        int rate = ModemCatalog.DspRateFor(Mode);

        try
        {
            using IAudioDevice device = DeviceFactory.Create(
                DeviceString.Parse($"pipe:{btoa},{atob},{rate}"), rate);

            var edges = new List<bool>();
            device.PttChanged += keyed => edges.Add(keyed);

            await using var station = Station.Create(Options("M0LTE-7"), device, Mode);
            station.Start();
            station.Transmitting.Should().BeFalse();

            await station.SendAsync(station.Frame(LinkFrameType.Hello, 1));

            edges.Should().Equal([true, false], "keyed for the burst and dropped after it");
            station.Transmitting.Should().BeFalse("a transmitter left keyed is the one failure that matters");
        }
        finally
        {
            File.Delete(atob);
            File.Delete(btoa);
        }
    }

    [Fact]
    public async Task Two_Stations_Talk_Through_A_Pipe_Pair_Running_Faster_Than_The_Mode()
    {
        // The case a sound card is always in: the device runs at 48 kHz and the mode at 12, so
        // the burst goes out through the upsampler and comes back through the decimator. The
        // 1:1 test above never touches either of them, and the first hand-run of two copies of
        // the program over a 48 kHz pipe pair heard nothing at all.
        string atob = Fifo("fast-atob");
        string btoa = Fifo("fast-btoa");
        int modeRate = ModemCatalog.DspRateFor(Mode);
        const int deviceRate = 48000;
        (deviceRate % modeRate).Should().Be(0, "the resampler only does whole ratios");

        DeviceString forA = DeviceString.Parse($"pipe:{btoa},{atob},{deviceRate}");
        DeviceString forB = DeviceString.Parse($"pipe:{atob},{btoa},{deviceRate}");

        try
        {
            using IAudioDevice deviceA = DeviceFactory.Create(forA, modeRate);
            using IAudioDevice deviceB = DeviceFactory.Create(forB, modeRate);

            await using var a = Station.Create(Options("M0LTE-7"), deviceA, Mode);
            await using var b = Station.Create(Options("G0OLD-1"), deviceB, Mode);

            var heard = new TaskCompletionSource<LinkFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            b.FrameReceived += (frame, _) => heard.TrySetResult(frame);

            a.Start();
            b.Start();

            await a.SendAsync(a.Frame(LinkFrameType.Hello, 0x5A, "hello at four times the rate"u8));

            LinkFrame received = await heard.Task.WaitAsync(TimeSpan.FromSeconds(30));

            received.Source.Should().Be("M0LTE-7");
            System.Text.Encoding.UTF8.GetString(received.Payload.Span)
                .Should().Be("hello at four times the rate");
        }
        finally
        {
            File.Delete(atob);
            File.Delete(btoa);
        }
    }

    private static StationOptions Options(string callsign) => new()
    {
        Callsign = callsign,
        TxDelayMilliseconds = 100,
    };

    private static string Fifo(string name) =>
        Path.Combine(Path.GetTempPath(), $"pdn-qso-test-{Guid.NewGuid():N}-{name}");
}
