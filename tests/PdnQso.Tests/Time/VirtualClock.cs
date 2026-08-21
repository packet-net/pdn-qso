namespace PdnQso.Tests.Time;

/// <summary>
/// A clock the tests own outright: it starts at a fixed instant and moves only when something
/// moves it. Nothing in a test that uses one can be decided by how fast the machine is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not FakeTimeProvider.</b> The hermetic rig is not a single-threaded simulation: a
/// burst crosses the link synchronously on the transmitting thread, and the protocol loops on
/// both sides run as ordinary tasks. A clock that only moves when the test says so deadlocks
/// there, because the test is itself waiting for one of those loops. What is needed is a clock
/// that knows what is scheduled and can be pushed forward to the next thing that is due, which
/// is what <see cref="NextDue"/> and <see cref="TryAdvanceToNextDue"/> are for, and
/// <see cref="VirtualTime.SettleAsync"/> is the loop that drives it.
/// </para>
/// <para>
/// <b>What determinism this does and does not buy.</b> Every duration a test asserts on is
/// virtual, so no verdict can change with machine load: that is the whole point, and it is
/// what a wall-clock timeout cannot promise. The order in which two ready threads run is still
/// the operating system's business, as it is in the real program.
/// </para>
/// </remarks>
public sealed class VirtualClock : TimeProvider
{
    /// <summary>Where every test's clock starts, so a failure message reads the same twice.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly List<VirtualTimer> _timers = [];
    private DateTimeOffset _now = Epoch;
    private long _fired;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>How far this clock has been moved since it started.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _now - Epoch;
            }
        }
    }

    /// <summary>How many timer callbacks have run. A change means work was released.</summary>
    public long Fired
    {
        get
        {
            lock (_gate)
            {
                return _fired;
            }
        }
    }

    /// <summary>When the next scheduled callback is due, or null when nothing is scheduled.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? next = null;
                foreach (VirtualTimer timer in _timers)
                {
                    if (timer.Due is DateTimeOffset due && (next is null || due < next))
                    {
                        next = due;
                    }
                }

                return next;
            }
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new VirtualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock to the next scheduled callback and runs everything that falls due.
    /// </summary>
    /// <returns>How far it moved, or null when nothing was scheduled to move it to.</returns>
    public TimeSpan? TryAdvanceToNextDue()
    {
        if (NextDue is not DateTimeOffset due)
        {
            return null;
        }

        DateTimeOffset was = GetUtcNow();
        TimeSpan step = due > was ? due - was : TimeSpan.Zero;
        Advance(step);
        return step;
    }

    /// <summary>Moves the clock forward, running every callback that falls due on the way.</summary>
    /// <param name="by">How far to move it. Zero still fires anything already due.</param>
    /// <remarks>
    /// The clock stops at each due time on the way rather than jumping to the end and firing
    /// what it passed, so a callback sees the time it was scheduled for and not the time the
    /// advance happened to finish at. That is what a real clock does, and everything that
    /// schedules its next step from "now" depends on it: without it a timer repeating every
    /// second fires once in a five second advance, because its second tick is booked from the
    /// end of the advance rather than from its first.
    /// </remarks>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        // Callbacks run outside the lock, one at a time, in the order they come due, with the
        // clock standing at each one's own due time as it runs.
        while (true)
        {
            VirtualTimer? next = null;
            DateTimeOffset due = default;
            lock (_gate)
            {
                foreach (VirtualTimer timer in _timers)
                {
                    if (timer.Due is DateTimeOffset at && at <= target && (next is null || at < due))
                    {
                        next = timer;
                        due = at;
                    }
                }

                if (next is null)
                {
                    // Nothing else falls due on the way: finish the move and stop.
                    if (_now < target)
                    {
                        _now = target;
                    }

                    return;
                }

                if (_now < due)
                {
                    _now = due;
                }
            }

            next.Fire(due);
            lock (_gate)
            {
                _fired++;
            }
        }
    }

    private void Forget(VirtualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>One scheduled callback on a <see cref="VirtualClock"/>.</summary>
    private sealed class VirtualTimer(VirtualClock clock, TimerCallback callback, object? state)
        : ITimer
    {
        /// <summary>Not scheduled. A sentinel, so the due time is one atomic long.</summary>
        private const long Never = long.MinValue;

        private readonly object _gate = new();
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private long _due = Never;
        private bool _disposed;

        /// <summary>
        /// When this next fires, or null when it is stopped.
        /// </summary>
        /// <remarks>
        /// Held as ticks rather than as a <see cref="DateTimeOffset"/> so that the clock's scan,
        /// which reads it without taking this timer's lock, cannot see half of one write and
        /// half of the next. Sixteen bytes are not written atomically; eight are.
        /// </remarks>
        public DateTimeOffset? Due
        {
            get
            {
                long ticks = Volatile.Read(ref _due);
                return ticks == Never ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                Volatile.Write(
                    ref _due,
                    dueTime == Timeout.InfiniteTimeSpan
                        ? Never
                        : (clock.GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime))
                            .UtcTicks);
                return true;
            }
        }

        /// <summary>Runs the callback and schedules the next repeat, if it repeats.</summary>
        public void Fire(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                Volatile.Write(
                    ref _due,
                    _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan
                        ? (now + _period).UtcTicks
                        : Never);
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                Volatile.Write(ref _due, Never);
            }

            clock.Forget(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
