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
    /// The gap this covers is a thread that is runnable and has not been given a slot: it holds
    /// no timer and shows no progress, so it is indistinguishable from an idle one, and moving
    /// the clock across it can fire a timeout against work that was about to happen. Sixteen
    /// rounds is a hundred and twenty-eight yields of the machine, which on a box running eight
    /// test classes at once is a real chance rather than a token one, and it costs nothing when
    /// the rig is genuinely idle.
    ///
    /// It is a margin and not a proof. The exact version - a party publishing the clock time it
    /// has caught up with - was tried and is a deadlock: an air-time advance from another
    /// thread can move the clock between a party observing the time and parking on its next
    /// timer, and the party then waits for a clock that is waiting for the party.
    /// </remarks>
    private const int StillRounds = 16;


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
    /// Waits for something a real-time device is producing, and fails the test once the
    /// device has pumped <paramref name="airBudgetSamples"/> of audio without it happening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="WaitForAsync"/> for the one kind of test that runs on
    /// the wall clock: a device paced like a sound card, where the audio takes as long as it
    /// takes. The no-deadline rule is wrong there. On a paced device a frame can genuinely be
    /// lost - CPU starvation holds the writer off long enough that the reader pads the middle
    /// of the burst with silence, and no amount of further waiting brings the frame back - so
    /// a wait with no bound turns a lost frame into a suite that never finishes. Issue #23
    /// measured that at about one run in twenty under six-fold oversubscription.
    /// </para>
    /// <para>
    /// The bound is still not a deadline. It is counted in the medium's own samples, off the
    /// receiving device's own pump, so "the frame did not arrive within this much air" is a
    /// statement about the link that is true or false however busy the machine was: a starved
    /// box pumps its air late, and the budget stretches with it by exactly as much. Note that
    /// the budget has to be counted in pumped air - real samples and filled-in silence
    /// together - and not in the pipe's own count of real audio: real audio stops accruing
    /// the moment the sender falls silent, so a lost frame would freeze that counter below
    /// any budget and the wait would hang exactly as before. The one thing that still hangs
    /// here is a medium whose pump has stopped entirely, and that is right: with the medium
    /// stopped there is no air to count a verdict in, and the runner's own timeout reporting
    /// "this never happened" is the honest finding.
    /// </para>
    /// </remarks>
    /// <param name="receiver">The device whose pump counts the air off.</param>
    /// <param name="fact">What is being waited for.</param>
    /// <param name="airBudgetSamples">How much pumped air to allow from this call before the
    /// fact is declared to have not happened, in samples at the device's own rate. Make it a
    /// generous multiple of the audio the awaited thing occupies.</param>
    /// <param name="what">What to say it was waiting for, if it never happened.</param>
    /// <param name="postMortem">Evidence to append to the failure, read only once it has
    /// failed: where the audio got to, so a miss on a loaded runner says whether the burst
    /// never arrived or arrived unreadable.</param>
    public static async Task WaitForWithinAirAsync(
        PumpedAudioDevice receiver,
        Func<bool> fact,
        long airBudgetSamples,
        string what,
        Func<string>? postMortem = null)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(airBudgetSamples);

        long from = receiver.SamplesCaptured;
        int spins = 0;
        while (!fact())
        {
            long pumped = receiver.SamplesCaptured - from;
            if (pumped > airBudgetSamples)
            {
                // The assertion re-reads the fact, so something that came true between the
                // pump crossing the budget and this loop noticing still passes; the budget is
                // generous enough that nothing is ever failed while its audio is still in the
                // demodulator.
                string evidence = postMortem is null ? string.Empty : $"; {postMortem()}";
                fact().Should().BeTrue(
                    $"{what} within {airBudgetSamples / (double)receiver.SampleRate:0.#} s of "
                    + $"pumped air, and the device has pumped "
                    + $"{pumped / (double)receiver.SampleRate:0.#} s{evidence}");
                return;
            }

            // The same ladder as WaitForAsync: yield while the fact is merely queued, then
            // give the core back. The delay is how this loop shares the machine, not how it
            // decides anything - the verdict above is counted in air alone.
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

        // Measured from the clock itself, not from the steps this loop takes: a rig that
        // charges for air time moves the clock without this loop's help, and a budget that
        // counted only its own steps would let a transfer run for ten minutes of the protocol's
        // time while believing it had spent one.
        TimeSpan started = clock.Elapsed;
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

            // A step is refused before it is taken, not regretted after: a budget that let one
            // enormous jump through and then noticed would report the thing it was waiting for
            // as having happened inside a budget that never covered it.
            if (clock.NextDue is DateTimeOffset next
                && (clock.Elapsed - started) + (next - clock.GetUtcNow()) > allowed)
            {
                return done();
            }

            if (clock.TryAdvanceToNextDue() is null)
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
            if (clock.Elapsed - started > allowed)
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
