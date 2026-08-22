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
    private readonly float[] _carry;
    private int _carried;
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

        // Room for the part-frame tail of one read, to go in front of the next one.
        _carry = new float[Math.Max(1, _factor - 1)];
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>The whole-number ratio between the device's rate and the modem's.</summary>
    public int Factor => _factor;

    /// <summary>
    /// The faster device under this one, for a caller after that device's own counters - a
    /// test reading how many real samples the pipe behind a decimated station delivered.
    /// </summary>
    public IAudioInput Inner => _inner;

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

            // Last read's part-frame tail goes in front of this read, so the stream the
            // decimator sees is the stream the device produced, unbroken.
            _carry.AsSpan(0, _carried).CopyTo(_raw);

            // At least one whole frame before returning, or a device that owes a sample or two
            // would produce no output at all and a caller reading 0 as "nothing there" would
            // stall on a live input. The carry makes this terminate: every read moves the count
            // forward and the shortfall is never more than the factor.
            int target = Math.Min(
                _raw.Length, Math.Max(_factor, destination.Length * _factor));
            int got = _carried;
            while (got < _factor)
            {
                int read = _inner.Read(_raw.AsSpan(got, target - got));
                if (read <= 0)
                {
                    break;
                }

                got += read;
            }

            // Whole input frames only: handing the decimator a part-frame tail would put the
            // polyphase commutator out of step with the next block and every sample after it.
            // The tail is kept rather than dropped: a device paced to wall clock returns
            // whatever it owes, which is a multiple of the factor only by accident, and
            // dropping one to three samples per read is a stream that slips continuously.
            // Nothing decodes through that.
            _carried = got % _factor;
            got -= _carried;
            _raw.AsSpan(got, _carried).CopyTo(_carry);
            if (got == 0)
            {
                // The inner device has nothing at all. What little it did give is in the carry
                // and goes in front of the next read.
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
