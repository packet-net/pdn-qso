namespace PdnQso.Link.Devices;

/// <summary>What a device's power setting is expressed in.</summary>
public enum PowerUnit
{
    /// <summary>There is no power control on this device.</summary>
    None,

    /// <summary>Watts of RF, as a Flex takes and reports them.</summary>
    Watts,

    /// <summary>Per cent of a playback mixer's range, as a sound card takes them.</summary>
    Percent,
}

/// <summary>
/// What the power control is set to and, where the device can tell us, what is actually
/// coming out.
/// </summary>
/// <param name="Unit">What <paramref name="Setting"/> and <paramref name="Measured"/> are in.</param>
/// <param name="Setting">What the control is set to.</param>
/// <param name="Measured">What the device reports it is really doing - forward power off a
/// Flex's meter, the dB a mixer says it is at - or <see langword="null"/> where the device
/// only knows what it was told. The UI shows both, never only the setting: on a shared rig
/// the difference between "set 10 W" and "reading 9.6 W" is the whole point of asking.</param>
/// <param name="Display">A ready-made one-liner for the status bar, e.g.
/// <c>"set 10 W, reading 9.6 W"</c>. ASCII, like everything else that reaches a terminal.</param>
public readonly record struct PowerReading(
    PowerUnit Unit,
    double Setting,
    double? Measured,
    string Display);

/// <summary>
/// The transmit power of one device, as design.md section 4a defines it: watts through
/// <c>rfpower</c> on a Flex with the meter read back, the playback mixer's volume on a sound
/// card, and nothing at all on a receive-only device.
/// </summary>
/// <remarks>
/// A station ceiling is honoured by refusing a setting above what the device reports as its
/// maximum, never by clamping silently - an operator who asked for 50 W on a 15 W station has
/// made a mistake worth being told about, and a tool that quietly transmits 15 W instead has
/// hidden it. The ALSA-mixer and Flex implementations arrive in phase A2; phase A ships the
/// interface and <see cref="NoPowerControl"/>.
/// </remarks>
public interface IPowerControl
{
    /// <summary>What this control's numbers mean.</summary>
    PowerUnit Unit { get; }

    /// <summary>False when the device can only be read, or has no power control at all.</summary>
    bool CanSet { get; }

    /// <summary>
    /// The most this device will accept, in <see cref="Unit"/>, as the device itself reports
    /// it; <see langword="null"/> when it does not say.
    /// </summary>
    double? Maximum { get; }

    /// <summary>Reads the current setting and, where available, the measured output.</summary>
    ValueTask<PowerReading> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the power.</summary>
    /// <param name="setting">The new value, in <see cref="Unit"/>.</param>
    /// <param name="cancellationToken">Cancels the exchange with the device.</param>
    /// <exception cref="NotSupportedException"><see cref="CanSet"/> is false.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="setting"/> is negative or
    /// above <see cref="Maximum"/>. Refused, not clamped.</exception>
    ValueTask SetAsync(double setting, CancellationToken cancellationToken = default);
}

/// <summary>
/// The power control of a device that has none: an UberSDR receiver, a sound card whose mixer
/// has no playback volume, or a rig whose power is set on its front panel.
/// </summary>
public sealed class NoPowerControl : IPowerControl
{
    /// <summary>The one instance; it has no state.</summary>
    public static NoPowerControl Instance { get; } = new();

    /// <inheritdoc />
    public PowerUnit Unit => PowerUnit.None;

    /// <inheritdoc />
    public bool CanSet => false;

    /// <inheritdoc />
    public double? Maximum => null;

    /// <inheritdoc />
    public ValueTask<PowerReading> ReadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new PowerReading(PowerUnit.None, 0, null, "n/a"));

    /// <inheritdoc />
    public ValueTask SetAsync(double setting, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "this device has no power control - set the power on the radio itself");
}
