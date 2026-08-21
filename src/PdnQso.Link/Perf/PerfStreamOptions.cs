namespace PdnQso.Link.Perf;

/// <summary>How a <see cref="PerfRun"/> stream measurement is configured, from the sending end.</summary>
public sealed record PerfStreamOptions
{
    /// <summary>How many <see cref="LinkFrameType.PerfStream"/> frames to send.</summary>
    public required int FrameCount { get; init; }

    /// <summary>
    /// The link-frame payload size of every stream frame, in bytes - what design.md's goodput
    /// figure is bytes of. Must be at least 8: the sequence, the total count and the send
    /// timestamp that ride in every frame regardless of the size asked for.
    /// </summary>
    public required int PayloadSize { get; init; }

    /// <summary>
    /// Extra spacing between frames, on top of the modem's own air time (the station's transmit
    /// queue already will not key up again before the previous burst has gone out). Zero sends
    /// back to back.
    /// </summary>
    public TimeSpan Gap { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// TXDELAY for every frame this run sends, milliseconds. Must match the sending station's
    /// own <see cref="StationOptions.TxDelayMilliseconds"/> - <see cref="PerfRun"/> measures air
    /// time by modulating a probe frame with this value, and a mismatch would measure a
    /// different burst to the one actually transmitted.
    /// </summary>
    public int TxDelayMilliseconds { get; init; } = 300;

    /// <summary>The session id this run's frames carry; a fresh random one when omitted.</summary>
    public byte? Session { get; init; }

    /// <summary>
    /// How long to wait, after asking the receiver to wrap up, for its summary before asking
    /// again.
    /// </summary>
    public TimeSpan SummaryTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many times to ask for the summary before giving up and throwing.</summary>
    public int SummaryRetries { get; init; } = 3;

    /// <summary>
    /// The audio centre to record on the report - design.md's "centre (from StationOptions if
    /// exposed, else passed in)": <see cref="IStation"/> does not expose it, so it is passed in
    /// here. Null when not worth recording (an FM/baseband mode, say).
    /// </summary>
    public double? CentreHz { get; init; }
}
