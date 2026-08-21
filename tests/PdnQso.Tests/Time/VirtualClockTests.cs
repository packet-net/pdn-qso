using PdnQso.Tests.Time;

namespace PdnQso.Tests;

/// <summary>
/// The clock every other test now stands on, checked on its own: that it does not move by
/// itself, that a timer fires when it is asked to and not before, and that a repeating one goes
/// on repeating.
/// </summary>
public class VirtualClockTests
{
    [Fact]
    public void A_New_Clock_Is_At_The_Epoch_And_Stays_There()
    {
        var clock = new VirtualClock();

        clock.GetUtcNow().Should().Be(VirtualClock.Epoch);
        clock.Elapsed.Should().Be(TimeSpan.Zero);
        clock.NextDue.Should().BeNull("nothing is scheduled");
        clock.TryAdvanceToNextDue().Should().BeNull("there is nothing to advance to");
        clock.GetUtcNow().Should().Be(VirtualClock.Epoch, "and it has not moved on its own");
    }

    [Fact]
    public void A_Delay_Comes_Due_When_The_Clock_Reaches_It_And_Not_Before()
    {
        var clock = new VirtualClock();
        Task waiting = Task.Delay(TimeSpan.FromSeconds(30), clock);

        clock.NextDue.Should().Be(VirtualClock.Epoch.AddSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(29));
        waiting.IsCompleted.Should().BeFalse("one second short");

        clock.Advance(TimeSpan.FromSeconds(1));
        waiting.IsCompleted.Should().BeTrue();
        clock.Fired.Should().Be(1);
    }

    [Fact]
    public void Advancing_To_The_Next_Due_Time_Lands_Exactly_On_It()
    {
        var clock = new VirtualClock();
        Task first = Task.Delay(TimeSpan.FromSeconds(5), clock);
        Task second = Task.Delay(TimeSpan.FromSeconds(11), clock);

        clock.TryAdvanceToNextDue().Should().Be(TimeSpan.FromSeconds(5));
        first.IsCompleted.Should().BeTrue();
        second.IsCompleted.Should().BeFalse();
        clock.Elapsed.Should().Be(TimeSpan.FromSeconds(5));

        clock.TryAdvanceToNextDue().Should().Be(TimeSpan.FromSeconds(6), "the rest of the way to the second");
        second.IsCompleted.Should().BeTrue();
        clock.Elapsed.Should().Be(TimeSpan.FromSeconds(11));
        clock.NextDue.Should().BeNull("both have gone off");
    }

    [Fact]
    public void Callbacks_Run_In_The_Order_They_Come_Due_However_They_Were_Scheduled()
    {
        var clock = new VirtualClock();
        var order = new List<string>();

        using ITimer late = clock.CreateTimer(
            _ => order.Add("late"), null, TimeSpan.FromSeconds(9), Timeout.InfiniteTimeSpan);
        using ITimer early = clock.CreateTimer(
            _ => order.Add("early"), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

        // One jump past both: the order is the clock's, not the order they were made in.
        clock.Advance(TimeSpan.FromSeconds(30));

        order.Should().Equal("early", "late");
    }

    [Fact]
    public void A_Callback_That_Schedules_Another_Is_Answered_In_The_Same_Advance()
    {
        var clock = new VirtualClock();
        var order = new List<string>();
        ITimer? second = null;

        using ITimer first = clock.CreateTimer(
            _ =>
            {
                order.Add("first");
                second = clock.CreateTimer(
                    _ => order.Add("second"), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            },
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);

        // Ten seconds covers the first at one second and the one it makes at two, exactly as
        // the real clock would have done had it been left running.
        clock.Advance(TimeSpan.FromSeconds(10));

        order.Should().Equal("first", "second");
        second?.Dispose();
    }

    [Fact]
    public void A_Repeating_Timer_Goes_On_Repeating()
    {
        var clock = new VirtualClock();
        int ticks = 0;

        using ITimer timer = clock.CreateTimer(
            _ => ticks++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(5));

        ticks.Should().Be(5);
        clock.NextDue.Should().Be(VirtualClock.Epoch.AddSeconds(6), "and it is armed for the next");
    }

    [Fact]
    public void A_Disposed_Timer_Is_Forgotten_Rather_Than_Fired()
    {
        var clock = new VirtualClock();
        int ticks = 0;

        ITimer timer = clock.CreateTimer(
            _ => ticks++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        timer.Dispose();

        clock.NextDue.Should().BeNull();
        clock.Advance(TimeSpan.FromMinutes(10));

        ticks.Should().Be(0);
    }

    [Fact]
    public void A_Timestamp_Moves_With_The_Clock_And_In_Its_Own_Units()
    {
        var clock = new VirtualClock();
        long before = clock.GetTimestamp();

        clock.Advance(TimeSpan.FromSeconds(3));

        TimeSpan measured = clock.GetElapsedTime(before, clock.GetTimestamp());
        measured.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Settling_Runs_The_Clock_Until_The_Thing_Waited_For_Happens()
    {
        var clock = new VirtualClock();
        Task waiting = Task.Delay(TimeSpan.FromMinutes(2), clock);

        bool settled = await VirtualTime.SettleAsync(clock, () => waiting.IsCompleted);

        settled.Should().BeTrue();
        clock.Elapsed.Should().Be(TimeSpan.FromMinutes(2), "it moved exactly as far as it had to");
    }

    [Fact]
    public async Task Settling_Stops_At_Its_Budget_Rather_Than_Running_On_For_Ever()
    {
        var clock = new VirtualClock();
        Task waiting = Task.Delay(TimeSpan.FromHours(1), clock);

        bool settled = await VirtualTime.SettleAsync(
            clock, () => waiting.IsCompleted, budget: TimeSpan.FromMinutes(1));

        settled.Should().BeFalse("an hour does not fit in a minute of the clock's time");
        waiting.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Time_Does_Not_Move_While_Something_Says_It_Is_Busy()
    {
        var clock = new VirtualClock();
        Task waiting = Task.Delay(TimeSpan.FromSeconds(10), clock);
        bool busy = true;

        // Let go of the brake from somewhere else, which is what a station finishing a burst
        // does. Until then the clock must not have moved a tick.
        Task release = Task.Run(async () =>
        {
            await Task.Delay(50);
            clock.Elapsed.Should().Be(TimeSpan.Zero, "nothing may move while the rig is busy");
            busy = false;
        });

        await VirtualTime.SettleAsync(clock, () => waiting.IsCompleted, busy: () => busy);
        await release;

        waiting.IsCompleted.Should().BeTrue();
        clock.Elapsed.Should().Be(TimeSpan.FromSeconds(10));
    }
}
