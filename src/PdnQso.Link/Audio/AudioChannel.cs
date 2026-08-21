namespace PdnQso.Link.Audio;

/// <summary>A half-open range of samples, counted from the start of a burst.</summary>
/// <param name="Start">Index of the first sample in the range.</param>
/// <param name="Length">How many samples the range covers.</param>
public readonly record struct SampleRange(int Start, int Length)
{
    /// <summary>One past the last sample in the range.</summary>
    public int End => Start + Length;
}

/// <summary>
/// What happens to a burst between one station's transmitter and another's receiver: additive
/// white Gaussian noise at a stated signal-to-noise ratio, a fixed delay, and windows where
/// the signal simply is not there.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately simpler than pdn-soundmodem's Watterson rig, which models a real ionospheric
/// path and belongs to the modem's own performance masks. This one exists to settle protocol
/// questions - does the ARQ retry, does the fountain transfer finish, does a station give up
/// honestly - and for those, a knob marked "make some frames fail" is the whole requirement.
/// Nothing measured through this channel is a statement about a modem.
/// </para>
/// <para>
/// <b>SNR convention.</b> <see cref="SnrDb"/> is referenced to a 3 kHz noise bandwidth, the
/// same convention pdn-soundmodem's ladder and Watterson masks use (MIL-STD-188-110 D.6.2), so
/// a number here means what a number there means. Signal power is the mean square of the clean
/// burst; noise variance is then
/// <c>signalPower / snr * (sampleRate / 2) / noiseBandwidth</c>, because white noise generated
/// per sample spreads over the whole DC-to-Nyquist band and only the stated slice of it counts.
/// It is NOT the band-tracker convention <c>FrameQuality.SnrDb</c> reports.
/// </para>
/// <para>
/// Noise covers the lead-in and the tail as well as the burst, so the receiver's acquisition
/// sees a realistic floor rather than digital silence, but signal power is measured over the
/// burst alone and padding therefore never dilutes the stated SNR.
/// </para>
/// </remarks>
public sealed record AudioChannel
{
    /// <summary>A perfect wire: no noise, no delay, no dropouts.</summary>
    public static AudioChannel Clean { get; } = new();

    /// <summary>
    /// Signal-to-noise ratio in <see cref="NoiseBandwidthHz"/>;
    /// <see cref="double.PositiveInfinity"/> (the default) for a noiseless run.
    /// </summary>
    public double SnrDb { get; init; } = double.PositiveInfinity;

    /// <summary>The reference noise bandwidth <see cref="SnrDb"/> is stated in.</summary>
    public double NoiseBandwidthHz { get; init; } = 3000;

    /// <summary>
    /// Samples of noise-only lead-in before the burst: propagation delay, and the receiver's
    /// chance to settle on a floor before anything arrives.
    /// </summary>
    public int DelaySamples { get; init; }

    /// <summary>
    /// Samples of noise-only tail after the burst, so a demodulator that needs to run past the
    /// last symbol to flush its filters and emit the frame gets to.
    /// </summary>
    public int TailSamples { get; init; } = 4800;

    /// <summary>
    /// Windows of the <em>output</em> - so counted from the start of the lead-in - that are
    /// zeroed after everything else: a hole where signal and noise alike are gone. That is a
    /// blunt instrument on purpose; it is a lost burst, not a fade.
    /// </summary>
    public IReadOnlyList<SampleRange> Dropouts { get; init; } = [];

    /// <summary>
    /// Seeds the noise when <see cref="Apply(ReadOnlySpan{float}, int)"/> makes its own
    /// generator. A run through one <see cref="AudioLink"/> draws from a single generator
    /// seeded once, so the whole run is reproducible while successive bursts still differ.
    /// </summary>
    public int Seed { get; init; } = 20260821;

    /// <summary>Puts a burst through the channel with a fresh generator from <see cref="Seed"/>.</summary>
    public float[] Apply(ReadOnlySpan<float> burst, int sampleRate) =>
        Apply(burst, sampleRate, new Random(Seed));

    /// <summary>
    /// Puts a burst through the channel, drawing noise from <paramref name="random"/>.
    /// </summary>
    /// <param name="burst">The transmitter's audio.</param>
    /// <param name="sampleRate">The sample rate the burst is at.</param>
    /// <param name="random">The noise source; pass one generator for a whole run so the run
    /// is reproducible.</param>
    /// <returns>A new array of
    /// <c>DelaySamples + burst.Length + TailSamples</c> samples.</returns>
    public float[] Apply(ReadOnlySpan<float> burst, int sampleRate, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegative(DelaySamples);
        ArgumentOutOfRangeException.ThrowIfNegative(TailSamples);

        var output = new float[DelaySamples + burst.Length + TailSamples];
        burst.CopyTo(output.AsSpan(DelaySamples, burst.Length));

        double sigma = NoiseSigma(burst, sampleRate);
        if (sigma > 0)
        {
            for (int i = 0; i < output.Length; i++)
            {
                output[i] += (float)(sigma * Gaussian(random));
            }
        }

        foreach (SampleRange dropout in Dropouts)
        {
            int start = Math.Clamp(dropout.Start, 0, output.Length);
            int end = Math.Clamp(dropout.End, start, output.Length);
            output.AsSpan(start, end - start).Clear();
        }

        return output;
    }

    /// <summary>
    /// The noise standard deviation this channel adds to a burst, or 0 for a noiseless run.
    /// Exposed so a test can state a level rather than infer one.
    /// </summary>
    public double NoiseSigma(ReadOnlySpan<float> burst, int sampleRate)
    {
        if (double.IsPositiveInfinity(SnrDb) || burst.Length == 0)
        {
            return 0;
        }

        double signalPower = 0;
        foreach (float s in burst)
        {
            signalPower += (double)s * s;
        }

        signalPower /= burst.Length;
        if (signalPower <= 0)
        {
            return 0;
        }

        double snr = Math.Pow(10, SnrDb / 10.0);
        return Math.Sqrt(signalPower / snr * (sampleRate / 2.0) / NoiseBandwidthHz);
    }

    /// <summary>Box-Muller, one draw per call; the cost is irrelevant off the hot path.</summary>
    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
