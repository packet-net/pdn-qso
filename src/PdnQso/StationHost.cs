using Packet.SoundModem.Ident;
using Packet.SoundModem.Modems;
using PdnQso.Config;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Devices;
using PdnQso.Link.Logging;

// The host has a Station property, which shadows the type of the same name inside this class;
// the alias keeps "build a station" readable rather than fully qualified at every use.
using LinkStation = PdnQso.Link.Station;

namespace PdnQso;

/// <summary>
/// The station, and everything that has to be built and torn down with it: the device, the
/// modem, the frame log and the Morse ident.
/// </summary>
/// <remarks>
/// <para>
/// The UI holds one of these and asks it to apply a config. Changing the device, the mode or
/// the audio centre means a new modem at a possibly different sample rate over a possibly
/// different radio, so the honest answer is to take the station down and build another - which
/// is what <see cref="ApplyAsync"/> does, and why every activity is re-attached afterwards.
/// </para>
/// <para>
/// Nothing here touches a screen. The UI subscribes to <see cref="StationChanged"/> and
/// <see cref="Log"/>, and marshals to its own thread itself.
/// </para>
/// </remarks>
public sealed class StationHost : IAsyncDisposable
{
    private static readonly TimeSpan IdentPollInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _swap = new(1, 1);
    private IAudioDevice? _device;
    private LinkStation? _station;
    private FrameLogWriter? _frameLog;
    private StationIdentifier? _ident;
    private CancellationTokenSource? _identStop;
    private Task? _identLoop;
    private bool _disposed;

    /// <summary>Builds a host for a config that has not been started yet.</summary>
    /// <param name="config">What to build.</param>
    /// <param name="monitorOnly">Lock the transmitter out for this session.</param>
    public StationHost(QsoConfig config, bool monitorOnly = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        MonitorOnly = monitorOnly;
    }

    /// <summary>The config the station is running.</summary>
    public QsoConfig Config { get; private set; }

    /// <summary>True when the transmitter is locked out for this session.</summary>
    public bool MonitorOnly { get; }

    /// <summary>The live station, or null before the first start and between restarts.</summary>
    public IStation? Station => _station;

    /// <summary>The frame log being written, or null when the config asked for none.</summary>
    public FrameLogWriter? FrameLog => _frameLog;

    /// <summary>Raised when the station has been replaced; every activity is re-attached.</summary>
    public event Action<IStation>? StationChanged;

    /// <summary>Anything worth putting in the log pane.</summary>
    public event Action<string>? Log;

    /// <summary>Builds and starts a station for <see cref="Config"/>.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ApplyAsync(Config, cancellationToken);

    /// <summary>
    /// Applies a config, restarting the station over it.
    /// </summary>
    /// <remarks>
    /// The old station goes down first, on purpose: two stations on one sound card is an error
    /// with a worse message than "device busy", and on a Flex it is a fight over a slice.
    /// </remarks>
    /// <exception cref="ArgumentException">The config will not start a station; the message
    /// lists what is wrong with it.</exception>
    public async Task ApplyAsync(QsoConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        IReadOnlyList<string> problems = config.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(
                "these settings will not start a station: " + string.Join(" ", problems),
                nameof(config));
        }

        await _swap.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);

            DeviceString device = DeviceString.Parse(config.Device);
            int rate = ModemCatalog.DspRateFor(config.Mode);

            IAudioDevice opened = await DeviceFactory
                .CreateAsync(
                    device, rate, config.ToDeviceOptions(Note, e => Note($"audio: {e.Message}")),
                    cancellationToken)
                .ConfigureAwait(false);

            if (MonitorOnly)
            {
                opened = new ReceiveOnlyDevice(opened);
            }

            _device = opened;

            try
            {
                _frameLog = config.ResolvedFrameLogPath is string logPath
                    ? FrameLogWriter.Open(logPath)
                    : null;

                _station = LinkStation.Create(
                    config.ToStationOptions(), opened, config.Mode, config.ToModemOptions(), _frameLog);
                _station.Start();
            }
            catch
            {
                opened.Dispose();
                _device = null;
                if (_frameLog is not null)
                {
                    await _frameLog.DisposeAsync().ConfigureAwait(false);
                    _frameLog = null;
                }

                throw;
            }

            Config = config;
            Note($"station: {config.Callsign} on {opened.Name}, {config.Mode} at {rate} Hz"
                + (MonitorOnly ? " (monitor only, transmitter locked out)" : ""));

            await ApplyPowerAsync(config, cancellationToken).ConfigureAwait(false);
            StartIdent(config, opened, rate);

            StationChanged?.Invoke(_station);
        }
        finally
        {
            _swap.Release();
        }
    }

    /// <summary>Sets the power the config asks for, if the device has a power control.</summary>
    private async Task ApplyPowerAsync(QsoConfig config, CancellationToken cancellationToken)
    {
        if (_device is not IAudioDevice device)
        {
            return;
        }

        if (config.Power is not double wanted || !device.Power.CanSet)
        {
            return;
        }

        try
        {
            await device.Power.SetAsync(wanted, cancellationToken).ConfigureAwait(false);
            PowerReading reading = await device.Power.ReadAsync(cancellationToken).ConfigureAwait(false);
            Note($"power: {reading.Display}");
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException
                                    or NotSupportedException or IOException)
        {
            // A station whose power could not be set still works; it is just not at the level
            // that was asked for, and that is exactly the thing not to be quiet about.
            Note($"power: could not set {wanted:0.#} - {e.Message}");
        }
    }

    private void StartIdent(QsoConfig config, IAudioDevice device, int rate)
    {
        if (!config.IdentEnabled || MonitorOnly || !device.CanTransmit)
        {
            return;
        }

        try
        {
            _ident = new StationIdentifier(
                config.ResolvedIdentCallsign,
                config.Mode,
                config.ResolvedAudioCentreHz ?? 1500,
                config.IdentWpm,
                TimeSpan.FromMinutes(config.IdentIntervalMinutes),
                rate);
        }
        catch (ArgumentException e)
        {
            Note($"ident: not sending one - {e.Message}");
            return;
        }

        // The clock starts at the first transmission, and every keyup is one: a station that
        // has sent nothing owes nothing, and identifying on a timer alone is pure QRM.
        device.PttChanged += OnPttForIdent;

        _identStop = new CancellationTokenSource();
        _identLoop = Task.Run(() => IdentLoopAsync(device, _identStop.Token), CancellationToken.None);
    }

    private void OnPttForIdent(bool keyed)
    {
        if (keyed)
        {
            _ident?.NoteTransmission();
        }
    }

    private async Task IdentLoopAsync(IAudioDevice device, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IdentPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_ident is not StationIdentifier ident
                || !ident.IdentificationDue
                || _station is not LinkStation station
                || station.Busy
                || station.Transmitting)
            {
                continue;
            }

            try
            {
                // Straight to the device rather than through a link frame: this is Morse, not
                // packet. The device's own keyup lock is what keeps it out of the middle of a
                // burst, and NoteIdentified goes last so the keyup it just made does not leave
                // another ident owed.
                await device.TransmitAsync(ident.Render(), cancellationToken).ConfigureAwait(false);
                ident.NoteIdentified();
                Note($"ident: sent {ident.Text}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Note($"ident: could not send - {e.Message}");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await TearDownAsync().ConfigureAwait(false);
        _swap.Dispose();
    }

    private async Task TearDownAsync()
    {
        if (_identStop is not null)
        {
            await _identStop.CancelAsync().ConfigureAwait(false);
        }

        if (_identLoop is not null)
        {
            try
            {
                await _identLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                // It is a poll loop on a cancelled token; if it will not come back in two
                // seconds it is stuck in a keyup that is about to be disposed anyway.
            }
        }

        _identStop?.Dispose();
        _identStop = null;
        _identLoop = null;
        _ident = null;

        if (_device is not null)
        {
            _device.PttChanged -= OnPttForIdent;
        }

        if (_station is not null)
        {
            // The station owns the device and the frame log and disposes both, which is what
            // drops the PTT and closes the pipes.
            await _station.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _device?.Dispose();
            if (_frameLog is not null)
            {
                await _frameLog.DisposeAsync().ConfigureAwait(false);
            }
        }

        _station = null;
        _device = null;
        _frameLog = null;
    }

    private void Note(string line) => Log?.Invoke(line);
}
