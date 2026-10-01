using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Chat;
using PdnQso.Link.Tait;
using PdnQso.Tests.Time;

namespace PdnQso.Tests.Tait;

/// <summary>
/// A station whose modem is a Tait radio's own: the same contract as an audio station, and a
/// chat over two of them on SDM, end to end, on the test clock.
/// </summary>
public class TaitStationTests
{
    private static StationOptions Options(string callsign) => new()
    {
        Callsign = callsign,
        BusyPollInterval = TimeSpan.FromMilliseconds(20),
        BusyWaitTimeout = TimeSpan.FromSeconds(30),
    };

    private static TaitSettings Settings => new() { PortName = "/dev/ttyUSB0" };

    [Fact]
    public async Task A_Frame_Sent_By_One_Station_Is_Heard_By_The_Other_And_Shown_As_Sent()
    {
        var clock = new VirtualClock();
        (FakeSdmRadio a, FakeSdmRadio b) = FakeSdmRadio.Pair();
        await using var sender = new TaitStation(Options("M0LTE-7"), new TaitSdmLink(a, Settings, clock), timeProvider: clock);
        await using var receiver = new TaitStation(Options("G0OLD-1"), new TaitSdmLink(b, Settings, clock), timeProvider: clock);
        sender.Start();
        receiver.Start();
        var heard = new List<(LinkFrame Frame, FrameQuality Quality)>();
        receiver.FrameReceived += (frame, quality) => heard.Add((frame, quality));
        LinkFrame? shown = null;
        sender.FrameTransmitted += (frame, _) => shown = frame;

        await VirtualTime.RunAsync(clock, sender.SendAsync(sender.Frame(LinkFrameType.Chat, 7, "59 in reading"u8)));

        heard.Should().ContainSingle();
        heard[0].Frame.Source.Should().Be("M0LTE-7");
        heard[0].Frame.Payload.ToArray().Should().Equal("59 in reading"u8.ToArray());
        heard[0].Quality.Mode.Should().Be(TaitModes.Sdm);
        heard[0].Quality.SnrDb.Should().BeNull("the radio reports no SNR, and none is made up");
        shown.Should().NotBeNull();
    }

    [Fact]
    public async Task There_Is_No_Soundmodem_Underneath()
    {
        await using var station = new TaitStation(
            Options("M0LTE"), new TaitSdmLink(new FakeSdmRadio(), Settings, new VirtualClock()));

        station.Modem.Should().BeNull();
        station.Power.CanSet.Should().BeFalse();
        station.DeviceName.Should().Be("tait:/dev/ttyUSB0");
        WaveformLadder.ForStation(station).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_Frame_Bigger_Than_The_Mode_Carries_Is_Refused_With_The_Limit()
    {
        await using var station = new TaitStation(
            Options("M0LTE"), new TaitSdmLink(new FakeSdmRadio(), Settings, new VirtualClock()));

        Func<Task> act = () => station.SendRawAsync(new byte[SdmFrameCodec.MaxFrameBytes + 1]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*126 bytes*tait-sdm*125*");
    }

    [Fact]
    public async Task Monitor_Only_Locks_The_Transmitter_Out()
    {
        var radio = new FakeSdmRadio();
        await using var station = new TaitStation(
            Options("M0LTE"), new TaitSdmLink(radio, Settings, new VirtualClock()), canTransmit: false);

        Func<Task> act = () => station.SendAsync(station.Frame(LinkFrameType.Chat, 1));

        await act.Should().ThrowAsync<InvalidOperationException>();
        radio.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Busy_Channel_Is_Waited_Out_Before_The_Radio_Is_Given_The_Frame()
    {
        var clock = new VirtualClock();
        var radio = new FakeSdmRadio { ChannelBusy = true };
        await using var station = new TaitStation(
            Options("M0LTE"), new TaitSdmLink(radio, Settings, clock), timeProvider: clock);

        Task send = station.SendAsync(station.Frame(LinkFrameType.Chat, 1));
        await VirtualTime.UntilAsync(clock, () => clock.Elapsed >= TimeSpan.FromSeconds(2), "two seconds of carrier");
        radio.Sent.Should().BeEmpty();

        radio.ChannelBusy = false;
        await VirtualTime.RunAsync(clock, send);
        radio.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task A_Chat_Line_Goes_Across_Two_Tait_Stations_On_Sdm_And_Is_Acknowledged()
    {
        var clock = new VirtualClock();
        (FakeSdmRadio a, FakeSdmRadio b) = FakeSdmRadio.Pair();
        var linkA = new TaitSdmLink(a, Settings, clock);
        var linkB = new TaitSdmLink(b, Settings, clock);
        await using var stationA = new TaitStation(Options("M0LTE-7"), linkA, timeProvider: clock);
        await using var stationB = new TaitStation(Options("G0OLD-1"), linkB, timeProvider: clock);
        stationA.Start();
        stationB.Start();
        var options = new ChatOptions { MaxTextBytes = TaitModes.MaxPayloadBytes(TaitModes.Sdm) - ChatPayload.HeaderLength };
        await using var chatA = new ChatSession(stationA, options with { SessionId = 0x2A }, timeProvider: clock, random: new Random(11));
        await using var chatB = new ChatSession(stationB, options with { SessionId = 0x5B }, timeProvider: clock, random: new Random(23));
        var received = new List<string>();
        chatB.MessageReceived += message => received.Add(message.Text);
        chatA.Start();
        chatB.Start();

        ChatDelivery delivery = await VirtualTime.RunAsync(
            clock,
            chatA.SendAsync("good evening, 59 in reading"),
            // A side that is sending is busy, except while it is parked on its own air time,
            // which is time passing and the one thing the clock is there to provide.
            busy: () => (chatA.Sending && !linkA.ParkedOnAir) || (chatB.Sending && !linkB.ParkedOnAir));

        delivery.IsDelivered.Should().BeTrue();
        received.Should().Equal("good evening, 59 in reading");
    }
}
