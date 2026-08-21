using M0LTE.Dsp;
using M0LTE.Radio.Audio;

namespace PdnQso.Link.Audio;

/// <summary>
/// A capture device running faster than the modem, presented at the modem's rate: reads from
/// the inner device and hands back anti-aliased samples decimated by a whole number.
/// </summary>
/// <remarks>
/// <para>
/// The receive-side counterpart of pdn-soundmodem's <c>UpsamplingAudioOutput</c>, and needed
/// for the same reason: a sound card will open at 48 kHz and most of the modems run at 12,
/// and a <see cref="Station"/> refuses a device whose rate is not its modem's. The daemon
/// solves this inside its shared channel, which is not on the package's public surface, so
/// this tool does the decimation in the adapter instead. The filter is M0LTE.Dsp's
/// <see cref="Decimator"/>, which is what the modems themselves decimate with.
/// </para>
/// <para>
/// Only whole-number ratios: 48000 -> 12000 is four, 48000 -> 9600 is five. A fractional
/// resample would need an interpolator whose passband ripple nobody here has measured, and a
/// modem fed by an unmeasured filter is a performance claim nobody can stand behind.
/// </para>
/// </remarks>
public sealed class DecimatingAudioInput : IAudioInput, IDisposable
{
    private readonly IAudioInput _inner;
    private readonly Decimator? _decimator;
    private readonly int _factor;
    private readonly float[] _raw;
    private readonly float[] _decimated;
    private int _ready;
    private int _taken;

    /// <summary>Wraps <paramref name="inner"/> and presents it at <paramref name="outputRate"/>.</summary>
    /// <param name="inner">The faster capture device.</param>
    /// <param name="outputRate">The modem's DSP rate.</param>
    /// <param name="blockSamples">Output samples per inner read; sizes the internal buffers.</param>
    /// <param name="taps">Anti-alias filter length.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/>'s rate is not a whole
    /// multiple of <paramref name="outputRate"/>.</exception>
    public DecimatingAudioInput(IAudioInput inner, int outputRate, int blockSamples = 1024, int taps = 63)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSamples);

        if (inner.SampleRate % outputRate != 0)
        {
            throw new ArgumentException(
                $"a {inner.SampleRate} Hz device cannot be decimated to {outputRate} Hz - the "
                + "ratio has to be a whole number. Ask the card for a rate that is a multiple "
                + "of the mode's, or pick a mode whose rate divides the card's.",
                nameof(outputRate));
        }

        _inner = inner;
        _factor = inner.SampleRate / outputRate;
        SampleRate = outputRate;

        // A factor of one is a card already at the mode's rate, and M0LTE.Dsp's decimator will
        // not take it: there is nothing to filter, so the samples pass through untouched.
        _decimator = _factor == 1 ? null : new Decimator(inner.SampleRate, _factor, taps);
        _raw = new float[blockSamples * _factor];
        _decimated = new float[_decimator?.MaxOutput(_raw.Length) ?? _raw.Length];
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>The whole-number ratio between the device's rate and the modem's.</summary>
    public int Factor => _factor;

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        if (_taken == _ready)
        {
            _taken = 0;
            _ready = 0;

            int wanted = Math.Min(_raw.Length, destination.Length * _factor);
            int got = _inner.Read(_raw.AsSpan(0, wanted));
            if (got <= 0)
            {
                return 0;
            }

            // Whole input frames only: handing the decimator a part-frame tail would put the
            // polyphase commutator out of step with the next block and every sample after it.
            got -= got % _factor;
            if (got == 0)
            {
                return 0;
            }

            if (_decimator is Decimator decimator)
            {
                _ready = decimator.Process(_raw.AsSpan(0, got), _decimated);
            }
            else
            {
                _raw.AsSpan(0, got).CopyTo(_decimated);
                _ready = got;
            }
        }

        int copied = Math.Min(destination.Length, _ready - _taken);
        _decimated.AsSpan(_taken, copied).CopyTo(destination);
        _taken += copied;
        return copied;
    }

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
