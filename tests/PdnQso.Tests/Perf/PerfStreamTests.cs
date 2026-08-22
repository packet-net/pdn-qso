using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Perf;
using PdnQso.Tests.Time;
using PdnQso.Tests.Rig;

namespace PdnQso.Tests.Perf;

/// <summary>
/// The stream procedure of docs/design.md section 3: a sender pushes numbered frames at a
/// receiver, which reports back what it actually heard so the sender's own table is complete.
/// </summary>
/// <remarks>
/// On a <see cref="VirtualClock"/>, and nothing here ever moves it: every frame is answered on
/// its own account, so the run finishes on facts. Waiting for a fact has no deadline, which is
/// the point - "the receiver never reported" is a finding, "the receiver did not report inside
/// ten seconds on this box" is not.
/// </remarks>
public class PerfStreamTests
{
    private const string Mode = "bpsk300";

    private static StationOptions Options(string callsign) => new()
    {
        Callsign = callsign,
        BusyWaitTimeout = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task A_Clean_Stream_Reports_All_Frames_Heard_And_Goodput_Matches_Air_Time()
    {
        var clock = new VirtualClock();
        using AudioLink link = AudioLink.Create(Mode);

        // Both ends on one medium, as the transfer rigs are. Without it the two stations can be
        // inside the same channel object at the same moment, which is not a collision but a data
        // race, and it cost this suite a stream frame about one run in ten.
        using var medium = new HalfDuplexChannel();
        await using var senderStation = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: clock);
        await using var receiverStation = new Station(
            Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: clock);
        senderStation.Start();
        receiverStation.Start();
        IStation sender = medium.Wrap(senderStation);
        IStation receiver = medium.Wrap(receiverStation);

        var senderRun = new PerfRun(clock);
        var receiverRun = new PerfRun(clock);
        // The session byte is random when it is not given, and it goes into every frame, so
        // leaving it out means the twelve frames - and the audio they modulate to - are
        // different on every run. Pinned so that what this test puts on the air is the same
        // thing twice, which is worth having on its own.
        var options = new PerfStreamOptions { FrameCount = 12, PayloadSize = 40, Session = 0x33 };

        Task<PerfReport> receiverTask = receiverRun.RunStreamReceiverAsync(receiver);

        // The far end is started by the same keystroke as this one, which on the air it never
        // is. Wait until it is actually listening, or the first frame of the run goes out to
        // nobody and the count comes back one short.
        await VirtualTime.WaitForAsync(() => receiverRun.Listening);

        PerfReport senderReport = await senderRun.RunStreamSenderAsync(
            sender, link.ModemA, link.SampleRate, options);
        PerfReport receiverReport = await receiverTask;

        clock.Elapsed.Should().Be(TimeSpan.Zero, "a clean stream never waits for a timeout");

        senderReport.FramesSent.Should().Be(12, "the sender was asked for twelve");
        senderReport.FramesHeard.Should().Be(12, "the receiver's summary should account for all twelve");
        senderReport.FramesDelivered.Should().Be(12, "none of the twelve was a duplicate");
        senderReport.FramesLost.Should().Be(0);
        senderReport.Duplicates.Should().Be(0);
        senderReport.FrameErrorRate.Should().Be(0);

        receiverReport.FramesHeard.Should().Be(12, "the receiver's own count should be all twelve");
        receiverReport.FramesLost.Should().Be(0);

        // The modem's own number for this payload size, measured the same way PerfRun does -
        // independent of PerfRun's own measurement, so this is not just checking its arithmetic
        // against itself.
        byte[] probe = new LinkFrame("N0CALL", LinkFrameType.PerfStream, 0, new byte[40]).Encode();
        double airTimeSeconds = (double)link.ModemA.Modulate(probe, options.TxDelayMilliseconds).Length / link.SampleRate;
        double expectedGoodput = 40 / airTimeSeconds;

        senderReport.GoodputBytesPerSecond.Should().BeApproximately(
            expectedGoodput, expectedGoodput * 0.05, "goodput should be payload bytes over the modem's own air time");

        senderReport.Mode.Should().StartWith("bpsk300");
        senderReport.Device.Should().Be("audiolink:A");
        senderReport.Procedure.Should().Be("stream");
    }

    /// <summary>
    /// The count in the summary is the receiving station's own, and the wrap-up request never
    /// overtakes a frame that arrived before it.
    /// </summary>
    /// <remarks>
    /// Issue #17 was a run that came back one frame short, and the argument that said it could
    /// not happen is the one pinned here: the frames and the wrap-up request cross on the same
    /// transmitting thread, in the order they were sent, so a frame that arrived is counted
    /// before the request that asks for the count. That held, and the missing frame turned out
    /// never to have been decoded at all (design.md 6f). It is worth a test of its own because
    /// nothing else says it: the counts elsewhere are all read against what the sender sent, so
    /// a receiver that answered the request from a queue - which is exactly what the pong
    /// responder does, and for a good reason - would pass every one of them while quietly
    /// reporting a stale number. Counted at the station, below the perf run and above the
    /// modem, so this holds whether the modem decoded everything or not.
    /// </remarks>
    [Fact]
    public async Task The_Summary_Is_Exactly_What_The_Receiving_Station_Heard()
    {
        var clock = new VirtualClock();
        using AudioLink link = AudioLink.Create(Mode);
        using var medium = new HalfDuplexChannel();
        await using var senderStation = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: clock);
        await using var receiverStation = new Station(
            Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: clock);

        // What the receiving station heard, in the order it heard it, taken from the station's
        // own event rather than from anything the perf run counted.
        var arrivals = new List<string>();
        var distinct = new HashSet<ushort>();
        receiverStation.FrameReceived += (frame, _) =>
        {
            if (frame.Type == LinkFrameType.PerfStream && frame.Payload.Length >= 2)
            {
                ushort seq = (ushort)((frame.Payload.Span[0] << 8) | frame.Payload.Span[1]);
                distinct.Add(seq);
                arrivals.Add("stream");
            }
            else if (frame.Type == LinkFrameType.PerfPing)
            {
                arrivals.Add("wrap-up");
            }
        };

        senderStation.Start();
        receiverStation.Start();
        IStation sender = medium.Wrap(senderStation);
        IStation receiver = medium.Wrap(receiverStation);

        var senderRun = new PerfRun(clock);
        var receiverRun = new PerfRun(clock);

        // Session pinned for the same reason as the test above: unpinned it is a fresh draw of
        // what goes on the air every run, and four of the 256 draws put out a frame this rig's
        // noiseless channel does not carry. See design.md 6f.
        var options = new PerfStreamOptions { FrameCount = 12, PayloadSize = 40, Session = 0x33 };

        Task<PerfReport> receiverTask = receiverRun.RunStreamReceiverAsync(receiver);
        await VirtualTime.WaitForAsync(() => receiverRun.Listening);

        PerfReport senderReport = await senderRun.RunStreamSenderAsync(
            sender, link.ModemA, link.SampleRate, options);
        PerfReport receiverReport = await receiverTask;

        senderReport.FramesHeard.Should().Be(
            distinct.Count,
            "the summary carries the receiving station's own count of what arrived, not a count "
            + "taken after the fact");
        receiverReport.FramesHeard.Should().Be(distinct.Count);

        arrivals.Should().NotBeEmpty();
        arrivals[^1].Should().Be(
            "wrap-up",
            "the request for the count is the last thing the receiver hears, so every frame that "
            + "arrived is already in the number it answers with");
        arrivals.Should().ContainSingle(what => what == "wrap-up", "one clean run asks once");
    }

    [Fact]
    public async Task Lost_Stream_Frames_Are_Counted_As_Gaps_Not_As_Successes()
    {
        var clock = new VirtualClock();
        using AudioLink link = AudioLink.Create(Mode);

        // Both ends on one medium, as the transfer rigs are. Without it the two stations can be
        // inside the same channel object at the same moment, which is not a collision but a data
        // race, and it cost this suite a stream frame about one run in ten.
        using var medium = new HalfDuplexChannel();
        await using var senderStation = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: clock);
        await using var receiverStation = new Station(
            Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: clock);
        senderStation.Start();
        receiverStation.Start();
        IStation sender = medium.Wrap(senderStation);
        IStation receiver = medium.Wrap(receiverStation);

        const int frameCount = 10;
        const int payloadSize = 32;
        var dropped = new HashSet<int> { 2, 5, 7 };

        int burstSamples = link.ModemA.Modulate(
            new LinkFrame("M0LTE", LinkFrameType.PerfStream, 1, new byte[payloadSize]).Encode(), 300).Length;
        var lossy = new AudioChannel { Dropouts = [new SampleRange(0, burstSamples)] };

        var receiverRun = new PerfRun(clock);
        Task<PerfReport> receiverTask = receiverRun.RunStreamReceiverAsync(receiver);

        // The far end is started by the same keystroke as this one, which on the air it never
        // is. Wait until it is actually listening, or the first frame of the run goes out to
        // nobody and the count comes back one short.
        await VirtualTime.WaitForAsync(() => receiverRun.Listening);

        byte session = 0x55;
        for (int i = 0; i < frameCount; i++)
        {
            link.Channel = dropped.Contains(i) ? lossy : AudioChannel.Clean;
            byte[] payload = new byte[payloadSize];
            payload[0] = 0;
            payload[1] = (byte)i;
            payload[2] = 0;
            payload[3] = (byte)frameCount;
            await sender.SendAsync(sender.Frame(LinkFrameType.PerfStream, session, payload));
        }

        link.Channel = AudioChannel.Clean;
        await sender.SendAsync(sender.Frame(LinkFrameType.PerfPing, session));

        PerfReport receiverReport = await receiverTask;

        receiverReport.FramesHeard.Should().Be(frameCount - dropped.Count);
        receiverReport.FramesLost.Should().Be(dropped.Count);
        receiverReport.Duplicates.Should().Be(0);
        receiverReport.FrameErrorRate.Should().BeApproximately((double)dropped.Count / frameCount, 0.0001);
    }
}
