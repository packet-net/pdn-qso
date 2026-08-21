using Packet.SoundModem.Modems;
using PdnQso.Link.Audio;
using PdnQso.Link.Devices;
using PdnQso.Link.Logging;

namespace PdnQso.Link;

/// <summary>
/// One device, one modem, one transmit queue: the whole of design.md section 5.
/// </summary>
/// <remarks>
/// <para>
/// The station owns the receive pump - it feeds the device's samples to the modem - and the
/// transmit queue, which takes one frame at a time and will not key up while the channel is
/// busy. Above it everything is link frames; below it everything is pdn-soundmodem.
/// </para>
/// <para>
/// Both frame events fire on whatever thread the audio arrived on, which for the real devices
/// is the capture thread and for <see cref="AudioLink"/> is the far station's transmitting
/// thread. A handler that wants to send something back should queue it rather than await a
/// send from inside the event.
/// </para>
/// <para>
/// The ident (pdn-soundmodem's <c>StationIdentifier</c>), the waveform step-down lever and the
/// ARQ all sit above this and arrive in later phases. This class deliberately knows nothing
/// about them: it moves frames.
/// </para>
/// </remarks>
public sealed class Station : IStation
{
    private readonly StationOptions _options;
    private readonly IAudioDevice _device;
    private readonly IModem _modem;
    private readonly IBusyGate _busy;
    private readonly FrameLogWriter? _frameLog;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _transmit = new(1, 1);
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Builds a station over a device and a modem that already exist.
    /// </summary>
    /// <param name="options">Callsign, TXDELAY and channel patience.</param>
    /// <param name="device">The radio's audio and PTT.</param>
    /// <param name="modem">The modem, at the device's sample rate.</param>
    /// <param name="busy">The channel gate; <see cref="ModemBusyGate"/> over
    /// <paramref name="modem"/> when omitted.</param>
    /// <param name="frameLog">Where to record every frame heard and sent; null for no log.</param>
    /// <param name="timeProvider">Wall clock, for the busy wait. <see cref="TimeProvider.System"/>
    /// when omitted.</param>
    /// <exception cref="ArgumentException">The modem's DSP rate is not the device's sample
    /// rate, so one of them would be running at the wrong speed and nothing would decode.</exception>
    /// <param name="dspRateHz">The sample rate the modem was built for, when the caller knows it
    /// (<see cref="Create"/> always does); a modem's reported mode name is not always a
    /// catalogue key, so this is the authoritative figure for the rate check.</param>
    public Station(
        StationOptions options,
        IAudioDevice device,
        IModem modem,
        IBusyGate? busy = null,
        FrameLogWriter? frameLog = null,
        TimeProvider? timeProvider = null,
        int? dspRateHz = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(modem);

        // The rate check guards against a modem and a device running at different speeds.
        // A modem's self-reported Mode is not always a catalogue key (fsk9600-il2p reports
        // fsk9600-il2pc, for which the catalogue answers a different rate), so the caller
        // who built the modem passes the rate it asked for; without it the check runs only
        // when the reported mode is a name the catalogue knows.
        int? modemRate = dspRateHz
            ?? (ModemCatalog.KnownModes.Contains(modem.Mode) ? ModemCatalog.DspRateFor(modem.Mode) : null);
        if (modemRate is { } rate && rate != device.SampleRate)
        {
            throw new ArgumentException(
                $"mode '{modem.Mode}' runs at {rate} Hz and device '{device.Name}' at "
                + $"{device.SampleRate} Hz - nothing would decode",
                nameof(modem));
        }

        _options = options;
        _device = device;
        _modem = modem;
        _busy = busy ?? new ModemBusyGate(modem);
        _frameLog = frameLog;
        _time = timeProvider ?? TimeProvider.System;

        // Validated here rather than at the first send, so a bad callsign is a start-up error
        // and not a surprise thirty minutes into a QSO.
        Callsign = new LinkFrame(options.Callsign, LinkFrameType.Hello, 0).Source;
    }

    /// <summary>
    /// Builds a station and its modem together, from a mode string.
    /// </summary>
    /// <param name="options">Callsign, TXDELAY and channel patience.</param>
    /// <param name="device">The radio's audio and PTT.</param>
    /// <param name="mode">A mode string from <c>ModemCatalog.KnownModes</c>.</param>
    /// <param name="modemOptions">Per-mode modem knobs (audio centre, plain-IL2P tolerance).</param>
    /// <param name="frameLog">Where to record every frame heard and sent; null for no log.</param>
    /// <param name="timeProvider">Wall clock, for the busy wait.</param>
    public static Station Create(
        StationOptions options,
        IAudioDevice device,
        string mode,
        ModemOptions modemOptions = default,
        FrameLogWriter? frameLog = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        IModem modem = ModemCatalog.Create(mode, device.SampleRate, _ => { }, modemOptions);
        return new Station(options, device, modem, busy: null, frameLog, timeProvider, ModemCatalog.DspRateFor(mode));
    }

    /// <inheritdoc />
    public string Callsign { get; }

    /// <inheritdoc />
    public string Mode => _modem.Mode;

    /// <inheritdoc />
    public string DeviceName => _device.Name;

    /// <inheritdoc />
    public bool CanTransmit => _device.CanTransmit;

    /// <inheritdoc />
    public bool Busy => _busy.Busy;

    /// <inheritdoc />
    public bool Transmitting => _device.Ptt;

    /// <inheritdoc />
    public IPowerControl Power => _device.Power;

    /// <summary>The modem underneath, for a caller that needs the mode's own controls -
    /// MS110D's <c>IHardwareControllable</c> waveform lever, say.</summary>
    public IModem Modem => _modem;

    /// <inheritdoc />
    public event Action<LinkFrame, FrameQuality>? FrameReceived;

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? RawFrameReceived;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _device.SamplesReceived += OnSamples;
        _modem.FrameDecoded += OnFrameDecoded;
        _device.Start();
    }

    /// <inheritdoc />
    public LinkFrame Frame(LinkFrameType type, byte session, ReadOnlySpan<byte> payload = default) =>
        new(Callsign, type, session, payload, _options.Destination);

    /// <inheritdoc />
    public Task SendAsync(LinkFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return SendRawAsync(frame.Encode(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendRawAsync(
        ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_device.CanTransmit)
        {
            throw new InvalidOperationException(
                $"'{_device.Name}' is a receive-only device - this station cannot transmit");
        }

        if (ax25Frame.Length == 0)
        {
            throw new ArgumentException("an empty frame is not a frame", nameof(ax25Frame));
        }

        // One frame at a time. Everything above waits its turn rather than interleaving two
        // bursts into one keyup, which on air would be one unreadable frame instead of two.
        await _transmit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForClearChannelAsync(cancellationToken).ConfigureAwait(false);

            float[] audio = _modem.Modulate(ax25Frame.Span, _options.TxDelayMilliseconds);

            // The receiver hears our own transmission on most devices; clearing carrier state
            // stops that being read as somebody else holding the channel afterwards.
            _busy.Reset();
            await _device.TransmitAsync(audio, cancellationToken).ConfigureAwait(false);
            _busy.Reset();

            _frameLog?.RecordTransmitted(
                _options.SubChannel, ax25Frame.ToArray(), _modem.Mode,
                _options.AudioCentreHz, _options.RfHz);
        }
        finally
        {
            _transmit.Release();
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
        if (_started)
        {
            _device.SamplesReceived -= OnSamples;
            _modem.FrameDecoded -= OnFrameDecoded;
        }

        _device.Dispose();
        _transmit.Dispose();
        if (_frameLog is not null)
        {
            await _frameLog.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits out anybody else on the channel. Polls rather than waits on an event because the
    /// busy gates underneath are level-triggered - "is it busy now" - and a poll of a level is
    /// simpler to be right about than an edge nobody promised to raise.
    /// </summary>
    private async Task WaitForClearChannelAsync(CancellationToken cancellationToken)
    {
        if (!_busy.Busy)
        {
            return;
        }

        DateTimeOffset deadline = _time.GetUtcNow() + _options.BusyWaitTimeout;
        while (_busy.Busy)
        {
            if (_time.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"the channel was still busy after {_options.BusyWaitTimeout.TotalSeconds:0.#} s "
                    + "- this frame was not sent");
            }

            await Task.Delay(_options.BusyPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnSamples(float[] samples)
    {
        if (_disposed)
        {
            return;
        }

        _modem.Process(samples);
        if (_busy is EnergyBusyGate energy)
        {
            energy.Process(samples);
        }
    }

    private void OnFrameDecoded(byte[] frame, FrameQuality quality)
    {
        _frameLog?.Record(
            _options.SubChannel, frame, quality, _options.AudioCentreHz, _options.RfHz);

        RawFrameReceived?.Invoke(frame, quality);

        // A frame carrying FrameQuality.MonitorOnly is one the modem heard and would not hand
        // to a KISS host. It is still a frame this station heard, so it is delivered here and
        // the flag travels with it - deciding what to do about an RS-only chat line is the
        // ARQ's business, not the station's.
        if (LinkFrame.TryDecode(frame, out LinkFrame? link))
        {
            FrameReceived?.Invoke(link, quality);
        }
    }
}
