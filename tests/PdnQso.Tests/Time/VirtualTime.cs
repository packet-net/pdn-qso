using PdnQso.Link.Audio;

namespace PdnQso.Tests.Time;

/// <summary>
/// Drives a <see cref="VirtualClock"/> forward until something a test is waiting for has
/// happened. This is the replacement for every "sleep and hope" in the suite.
/// </summary>
/// <remarks>
/// <para>
/// The loop is a small discrete-event scheduler over work that is not in a scheduler. It lets
/// everything runnable run, checks that the rig is not mid-burst, and only then moves the clock
/// to the next thing that is due. Moving it any other way, or moving it while a transmission is
/// in the air, would let a timeout fire before the answer it is waiting for could exist, which
/// is the very fault this exists to remove.
/// </para>
/// <para>
/// <b>Budgets are in the clock's own time, not the machine's.</b> "The station did not answer
/// within thirty seconds" now means thirty seconds of the protocol's time, however long the
/// machine took to work them out, so a loaded CI runner cannot turn a passing claim into a
/// failing one. A test that hangs is caught by the runner's own timeout, which is the right
/// place for that: it is an accident, not a measurement.
/// </para>
/// </remarks>
public static class VirtualTime
{
    /// <summary>How long a settle runs, in the clock's time, before it gives up.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times to hand the machine back to whatever is runnable before looking again.
    /// </summary>
    private const int YieldsPerRound = 8;

    /// <summary>
    /// Rounds with nothing having happened before the clock is allowed to move.
    /// </summary>
    /// <remarks>
    /// One round of nothing at all is the cheap insurance against firing a timeout in the gap
    /// between an answer arriving and the end that was waiting for it noticing.
    /// </remarks>
    private const int StillRounds = 2;

    /// <summary>
    /// Waits for something to become true, for as long as it takes.
    /// </summary>
    /// <remarks>
    /// <b>There is deliberately no deadline.</b> A deadline here would be a wall-clock
    /// measurement, and a wall-clock measurement is what makes a test's verdict depend on how
    /// busy the machine was. A fact that never becomes true hangs until the test runner's own
    /// timeout stops it, which says "this never happened" rather than "this did not happen
    /// quickly enough on this box", and those are different findings.
    /// </remarks>
    /// <param name="fact">What is being waited for.</param>
    public static async Task WaitForAsync(Func<bool> fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        int spins = 0;
        while (!fact())
        {
            // Yield for the first while, which covers everything that is merely queued; then
            // give the core back, because a fact that takes real work to become true should not
            // be raced for by a spinning loop. Neither is a deadline: this waits for ever.
            if (++spins < 1000)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Hands the machine back to whatever else is runnable, once. For a loop waiting on
    /// something a real device is doing on its own thread.
    /// </summary>
    public static async Task YieldAsync() => await Task.Yield();

    /// <summary>
    /// Runs until <paramref name="done"/> is true, moving the clock on only while nothing at
    /// all is happening.
    /// </summary>
    /// <param name="clock">The clock to drive.</param>
    /// <param name="done">The thing being waited for.</param>
    /// <param name="busy">
    /// True while any party the clock must not be run past has work in hand.
    /// <b>It has to become true synchronously</b> when that party is given the work: a flag that
    /// is only set once the party's own loop wakes up leaves a gap in which this will happily
    /// fire the other end's timeout. For a two-station rig that means
    /// <see cref="AudioLink.Carrying"/> for the burst itself, and, for any responder the test
    /// runs, a flag set inside the frame handler and cleared when its reply has gone.
    /// </param>
    /// <param name="budget">How much of the clock's time to allow.</param>
    /// <param name="progress">A number that changes whenever the rig does something;
    /// <see cref="AudioLink.Crossings"/> for a two-station rig.</param>
    /// <returns>Whether it finished, rather than running out of budget.</returns>
    public static async Task<bool> SettleAsync(
        VirtualClock clock,
        Func<bool> done,
        Func<bool>? busy = null,
        TimeSpan? budget = null,
        Func<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(done);

        TimeSpan allowed = budget ?? DefaultBudget;
        TimeSpan spent = TimeSpan.Zero;
        int still = 0;
        int spins = 0;
        long moved = Moved(clock, progress);

        while (true)
        {
            if (done())
            {
                return true;
            }

            for (int i = 0; i < YieldsPerRound; i++)
            {
                await Task.Yield();
            }

            if (done())
            {
                return true;
            }

            if (busy?.Invoke() == true)
            {
                still = 0;
                spins = 0;
                continue;
            }

            long moving = Moved(clock, progress);
            if (moving != moved)
            {
                moved = moving;
                still = 0;
                spins = 0;
                continue;
            }

            if (++still < StillRounds)
            {
                continue;
            }

            still = 0;
            if (clock.TryAdvanceToNextDue() is not TimeSpan step)
            {
                // Nothing scheduled, nothing busy, nothing moving. That is not the same as
                // over: a loop between an answer and its next timeout holds no timer either,
                // and on a machine under load it can sit there a while before it is given a
                // slot. So this waits rather than deciding, on a count of its own turns, that
                // nothing more will ever happen. A test that really is stuck hangs and the
                // runner's timeout says so honestly; guessing here would be the wall clock
                // coming back in by the side door.
                if (++spins > 1000)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }

                continue;
            }

            spins = 0;
            spent += step;
            if (spent > allowed)
            {
                return done();
            }
        }
    }

    /// <summary>One number that changes whenever anything at all has happened.</summary>
    private static long Moved(VirtualClock clock, Func<long>? progress) =>
        clock.Fired + (progress?.Invoke() ?? 0);

    /// <summary>Runs until <paramref name="done"/> is true, and fails the test if it is not.</summary>
    /// <param name="clock">The clock to drive.</param>
    /// <param name="done">The thing being waited for.</param>
    /// <param name="what">What to say it was waiting for, if it never happened.</param>
    /// <param name="busy">True while the rig must not be run past.</param>
    /// <param name="budget">How much of the clock's time to allow.</param>
    public static async Task UntilAsync(
        VirtualClock clock,
        Func<bool> done,
        string what,
        Func<bool>? busy = null,
        TimeSpan? budget = null,
        Func<long>? progress = null)
    {
        bool settled = await SettleAsync(clock, done, busy, budget, progress).ConfigureAwait(false);
        settled.Should().BeTrue(
            $"{what} (waited {clock.Elapsed.TotalSeconds:0.#} s of the clock's time)");
    }

    /// <summary>
    /// Runs the clock until <paramref name="work"/> finishes, then returns what it produced.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="clock">The clock to drive.</param>
    /// <param name="work">The protocol operation under test.</param>
    /// <param name="busy">True while the rig must not be run past.</param>
    /// <param name="budget">How much of the clock's time to allow.</param>
    /// <exception cref="TimeoutException">The work did not finish inside the budget. That is a
    /// statement about the protocol's own time, not about the machine.</exception>
    public static async Task<T> RunAsync<T>(
        VirtualClock clock,
        Task<T> work,
        Func<bool>? busy = null,
        TimeSpan? budget = null,
        Func<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!await SettleAsync(clock, () => work.IsCompleted, busy, budget, progress).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"the work did not finish inside {(budget ?? DefaultBudget).TotalSeconds:0} s "
                + $"of the clock's time (it reached {clock.Elapsed.TotalSeconds:0.#} s)");
        }

        return await work.ConfigureAwait(false);
    }

    /// <summary>Runs the clock until <paramref name="work"/> finishes.</summary>
    /// <param name="clock">The clock to drive.</param>
    /// <param name="work">The protocol operation under test.</param>
    /// <param name="busy">True while the rig must not be run past.</param>
    /// <param name="budget">How much of the clock's time to allow.</param>
    /// <exception cref="TimeoutException">The work did not finish inside the budget.</exception>
    public static async Task RunAsync(
        VirtualClock clock,
        Task work,
        Func<bool>? busy = null,
        TimeSpan? budget = null,
        Func<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!await SettleAsync(clock, () => work.IsCompleted, busy, budget, progress).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"the work did not finish inside {(budget ?? DefaultBudget).TotalSeconds:0} s "
                + $"of the clock's time (it reached {clock.Elapsed.TotalSeconds:0.#} s)");
        }

        await work.ConfigureAwait(false);
    }
}
