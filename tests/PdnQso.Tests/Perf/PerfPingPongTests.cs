using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Perf;
using PdnQso.Tests.Time;

namespace PdnQso.Tests.Perf;

/// <summary>
/// The ping-pong procedure of docs/design.md section 3: a probe answered straight back, timed
/// per round trip.
/// </summary>
/// <remarks>
/// Every duration here is on a <see cref="VirtualClock"/>. The timeouts are the protocol's own
/// and are measured in its time, so what this asserts cannot change with the load on the
/// machine: the run that put this comment here failed on a busy CI runner, at a three second
/// ping timeout, because a thread-pool hop took longer than that.
/// </remarks>
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
        var clock = new VirtualClock();
        using AudioLink link = AudioLink.Create(Mode);
        await using var pinger = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: clock);
        await using var responder = new Station(
            Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: clock);
        pinger.Start();
        responder.Start();

        var responderRun = new PerfRun(clock);
        using var responderCts = new CancellationTokenSource();
        Task responderTask = responderRun.RunPongResponderAsync(responder, responderCts.Token);
        await VirtualTime.WaitForAsync(() => responderRun.Listening);

        var pingerRun = new PerfRun(clock);
        var options = new PerfPingOptions { PingCount = 5, PingTimeout = TimeSpan.FromSeconds(5) };

        // Nothing here needs the clock to move: every ping is answered, so the run finishes on
        // facts alone. The clock is still the test's own, so the timeout cannot fire behind its
        // back however slow the machine is.
        Task<PerfReport> run = pingerRun.RunPingAsync(pinger, options);
        await VirtualTime.WaitForAsync(() => run.IsCompleted);
        PerfReport report = await run;
        clock.Elapsed.Should().Be(TimeSpan.Zero, "a clean ping-pong run never has to wait");

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
        var clock = new VirtualClock();
        using AudioLink link = AudioLink.Create(Mode);
        await using var pinger = new Station(
            Options("M0LTE"), link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: clock);
        await using var responder = new Station(
            Options("G0OLD"), link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: clock);
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

        // What the responder owes, counted where the ping is taken rather than where the reply
        // is made. The clock must not move while a reply is owed, and "owed" has to start the
        // instant the ping is accepted: a flag raised only once the loop below wakes up leaves
        // a gap, and the pinger's timeout would be fired inside it. This is the whole reason
        // the far end's answer cannot be lost to a slow machine any more.
        int owed = 0;

        void OnPing(LinkFrame frame, FrameQuality quality)
        {
            if (frame.Type != LinkFrameType.PerfPing || frame.Payload.Length < 2)
            {
                return;
            }

            ushort seq = (ushort)((frame.Payload.Span[0] << 8) | frame.Payload.Span[1]);
            if (seq == skipSeq)
            {
                // Owed nothing: this is the ping that is meant to go unanswered.
                return;
            }

            Interlocked.Increment(ref owed);
            pending.Writer.TryWrite(frame);
        }

        responder.FrameReceived += OnPing;
        using var responderCts = new CancellationTokenSource();
        Task responderLoop = Task.Run(async () =>
        {
            await foreach (LinkFrame ping in pending.Reader.ReadAllAsync(responderCts.Token))
            {
                try
                {
                    await responder.SendAsync(
                        responder.Frame(LinkFrameType.PerfPong, ping.Session, ping.Payload.Span));
                }
                finally
                {
                    Interlocked.Decrement(ref owed);
                }
            }
        });

        bool Busy() => link.Carrying || Volatile.Read(ref owed) > 0;

        var pingerRun = new PerfRun(clock);
        // The one unanswered ping is the only thing that may time out. On the wall clock this
        // was a three second timeout that a busy CI runner beat, and the run then counted two
        // losses instead of one; on this clock the timeout is three seconds of the protocol's
        // time and the machine cannot get in the way of it.
        var options = new PerfPingOptions { PingCount = 4, PingTimeout = TimeSpan.FromSeconds(3) };
        PerfReport report = await VirtualTime.RunAsync(
            clock, pingerRun.RunPingAsync(pinger, options), Busy, progress: () => link.Crossings);

        responder.FrameReceived -= OnPing;
        responderCts.Cancel();
        await VirtualTime.WaitForAsync(() => responderLoop.IsCompleted);

        clock.Elapsed.Should().Be(
            options.PingTimeout,
            "exactly one ping went unanswered, so exactly one timeout was waited out");

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
        await VirtualTime.WaitForAsync(() => responderTask.IsCompleted);
        await responderTask;
    }
}
