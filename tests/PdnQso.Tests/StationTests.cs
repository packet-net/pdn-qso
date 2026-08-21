using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Devices;

namespace PdnQso.Tests;

/// <summary>
/// The station core of docs/design.md section 5: one device, one modem, one frame at a time,
/// and it does not key up on top of somebody else.
/// </summary>
public class StationTests
{
    private const string Mode = "bpsk300";

    private static StationOptions Options(string callsign) => new()
    {
        Callsign = callsign,
        TxDelayMilliseconds = 150,
        BusyPollInterval = TimeSpan.FromMilliseconds(20),
        BusyWaitTimeout = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task A_Sends_And_B_Receives()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var a = new Station(Options("M0LTE-7"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        await using var b = new Station(Options("G0OLD-1"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);

        var heard = new List<LinkFrame>();
        var raw = new List<byte[]>();
        b.FrameReceived += (frame, _) => heard.Add(frame);
        b.RawFrameReceived += (frame, _) => raw.Add(frame);
        a.Start();
        b.Start();

        await a.SendAsync(a.Frame(LinkFrameType.Chat, 0x33, "good evening"u8));

        heard.Should().ContainSingle();
        heard[0].Source.Should().Be("M0LTE-7");
        heard[0].Type.Should().Be(LinkFrameType.Chat);
        heard[0].Session.Should().Be(0x33);
        System.Text.Encoding.UTF8.GetString(heard[0].Payload.Span).Should().Be("good evening");
        raw.Should().ContainSingle("Monitor sees every frame, ours included");
    }

    [Fact]
    public async Task Both_Ends_Can_Send_To_Each_Other()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var a = new Station(Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        await using var b = new Station(Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);

        var atA = new List<LinkFrame>();
        var atB = new List<LinkFrame>();
        a.FrameReceived += (frame, _) => atA.Add(frame);
        b.FrameReceived += (frame, _) => atB.Add(frame);
        a.Start();
        b.Start();

        await a.SendAsync(a.Frame(LinkFrameType.PerfPing, 1, [1, 2, 3]));
        await b.SendAsync(b.Frame(LinkFrameType.PerfPong, 1, [1, 2, 3]));

        atB.Should().ContainSingle().Which.Source.Should().Be("M0LTE");
        atA.Should().ContainSingle().Which.Source.Should().Be("G0OLD");
        atA[0].Type.Should().Be(LinkFrameType.PerfPong);
    }

    [Fact]
    public async Task Somebody_Elses_Traffic_Reaches_Monitor_And_Not_The_Link()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var a = new Station(Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        await using var b = new Station(Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);

        var linkFrames = new List<LinkFrame>();
        var rawFrames = new List<byte[]>();
        b.FrameReceived += (frame, _) => linkFrames.Add(frame);
        b.RawFrameReceived += (frame, _) => rawFrames.Add(frame);
        a.Start();
        b.Start();

        // The IL2P specification's example frame: a real UI frame that belongs to somebody else.
        byte[] notOurs =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F,
            0x03, 0xF0, (byte)'T', (byte)'e', (byte)'s', (byte)'t',
        ];
        await a.SendRawAsync(notOurs);

        rawFrames.Should().ContainSingle().Which.Should().Equal(notOurs);
        linkFrames.Should().BeEmpty();
    }

    [Fact]
    public async Task Transmit_Waits_For_The_Channel_To_Clear()
    {
        using AudioLink link = AudioLink.Create(Mode);
        var busy = new ManualBusyGate { Held = true };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 21, 20, 0, 0, TimeSpan.Zero));
        await using var a = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, busy, frameLog: null, timeProvider: time);
        await using var b = new Station(Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);

        var heard = new List<LinkFrame>();
        b.FrameReceived += (frame, _) => heard.Add(frame);
        a.Start();
        b.Start();

        Task send = a.SendAsync(a.Frame(LinkFrameType.Hello, 1));

        // Hold the channel through a dozen poll intervals. The clock is fake, so this is a
        // dozen polls and not a dozen milliseconds of hoping.
        await PumpAsync(time, send, steps: 12);

        send.IsCompleted.Should().BeFalse("the channel is still busy");
        busy.Queries.Should().BeGreaterThan(2, "the station is polling, not sleeping");
        heard.Should().BeEmpty();
        a.Transmitting.Should().BeFalse();

        busy.Held = false;
        await PumpAsync(time, send, steps: 200);

        await send.WaitAsync(TimeSpan.FromSeconds(30));
        heard.Should().ContainSingle();
    }

    [Fact]
    public async Task Transmit_Gives_Up_Rather_Than_Waiting_For_Ever()
    {
        using AudioLink link = AudioLink.Create(Mode);
        var busy = new ManualBusyGate { Held = true };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 21, 20, 0, 0, TimeSpan.Zero));
        StationOptions options = Options("M0LTE") with { BusyWaitTimeout = TimeSpan.FromSeconds(2) };
        await using var a = new Station(
            options, link.DeviceA, link.ModemA, busy, frameLog: null, timeProvider: time);
        a.Start();

        Task send = a.SendAsync(a.Frame(LinkFrameType.Hello, 1));
        await PumpAsync(time, send, steps: 200);

        // The message is asserted, not just the type: the test's own patience would throw a
        // TimeoutException too, and "the station gave up honestly" is the claim.
        Func<Task> wait = async () => await send.WaitAsync(TimeSpan.FromSeconds(30));
        await wait.Should().ThrowAsync<TimeoutException>().WithMessage("*still busy*");
    }

    [Fact]
    public async Task A_Clear_Channel_Is_Not_Waited_On_At_All()
    {
        using AudioLink link = AudioLink.Create(Mode);
        var busy = new ManualBusyGate { Held = false };
        // A fake clock nobody advances: if the station waited even one poll interval this
        // would never finish, which is a sharper assertion than timing it.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 21, 20, 0, 0, TimeSpan.Zero));
        await using var a = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, busy, frameLog: null, timeProvider: time);
        a.Start();

        await a.SendAsync(a.Frame(LinkFrameType.Hello, 1)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Transmitting_Clears_The_Carrier_State_Either_Side_Of_The_Burst()
    {
        using AudioLink link = AudioLink.Create(Mode);
        var busy = new ManualBusyGate();
        await using var a = new Station(Options("M0LTE"), link.DeviceA, link.ModemA, busy);
        a.Start();

        await a.SendAsync(a.Frame(LinkFrameType.Hello, 1));

        busy.Resets.Should().Be(2, "once before keying up and once after the burst");
    }

    [Fact]
    public async Task A_Receive_Only_Device_Refuses_To_Transmit()
    {
        using AudioLink link = AudioLink.Create(Mode);
        var device = new ReceiveOnlyDevice(link.SampleRate);
        await using var station = new Station(Options("M0LTE"), device, link.ModemA, OpenBusyGate.Instance);
        station.Start();

        station.CanTransmit.Should().BeFalse();
        Func<Task> send = () => station.SendAsync(station.Frame(LinkFrameType.Hello, 1));

        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*receive-only*");
    }

    [Fact]
    public void A_Modem_At_The_Wrong_Rate_Is_Refused_At_Construction()
    {
        // ms110d runs at 48 kHz; a device at 12 kHz would feed it a quarter-speed world.
        using AudioLink link = AudioLink.Create("bpsk300");
        IModem wrongRate = ModemCatalog.Create("ms110d-wn4", 48000, _ => { });

        Action build = () => new Station(Options("M0LTE"), link.DeviceA, wrongRate);

        build.Should().Throw<ArgumentException>().WithMessage("*nothing would decode*");
    }

    [Fact]
    public async Task A_Station_Hands_Out_The_Modem_It_Was_Built_Over()
    {
        // The chat ladder and the perf run both need the modem itself - one for MS110D's
        // waveform lever, the other to measure a burst's air time - and reaching for it by
        // casting to this class left every other IStation without one.
        using AudioLink link = AudioLink.Create(Mode);
        await using var station = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);

        station.Modem.Should().BeSameAs(link.ModemA);
        ((IStation)station).Modem.Should().BeSameAs(link.ModemA);
    }

    [Fact]
    public async Task A_Transmitted_Link_Frame_Is_Announced_With_Its_Decode_And_Its_Bytes()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var a = new Station(
            Options("M0LTE-7"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        a.Start();

        var sent = new List<(LinkFrame? Link, byte[] Bytes)>();
        a.FrameTransmitted += (frame, bytes) => sent.Add((frame, bytes));

        LinkFrame outgoing = a.Frame(LinkFrameType.Chat, 0x44, "on air"u8);
        byte[] encoded = outgoing.Encode();
        await a.SendAsync(outgoing);

        sent.Should().ContainSingle();
        sent[0].Bytes.Should().Equal(encoded, "Monitor renders the bytes that went out");
        sent[0].Link.Should().NotBeNull();
        sent[0].Link!.Source.Should().Be("M0LTE-7");
        sent[0].Link!.Type.Should().Be(LinkFrameType.Chat);
        sent[0].Link!.Session.Should().Be(0x44);
    }

    [Fact]
    public async Task A_Transmitted_Frame_That_Is_Not_Ours_Is_Announced_With_A_Null_Link_Frame()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var a = new Station(
            Options("M0LTE-7"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        a.Start();

        var sent = new List<(LinkFrame? Link, byte[] Bytes)>();
        a.FrameTransmitted += (frame, bytes) => sent.Add((frame, bytes));

        // An AX.25 UI frame that is not one of ours: a beacon, or a frame from a test corpus.
        byte[] beacon = new LinkFrame("G0OLD-1", LinkFrameType.Hello, 0).Encode();
        beacon[LinkFrame.HeaderLength] = 0x99;
        await a.SendRawAsync(beacon);

        sent.Should().ContainSingle();
        sent[0].Link.Should().BeNull("0x99 is not a link frame type");
        sent[0].Bytes.Should().Equal(beacon);
    }

    [Fact]
    public async Task A_Frame_Is_Announced_After_The_Transmitter_Has_Dropped()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var a = new Station(
            Options("M0LTE-7"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        a.Start();

        bool keyedWhenAnnounced = true;
        a.FrameTransmitted += (_, _) => keyedWhenAnnounced = a.Transmitting;

        await a.SendAsync(a.Frame(LinkFrameType.Chat, 1, "done"u8));

        keyedWhenAnnounced.Should().BeFalse(
            "the pane shows a transmission that happened, not one that is about to");
    }

    [Fact]
    public async Task A_Station_Reports_Its_Own_Callsign_Mode_And_Device()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var station = Station.Create(Options("m0lte-7"), link.DeviceA, Mode);

        station.Callsign.Should().Be("M0LTE-7", "a callsign is filed upper case");
        station.Mode.Should().StartWith("bpsk300");
        station.DeviceName.Should().Be("audiolink:A");
        station.Power.Should().BeSameAs(NoPowerControl.Instance);
    }

    [Fact]
    public async Task A_Station_With_A_Bad_Callsign_Will_Not_Start()
    {
        using AudioLink link = AudioLink.Create(Mode);

        Action build = () => new Station(Options("not a callsign"), link.DeviceA, link.ModemA);

        build.Should().Throw<ArgumentException>();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Winds the fake clock on one poll interval at a time until <paramref name="task"/>
    /// finishes or the steps run out, giving the station's continuation a real moment to run
    /// in between. A fake clock only fires a timer that is already registered, and the station
    /// registers its next one from a thread-pool continuation, so a single big Advance would
    /// race with it.
    /// </summary>
    private static async Task PumpAsync(FakeTimeProvider time, Task task, int steps)
    {
        for (int i = 0; i < steps && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Delay(2);
        }
    }

    /// <summary>An UberSDR stands in as a device that hears and cannot answer.</summary>
    private sealed class ReceiveOnlyDevice(int sampleRate) : IAudioDevice
    {
        public string Name => "ubersdr:test";

        public int SampleRate => sampleRate;

        public bool CanTransmit => false;

        public bool Ptt => false;

        public IPowerControl Power => NoPowerControl.Instance;

        public event Action<float[]>? SamplesReceived;

        public event Action<bool>? PttChanged;

        public void Start()
        {
            SamplesReceived?.Invoke([]);
            PttChanged?.Invoke(false);
        }

        public Task TransmitAsync(
            ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("receive only");

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_Mode_Whose_Modem_Reports_A_Different_Name_Still_Builds_A_Station()
    {
        // fsk9600-il2p's modem reports its mode as fsk9600-il2pc, which the catalogue rates
        // differently, so the rate check must trust the mode the caller asked for.
        using AudioLink link = AudioLink.Create("fsk9600-il2p");
        await using var station = Station.Create(Options("M0LTE"), link.DeviceA, "fsk9600-il2p");
        station.Mode.Should().NotBeNullOrEmpty();
        link.SampleRate.Should().Be(link.DeviceA.SampleRate);
    }
}
