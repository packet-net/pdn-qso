using System.Globalization;

namespace PdnQso.Link.Devices;

/// <summary>
/// The transmit power of a sound-card station: the playback volume of the card feeding the
/// radio, in per cent of the control's range, with the dB the card reports beside it.
/// </summary>
/// <remarks>
/// <para>
/// Design.md section 4a. On a CM108 widget or any other card there is no RF power to set - the
/// radio's own power knob is doing that - so the lever this tool has is the audio drive, and
/// on a rig past its limiter that is what decides how much of the transmitter is being used.
/// The percentage is of the control's raw range, which is what the operator sees in
/// <c>alsamixer</c>, and the dB beside it is the card's own claim rather than our arithmetic:
/// the two do not track each other linearly on most cards, and the dB is the honest number.
/// </para>
/// <para>
/// A setting above 100 per cent is refused rather than clamped, like every other power control
/// here: an operator who typed 150 has made a mistake worth being told about.
/// </para>
/// </remarks>
public sealed class MixerPowerControl : IPowerControl, IDisposable
{
    private readonly IMixerDevice _mixer;

    /// <summary>Builds a power control over one discovered playback control.</summary>
    /// <param name="mixer">The card's playback control.</param>
    /// <exception cref="ArgumentException">The control has no range at all, so there is
    /// nothing to set.</exception>
    public MixerPowerControl(IMixerDevice mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        if (mixer.Maximum <= mixer.Minimum)
        {
            throw new ArgumentException(
                $"the '{mixer.ElementName}' control on {mixer.Card} reports the range "
                + $"{mixer.Minimum}..{mixer.Maximum}, which is not a range - there is nothing "
                + "to set here",
                nameof(mixer));
        }

        _mixer = mixer;
    }

    /// <summary>The control being driven, for the settings dialog to name.</summary>
    public string ElementName => _mixer.ElementName;

    /// <summary>The card the control is on.</summary>
    public string Card => _mixer.Card;

    /// <inheritdoc />
    public PowerUnit Unit => PowerUnit.Percent;

    /// <inheritdoc />
    public bool CanSet => true;

    /// <inheritdoc />
    public double? Maximum => 100.0;

    /// <inheritdoc />
    public ValueTask<PowerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double percent = ToPercent(_mixer.Volume);
        double? decibels = _mixer.Decibels;
        return ValueTask.FromResult(
            new PowerReading(PowerUnit.Percent, percent, decibels, Describe(percent, decibels)));
    }

    /// <inheritdoc />
    public ValueTask SetAsync(double setting, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (setting is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(setting), setting,
                $"the '{_mixer.ElementName}' control on {_mixer.Card} takes 0 to 100 per cent");
        }

        _mixer.Volume = FromPercent(setting);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _mixer.Dispose();

    /// <summary>The status bar's one-liner, e.g. <c>73 % (-9.5 dB)</c>.</summary>
    /// <param name="percent">The setting.</param>
    /// <param name="decibels">What the card says that is in dB, or null when it does not say.</param>
    public static string Describe(double percent, double? decibels) =>
        decibels is double db
            ? string.Create(CultureInfo.InvariantCulture, $"{percent:0} % ({db:0.0} dB)")
            : string.Create(CultureInfo.InvariantCulture, $"{percent:0} %");

    private double ToPercent(long volume) =>
        (volume - _mixer.Minimum) * 100.0 / (_mixer.Maximum - _mixer.Minimum);

    private long FromPercent(double percent) =>
        _mixer.Minimum + (long)Math.Round(
            percent / 100.0 * (_mixer.Maximum - _mixer.Minimum), MidpointRounding.AwayFromZero);
}
