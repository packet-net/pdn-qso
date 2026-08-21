using System.Globalization;
using M0LTE.Flex;

namespace PdnQso.Link.Devices;

/// <summary>
/// A FlexRadio's transmit power: <c>rfpower</c> set in watts, and the radio's own forward-power
/// meter read back so the UI can show "set 10 W, last 9.6 W" rather than only what was asked
/// for.
/// </summary>
/// <remarks>
/// <para>
/// Design.md section 4a, and the difference between the two numbers is the point: on a shared
/// rig somebody else's client may have moved the power, the ALC may not be doing what the
/// setting says, and an antenna that is wrong shows up here first. The setting alone is a
/// statement about our own intent; the meter is a statement about the transmission.
/// </para>
/// <para>
/// <b>Watts and the radio's 0-100 number.</b> Every 6000-series radio has a 100 W PA, which is
/// why pdn-soundmodem's <c>FlexDevice.PaWatts</c> treats the two as coinciding. The conversion
/// is kept explicit anyway so that a station ceiling expressed in watts stays in watts.
/// </para>
/// <para>
/// <b>The ceiling is refused, never clamped.</b> <see cref="Maximum"/> comes from what the
/// radio reports as <c>max_power_level</c> - on Tom's station that is the 15 W the station is
/// set to, not the PA's 100 W - and a request above it throws. A tool that quietly transmits
/// 15 W when it was asked for 50 has hidden an operator's mistake rather than caught it.
/// </para>
/// <para>
/// Meter samples only arrive while the radio is transmitting, so what is reported between
/// bursts is the peak of the last one: a packet burst is over long before anyone can read a
/// live figure off a screen.
/// </para>
/// </remarks>
public sealed class FlexPowerControl : IPowerControl, IDisposable
{
    /// <summary>The radio's forward-power meter, by the name it describes itself with.</summary>
    public const string ForwardPowerMeter = "FWDPWR";

    /// <summary>
    /// Below this the transmitter is not really on: meter samples at rest sit near zero and
    /// reporting them as "the last transmission" would be a lie about a burst that never
    /// happened.
    /// </summary>
    public const double TransmittingFloorWatts = 0.2;

    private readonly FlexClient _client;
    private readonly FlexMeters? _meters;
    private readonly Lock _gate = new();
    private double _burstPeakWatts;
    private double? _lastBurstWatts;
    private double? _setting;
    private bool _disposed;

    /// <summary>Builds a power control over an open radio session.</summary>
    /// <param name="client">The connected radio.</param>
    /// <param name="meters">The radio's meter telemetry, or null when it could not be
    /// subscribed - the power is still settable, there is simply nothing to read back.</param>
    /// <param name="maximumWatts">What the radio reports it will accept; null when it did not
    /// say, in which case nothing is refused on the way out and the radio has the last word.</param>
    /// <param name="initialWatts">What the radio was already set to, if that was read at
    /// bring-up.</param>
    public FlexPowerControl(
        FlexClient client,
        FlexMeters? meters = null,
        double? maximumWatts = null,
        double? initialWatts = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _meters = meters;
        Maximum = maximumWatts;
        _setting = initialWatts;

        if (meters is not null)
        {
            meters.Updated += OnMeter;
        }
    }

    /// <inheritdoc />
    public PowerUnit Unit => PowerUnit.Watts;

    /// <inheritdoc />
    public bool CanSet => true;

    /// <inheritdoc />
    public double? Maximum { get; }

    /// <summary>
    /// Subscribes to the radio's transmit state and builds a power control over it, so that
    /// both the ceiling and the current setting come from the radio rather than from us.
    /// </summary>
    /// <remarks>
    /// The subscription (<c>sub tx all</c>) is what makes <c>rfpower</c> and
    /// <c>max_power_level</c> readable at all, and it keeps them current afterwards: on a
    /// shared radio another client moving the power has to show up here, or the status bar is
    /// reporting our intentions rather than the transmission.
    /// </remarks>
    /// <param name="client">The connected radio.</param>
    /// <param name="meters">The radio's meter telemetry, or null when it could not be
    /// subscribed.</param>
    /// <param name="stationMaxLevel">What the bring-up already read back, if anything -
    /// <c>FlexStation.MaxPowerLevel</c>. Saves waiting for the subscription.</param>
    /// <param name="timeProvider">The clock the wait for the first status runs on.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<FlexPowerControl> OpenAsync(
        FlexClient client,
        FlexMeters? meters = null,
        int? stationMaxLevel = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        TimeProvider time = timeProvider ?? TimeProvider.System;

        await client.SendCommandAsync("sub tx all", cancellationToken).ConfigureAwait(false);

        // The subscription's first status arrives on the status stream a moment after the
        // command is answered, so this waits briefly for it rather than reporting a radio with
        // no ceiling purely because we asked too early.
        for (int attempt = 0; attempt < 20 && !TransmitState(client, out _); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), time, cancellationToken)
                .ConfigureAwait(false);
        }

        return new FlexPowerControl(
            client,
            meters,
            MaximumWattsFrom(client, stationMaxLevel),
            ReadLevel(client, "rfpower") is int level ? ToWatts(level) : null);
    }

    /// <summary>
    /// The watts the radio reports it will accept, from <c>transmit max_power_level</c>, or
    /// null when the radio has not told us.
    /// </summary>
    /// <param name="client">The connected radio.</param>
    /// <param name="stationMaxLevel">What the bring-up already read back, if anything -
    /// <c>FlexStation.MaxPowerLevel</c>.</param>
    public static double? MaximumWattsFrom(FlexClient client, int? stationMaxLevel)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (stationMaxLevel is int level)
        {
            return ToWatts(level);
        }

        return ReadLevel(client, "max_power_level") is int max ? ToWatts(max) : null;
    }

    private static bool TransmitState(
        FlexClient client, out IReadOnlyDictionary<string, string>? state) =>
        client.TryGetObject("transmit", out state);

    private static int? ReadLevel(FlexClient client, string field) =>
        TransmitState(client, out IReadOnlyDictionary<string, string>? transmit)
        && transmit!.TryGetValue(field, out string? reported)
        && int.TryParse(reported, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
            ? level
            : null;

    /// <inheritdoc />
    public ValueTask<PowerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double setting = ReadSettingWatts();
        double? measured = LastTransmissionWatts();
        return ValueTask.FromResult(
            new PowerReading(PowerUnit.Watts, setting, measured, Describe(setting, measured)));
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(double setting, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (setting < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(setting), setting, "transmit power cannot be negative");
        }

        if (Maximum is double ceiling && setting > ceiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(setting), setting,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"this radio reports a {ceiling:0.#} W ceiling - refusing {setting:0.#} W "
                    + $"rather than quietly transmitting {ceiling:0.#} W instead"));
        }

        int level = ToLevel(setting);
        FlexResult result = await _client
            .SendCommandAsync(
                string.Create(CultureInfo.InvariantCulture, $"transmit set rfpower={level}"),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsOk)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the radio refused rfpower={level}: error 0x{result.Error:X8} {result.Message}"));
        }

        lock (_gate)
        {
            _setting = setting;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_meters is not null)
        {
            _meters.Updated -= OnMeter;
        }
    }

    /// <summary>The status bar's one-liner, e.g. <c>set 10 W, last 9.6 W</c>.</summary>
    /// <param name="setting">What the radio is set to.</param>
    /// <param name="measured">The peak of the last transmission, or null if it has not
    /// transmitted or the meters are not being read.</param>
    public static string Describe(double setting, double? measured) =>
        measured is double watts
            ? string.Create(CultureInfo.InvariantCulture, $"set {setting:0.#} W, last {watts:0.#} W")
            : string.Create(CultureInfo.InvariantCulture, $"set {setting:0.#} W");

    /// <summary>The radio's 0-100 power number as watts of its 100 W PA.</summary>
    public static double ToWatts(int level) => level / 100.0 * FlexPa.Watts;

    /// <summary>Watts as the radio's 0-100 power number, rounded to the whole number it takes.</summary>
    public static int ToLevel(double watts) =>
        (int)Math.Round(watts / FlexPa.Watts * 100.0, MidpointRounding.AwayFromZero);

    private double ReadSettingWatts()
    {
        // The radio first: another client may have moved the power since we last set it, and
        // on a shared rig it is the radio's answer that shapes the transmission, not ours.
        if (ReadLevel(_client, "rfpower") is int level)
        {
            return ToWatts(level);
        }

        lock (_gate)
        {
            return _setting ?? 0;
        }
    }

    private double? LastTransmissionWatts()
    {
        lock (_gate)
        {
            if (_burstPeakWatts >= TransmittingFloorWatts)
            {
                return _burstPeakWatts;   // keyed right now
            }

            return _lastBurstWatts;
        }
    }

    private void OnMeter(FlexMeterReading reading)
    {
        if (!reading.Descriptor.Name.Equals(ForwardPowerMeter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        double watts = FlexMeters.DbmToWatts(reading.Value);
        lock (_gate)
        {
            if (watts >= TransmittingFloorWatts)
            {
                if (watts > _burstPeakWatts)
                {
                    _burstPeakWatts = watts;
                }

                return;
            }

            // Key-down: freeze the burst's peak as what the last transmission did, and start
            // the next one from nothing.
            if (_burstPeakWatts >= TransmittingFloorWatts)
            {
                _lastBurstWatts = _burstPeakWatts;
            }

            _burstPeakWatts = 0;
        }
    }
}

/// <summary>The 6000-series PA, which every model in the family shares.</summary>
public static class FlexPa
{
    /// <summary>
    /// 100 W. The radio confirms it as <c>slice N max_internal_pa_power</c>, so on this family
    /// watts and the radio's 0-100 power number coincide; the conversion exists to keep an
    /// operator's numbers in watts, not because the arithmetic is hard.
    /// </summary>
    public const double Watts = 100.0;
}
