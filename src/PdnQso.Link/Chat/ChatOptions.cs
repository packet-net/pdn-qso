namespace PdnQso.Link.Chat;

/// <summary>
/// How patient the chat ARQ is, how hard it tries, and whether it is allowed to move the
/// MS110D waveform underneath itself. Every duration goes through the session's
/// <see cref="TimeProvider"/>, so a test winds a clock rather than sleeping through one.
/// </summary>
/// <remarks>
/// The defaults are the ones a station on HF wants: three seconds of margin on top of however
/// long the burst itself took, four retries, and a half-second backoff slot. A fast packet
/// mode on VHF wants smaller numbers and a settings dialog to put them in.
/// </remarks>
public sealed record ChatOptions
{
    /// <summary>
    /// How long to wait for the acknowledgement, fixed. Null (the default) derives it per
    /// attempt from the mode: <see cref="AckTimeoutBase"/> plus the time the burst that has
    /// just gone out actually took, capped at <see cref="MaxAckTimeout"/>.
    /// </summary>
    /// <remarks>
    /// The derivation is the honest one available at that moment. A station's own burst is
    /// measured, not modelled, and the acknowledgement is a shorter frame in the same mode, so
    /// our own air time is an upper bound on the ack's and covers the far end's turnaround
    /// with <see cref="AckTimeoutBase"/> on top. That is what makes the timeout scale with the
    /// mode without a table of bytes-per-second that would drift the moment a modem changed.
    /// </remarks>
    public TimeSpan? AckTimeout { get; init; }

    /// <summary>
    /// The fixed part of the derived acknowledgement timeout: the far end's decode, its
    /// turnaround and the slack a real path needs. Added to the measured air time.
    /// </summary>
    public TimeSpan AckTimeoutBase { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The ceiling on a derived timeout. Without it, one burst that waited a long time for a
    /// busy channel would set the patience for the whole QSO.
    /// </summary>
    public TimeSpan MaxAckTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many times to send the line again after the first attempt went unacknowledged.
    /// A line therefore gets <c>MaxRetries + 1</c> attempts in all, and that is the number
    /// <see cref="ChatDelivery.Attempts"/> reports.
    /// </summary>
    public int MaxRetries { get; init; } = 4;

    /// <summary>
    /// The backoff slot. A retry waits for the channel to be clear and then for a random
    /// whole number of slots, growing with the attempt number and capped at
    /// <see cref="MaxBackoffSlots"/>, so two stations that collided do not collide again.
    /// </summary>
    public TimeSpan BackoffSlot { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The most slots a backoff will ever draw.</summary>
    public int MaxBackoffSlots { get; init; } = 8;

    /// <summary>How often to look again while a backoff is waiting for the channel to clear.</summary>
    public TimeSpan BusyPollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How long a backoff will wait for a clear channel before sending anyway. The station
    /// underneath has its own, harder, rule (it refuses to transmit at all); this one only
    /// decides when to stop being polite about it.
    /// </summary>
    public TimeSpan BusyWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the ARQ may move the transmit waveform. Only ever acts on an MS110D modem;
    /// see <see cref="WaveformLadder"/>.
    /// </summary>
    public bool StepWaveform { get; init; } = true;

    /// <summary>Consecutive unacknowledged attempts before stepping to a more robust waveform.</summary>
    public int StepDownAfter { get; init; } = 2;

    /// <summary>
    /// Consecutive lines delivered on their first attempt before stepping back up one. Any
    /// retry at all resets the count: the point is to climb only from a link that is not
    /// working for it.
    /// </summary>
    public int StepUpAfter { get; init; } = 3;

    /// <summary>
    /// The waveform ladder, most capable first, each step more robust than the last.
    /// MIL-STD-188-110D Phase A waveform numbers; docs/design.md section 3 fixes the default.
    /// </summary>
    public IReadOnlyList<int> WaveformSteps { get; init; } = WaveformLadder.DefaultSteps;

    /// <summary>
    /// The session id to use, or null (the default) for a random one. A conversation is
    /// identified by it, and an acknowledgement carries the session of the line it answers.
    /// </summary>
    public byte? SessionId { get; init; }

    /// <summary>
    /// The one station this conversation is with, or null (the default) to take chat from
    /// whoever calls. Set it and frames from anybody else are left to Monitor.
    /// </summary>
    public string? Correspondent { get; init; }

    /// <summary>
    /// How many recently seen <c>(source, session, seq)</c> triples to remember, so that a
    /// line whose acknowledgement was lost and which therefore arrives twice is shown once.
    /// </summary>
    public int DuplicateWindow { get; init; } = 32;

    /// <summary>
    /// The longest line that will be sent, in UTF-8 bytes. A chat line is a line; anything
    /// longer is a file and there is a mode for that.
    /// </summary>
    public int MaxTextBytes { get; init; } = 512;

    /// <summary>Throws if any of this is not a workable setting.</summary>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public void Validate()
    {
        Check(MaxRetries >= 0, "MaxRetries cannot be negative");
        Check(AckTimeoutBase > TimeSpan.Zero, "AckTimeoutBase must be positive");
        Check(MaxAckTimeout >= AckTimeoutBase, "MaxAckTimeout cannot be shorter than AckTimeoutBase");
        Check(AckTimeout is null or { Ticks: > 0 }, "AckTimeout must be positive when it is set");
        Check(BackoffSlot >= TimeSpan.Zero, "BackoffSlot cannot be negative");
        Check(MaxBackoffSlots >= 1, "MaxBackoffSlots must be at least 1");
        Check(BusyPollInterval > TimeSpan.Zero, "BusyPollInterval must be positive");
        Check(BusyWaitTimeout > TimeSpan.Zero, "BusyWaitTimeout must be positive");
        Check(StepDownAfter >= 1, "StepDownAfter must be at least 1");
        Check(StepUpAfter >= 1, "StepUpAfter must be at least 1");
        Check(DuplicateWindow >= 1, "DuplicateWindow must be at least 1");
        Check(MaxTextBytes >= 1, "MaxTextBytes must be at least 1");
        Check(WaveformSteps.Count >= 1, "the waveform ladder needs at least one step");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
        {
            throw new ArgumentException(message, nameof(ChatOptions));
        }
    }
}
