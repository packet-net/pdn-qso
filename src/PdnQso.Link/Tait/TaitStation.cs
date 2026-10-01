using Packet.SoundModem.Modems;
using PdnQso.Link.Devices;
using PdnQso.Link.Logging;

namespace PdnQso.Link.Tait;

/// <summary>
/// A station whose modem is the radio: a Tait TM8100/TM8200 running one of its own modems,
/// reached over its CCDI serial port. The same contract as <see cref="Station"/>, with no
/// audio anywhere.
/// </summary>
/// <remarks>
/// <para>
/// Everything above a station is unchanged: chat, file and perf hand it link frames and get
/// link frames back. What changes is underneath. There is no <see cref="IModem"/>, so
/// <see cref="Modem"/> is null and anything that reached for it (the MS110D waveform ladder,
/// a modulated air-time probe) has to do without; <see cref="EstimateAirTime"/> stands in for
/// the probe.
/// </para>
/// <para>
/// Frames are delivered with what is known about them, which is little: the radio reports no
/// SNR, carrier offset or correction count for a data frame, so those are left null rather
/// than made up.
/// </para>
/// </remarks>
public sealed class TaitStation : IStation
{
    private readonly StationOptions _options;
    private readonly ITaitLink _link;
    private readonly FrameLogWriter? _frameLog;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _transmit = new(1, 1);
    private volatile bool _transmitting;
    private bool _started;
    private bool _disposed;

    /// <summary>Builds a station over a link that is already open.</summary>
    /// <param name="options">Callsign and channel patience. TXDELAY and the audio centre do
    /// not apply: the radio keys and modulates itself.</param>
    /// <param name="link">The radio's modem; the station owns it and disposes it.</param>
    /// <param name="canTransmit">False to lock the transmitter out.</param>
    /// <param name="frameLog">Where to record every frame heard and sent; null for no log.</param>
    /// <param name="timeProvider">Wall clock, for the busy wait.</param>
    public TaitStation(
        StationOptions options,
        ITaitLink link,
        bool canTransmit = true,
        FrameLogWriter? frameLog = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(link);
        _options = options;
        _link = link;
        CanTransmit = canTransmit;
        _frameLog = frameLog;
        _time = timeProvider ?? TimeProvider.System;
        Callsign = new LinkFrame(options.Callsign, LinkFrameType.Hello, 0).Source;
    }

    /// <summary>Opens the radio in <paramref name="mode"/> and builds a station over it.</summary>
    /// <param name="options">Callsign and channel patience.</param>
    /// <param name="settings">Where the radio is and how it is programmed.</param>
    /// <param name="mode">One of <see cref="TaitModes.All"/>.</param>
    /// <param name="canTransmit">False to lock the transmitter out.</param>
    /// <param name="frameLog">Where to record every frame; null for no log.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not a Tait mode.</exception>
    public static async Task<TaitStation> OpenAsync(
        StationOptions options,
        TaitSettings settings,
        string mode,
        bool canTransmit = true,
        FrameLogWriter? frameLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ITaitLink link = mode switch
        {
            TaitModes.Ffsk => await TaitFfskLink.OpenAsync(settings, cancellationToken: cancellationToken)
                .ConfigureAwait(false),
            TaitModes.Sdm => await TaitSdmLink.OpenAsync(settings, cancellationToken: cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentException($"'{mode}' is not a Tait mode", nameof(mode)),
        };

        try
        {
            return new TaitStation(options, link, canTransmit, frameLog);
        }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public string Callsign { get; }

    /// <inheritdoc />
    public string Mode => _link.Mode;

    /// <inheritdoc />
    public string DeviceName => $"tait:{_link.Name}";

    /// <inheritdoc />
    public bool CanTransmit { get; }

    /// <inheritdoc />
    public bool Busy => _link.Busy;

    /// <inheritdoc />
    public bool Transmitting => _transmitting;

    /// <inheritdoc />
    /// <remarks>The radio's power is set in its programming, not from here.</remarks>
    public IPowerControl Power => NoPowerControl.Instance;

    /// <inheritdoc />
    /// <remarks>Always null: the modem is inside the radio.</remarks>
    public IModem? Modem => null;

    /// <summary>The largest frame this station can send.</summary>
    public int MaxFrameBytes => _link.MaxFrameBytes;

    /// <inheritdoc />
    public event Action<LinkFrame, FrameQuality>? FrameReceived;

    /// <inheritdoc />
    public event Action<byte[], FrameQuality>? RawFrameReceived;

    /// <inheritdoc />
    public event Action<LinkFrame?, byte[]>? FrameTransmitted;

    /// <summary>How long a frame of <paramref name="ax25FrameBytes"/> takes on air.</summary>
    public TimeSpan EstimateAirTime(int ax25FrameBytes) => _link.EstimateAirTime(ax25FrameBytes);

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _link.FrameReceived += OnFrame;
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
    /// <exception cref="ArgumentException">The frame is bigger than the mode carries.</exception>
    public async Task SendRawAsync(ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanTransmit)
        {
            throw new InvalidOperationException(
                $"'{DeviceName}' is locked to receive only - this station cannot transmit");
        }

        if (ax25Frame.Length == 0)
        {
            throw new ArgumentException("an empty frame is not a frame", nameof(ax25Frame));
        }

        if (ax25Frame.Length > _link.MaxFrameBytes)
        {
            throw new ArgumentException(
                $"the frame is {ax25Frame.Length} bytes and {Mode} carries at most {_link.MaxFrameBytes}",
                nameof(ax25Frame));
        }

        byte[]? transmitted = null;
        await _transmit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForClearChannelAsync(cancellationToken).ConfigureAwait(false);

            _transmitting = true;
            try
            {
                await _link.SendAsync(ax25Frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _transmitting = false;
            }

            byte[] sent = ax25Frame.ToArray();
            _frameLog?.RecordTransmitted(_options.SubChannel, sent, Mode, null, _options.RfHz);
            transmitted = sent;
        }
        finally
        {
            _transmit.Release();
        }

        // After the frame is off the air and outside the lock, as Station does, so a handler
        // that answers by sending is a slow handler rather than a deadlocked one.
        _ = LinkFrame.TryDecode(transmitted, out LinkFrame? link);
        FrameTransmitted?.Invoke(link, transmitted);
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
            _link.FrameReceived -= OnFrame;
        }

        await _link.DisposeAsync().ConfigureAwait(false);
        _transmit.Dispose();
        if (_frameLog is not null)
        {
            await _frameLog.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WaitForClearChannelAsync(CancellationToken cancellationToken)
    {
        if (!_link.Busy)
        {
            return;
        }

        DateTimeOffset deadline = _time.GetUtcNow() + _options.BusyWaitTimeout;
        while (_link.Busy)
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

    private void OnFrame(byte[] frame)
    {
        if (_disposed)
        {
            return;
        }

        // What the radio tells us about a data frame is that it arrived and how long it is.
        // Whether it was checked on the way is the radio's business, so CRC is unknown, not
        // claimed.
        var quality = new FrameQuality(Mode, frame.Length, CorrectedBytes: null, CrcValid: null);
        _frameLog?.Record(_options.SubChannel, frame, quality, null, _options.RfHz);
        RawFrameReceived?.Invoke(frame, quality);
        if (LinkFrame.TryDecode(frame, out LinkFrame? link))
        {
            FrameReceived?.Invoke(link, quality);
        }
    }
}
