namespace PdnQso.Link.Perf;

/// <summary>How a <see cref="PerfRun"/> ping-pong measurement is configured, from the pinging end.</summary>
public sealed record PerfPingOptions
{
    /// <summary>How many <see cref="LinkFrameType.PerfPing"/> probes to send.</summary>
    public required int PingCount { get; init; }

    /// <summary>How long to wait for each probe's <see cref="LinkFrameType.PerfPong"/> before
    /// counting it lost and moving on to the next.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra spacing between probes, after each one is answered or has timed out.</summary>
    public TimeSpan Gap { get; init; } = TimeSpan.Zero;

    /// <summary>The session id this run's frames carry; a fresh random one when omitted.</summary>
    public byte? Session { get; init; }

    /// <summary>The audio centre to record on the report - see <see cref="PerfStreamOptions.CentreHz"/>.</summary>
    public double? CentreHz { get; init; }
}
