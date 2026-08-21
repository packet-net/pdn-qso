using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Perf;

namespace PdnQso.Tests.Perf;

/// <summary>
/// The ping-pong procedure of docs/design.md section 3: a probe answered straight back, timed
/// per round trip.
/// </summary>
public class PerfPingPongTests
{
    private const string Mode = "bpsk300";

    private static StationOptions Options(string callsign) => new()
    {
        Callsign = callsign,
        BusyWaitTimeout = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task Ping_Pong_Reports_Mean_And_Worst_Rtt()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var pinger = new Station(Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        await using var responder = new Station(Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);
        pinger.Start();
        responder.Start();

        var responderRun = new PerfRun();
        using var responderCts = new CancellationTokenSource();
        Task responderTask = responderRun.RunPongResponderAsync(responder, responderCts.Token);

        var pingerRun = new PerfRun();
        var options = new PerfPingOptions { PingCount = 5, PingTimeout = TimeSpan.FromSeconds(5) };
        PerfReport report = await pingerRun.RunPingAsync(
            pinger, options, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

        responderCts.Cancel();
        await AwaitResponderStop(responderTask);

        report.Procedure.Should().Be("ping-pong");
        report.FramesSent.Should().Be(5);
        report.FramesHeard.Should().Be(5);
        report.FramesDelivered.Should().Be(5);
        report.FramesLost.Should().Be(0);
        report.FrameErrorRate.Should().Be(0);
        report.MeanRttMs.Should().NotBeNull();
        report.WorstRttMs.Should().NotBeNull();
        report.MeanRttMs!.Value.Should().BeGreaterThanOrEqualTo(0);
        report.WorstRttMs!.Value.Should().BeGreaterThanOrEqualTo(report.MeanRttMs.Value * 0.999, "the worst cannot be under the mean");
    }

    [Fact]
    public async Task A_Dropped_Pong_Counts_As_Lost()
    {
        using AudioLink link = AudioLink.Create(Mode);
        await using var pinger = new Station(Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance);
        await using var responder = new Station(Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);
        pinger.Start();
        responder.Start();

        // A hand-rolled responder that answers every ping except sequence 1, so the run's own
        // loss counting is what is under test - not AudioChannel's dropout mechanics, which
        // AudioLinkTests already covers. Replies are queued and sent from a separate loop
        // rather than straight from this handler, for the same reason PerfRun.
        // RunPongResponderAsync does: the handler runs while the pinger's SendAsync is still on
        // the call stack, and calling back into the responder's own modem from in here
        // (Modulate, reentrant on top of the Process call that is still unwinding) corrupts its
        // state for the next frame - reproducibly cost this test a second, unintended loss
        // before the queue was added.
        const int skipSeq = 1;
        var pending = System.Threading.Channels.Channel.CreateUnbounded<LinkFrame>();
        void OnPing(LinkFrame frame, FrameQuality quality)
        {
            if (frame.Type != LinkFrameType.PerfPing || frame.Payload.Length < 2)
            {
                return;
            }

            ushort seq = (ushort)((frame.Payload.Span[0] << 8) | frame.Payload.Span[1]);
            if (seq == skipSeq)
            {
                return;
            }

            pending.Writer.TryWrite(frame);
        }

        responder.FrameReceived += OnPing;
        using var responderCts = new CancellationTokenSource();
        Task responderLoop = Task.Run(async () =>
        {
            await foreach (LinkFrame ping in pending.Reader.ReadAllAsync(responderCts.Token))
            {
                await responder.SendAsync(responder.Frame(LinkFrameType.PerfPong, ping.Session, ping.Payload.Span));
            }
        });

        var pingerRun = new PerfRun();
        // Generous relative to the RTTs this rig actually produces (comfortably under a
        // millisecond once warm) - the point being tested is loss counting, not tight timing,
        // and a background responder's first reply pays real thread-pool and JIT warm-up cost
        // that a tight timeout would mistake for a second drop.
        var options = new PerfPingOptions { PingCount = 4, PingTimeout = TimeSpan.FromSeconds(3) };
        PerfReport report = await pingerRun.RunPingAsync(
            pinger, options, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        responder.FrameReceived -= OnPing;
        responderCts.Cancel();
        try
        {
            await responderLoop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop was cancelled to stop it.
        }

        report.FramesSent.Should().Be(4);
        report.FramesHeard.Should().Be(3);
        report.FramesDelivered.Should().Be(3);
        report.FramesLost.Should().Be(1);
        report.FrameErrorRate.Should().BeApproximately(0.25, 0.0001);
        report.MeanRttMs.Should().NotBeNull("three of the four still answered");
    }

    /// <summary>
    /// A cancelled <see cref="PerfRun.RunPongResponderAsync"/> completes rather than faulting -
    /// its own catch of <see cref="OperationCanceledException"/> for the requested cancellation.
    /// </summary>
    private static async Task AwaitResponderStop(Task responderTask)
    {
        await responderTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
