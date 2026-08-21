using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Perf;

namespace PdnQso.Tests.Perf;

/// <summary>
/// The stream procedure of docs/design.md section 3: a sender pushes numbered frames at a
/// receiver, which reports back what it actually heard so the sender's own table is complete.
/// </summary>
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
        using AudioLink link = AudioLink.Create(Mode);
        await using var sender = new Station(Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        await using var receiver = new Station(Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);
        sender.Start();
        receiver.Start();

        var senderRun = new PerfRun();
        var receiverRun = new PerfRun();
        var options = new PerfStreamOptions { FrameCount = 12, PayloadSize = 40 };

        Task<PerfReport> receiverTask = receiverRun.RunStreamReceiverAsync(
            receiver, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        PerfReport senderReport = await senderRun.RunStreamSenderAsync(
            sender, link.ModemA, link.SampleRate, options,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        PerfReport receiverReport = await receiverTask;

        senderReport.FramesSent.Should().Be(12);
        senderReport.FramesHeard.Should().Be(12);
        senderReport.FramesDelivered.Should().Be(12);
        senderReport.FramesLost.Should().Be(0);
        senderReport.Duplicates.Should().Be(0);
        senderReport.FrameErrorRate.Should().Be(0);

        receiverReport.FramesHeard.Should().Be(12);
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

    [Fact]
    public async Task Lost_Stream_Frames_Are_Counted_As_Gaps_Not_As_Successes()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var sender = new Station(Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        await using var receiver = new Station(Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);
        sender.Start();
        receiver.Start();

        const int frameCount = 10;
        const int payloadSize = 32;
        var dropped = new HashSet<int> { 2, 5, 7 };

        int burstSamples = link.ModemA.Modulate(
            new LinkFrame("M0LTE", LinkFrameType.PerfStream, 1, new byte[payloadSize]).Encode(), 300).Length;
        var lossy = new AudioChannel { Dropouts = [new SampleRange(0, burstSamples)] };

        var receiverRun = new PerfRun();
        Task<PerfReport> receiverTask = receiverRun.RunStreamReceiverAsync(
            receiver, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

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
