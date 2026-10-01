using Packet.Ax25.Radio.Tait;
using Packet.Ax25.Transport;

namespace PdnQso.Link.Tait;

/// <summary>
/// The radio's FFSK modem in Transparent mode, through packet.net's
/// <see cref="TaitTransparentTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// All the radio work is the transport's: entering Transparent mode (with recovery from a
/// radio a previous session left in it), SLIP framing, and escaping back to Command mode on
/// the way out. This class turns its receive stream into an event and its modelled transmit
/// completion into a send that ends when the frame is off the air.
/// </para>
/// <para>
/// There is no carrier sense in Transparent mode: the serial port is the data, so nothing
/// else can come down it. <see cref="Busy"/> is always false and the radio's own channel
/// access, as programmed, is what keeps it off a busy channel.
/// </para>
/// </remarks>
public sealed class TaitFfskLink : ITaitLink
{
    private readonly ITxCompletionTransport _transport;
    private readonly TaitTransparentTransportOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiving;
    private bool _disposed;

    /// <summary>Wraps a transport that is already in Transparent mode.</summary>
    /// <param name="transport">The transport; this link owns it and disposes it.</param>
    /// <param name="options">The options it was opened with, for the air-time estimate.</param>
    /// <param name="name">Where the radio is, for the log.</param>
    public TaitFfskLink(ITxCompletionTransport transport, TaitTransparentTransportOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        _transport = transport;
        _options = options;
        Name = name;
        _receiving = Task.Run(ReceiveAsync, CancellationToken.None);
    }

    /// <summary>Opens the radio on a serial port and puts it into Transparent mode.</summary>
    /// <param name="settings">Port, rates.</param>
    /// <param name="timeProvider">The clock the transport times transmissions with.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public static async Task<TaitFfskLink> OpenAsync(
        TaitSettings settings, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new TaitTransparentTransportOptions
        {
            CommandBaud = settings.BaudRate,
            TransparentBaud = settings.BaudRate,
            FfskBaud = settings.FfskBaud,
        };

        TaitTransparentTransport transport = await TaitTransparentTransport
            .OpenAsync(settings.PortName, options, timeProvider ?? TimeProvider.System, cancellationToken)
            .ConfigureAwait(false);
        return new TaitFfskLink(transport, options, settings.PortName);
    }

    /// <inheritdoc />
    public string Mode => TaitModes.Ffsk;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int MaxFrameBytes => TaitModes.MaxFrameBytes(TaitModes.Ffsk);

    /// <inheritdoc />
    public bool Busy => false;

    /// <inheritdoc />
    public event Action<byte[]>? FrameReceived;

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The transport has no hardware signal for the end of a transmission, so it waits out
        // its own model of one: lead-in plus the framed bytes at the over-air rate.
        return _transport.SendAwaitingCompletionAsync(ax25Frame, timeout: null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The transport's own model: the lead-in, then the SLIP-framed bytes (a FEND either side
    /// and a port byte, ignoring the rare escaped byte) at the over-air rate. A floor, since the
    /// radio adds framing of its own per block that the host never sees.
    /// </remarks>
    public TimeSpan EstimateAirTime(int ax25FrameBytes) =>
        _options.LeadIn + TimeSpan.FromSeconds((ax25FrameBytes + 3) * 8.0 / _options.FfskBaud);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);

        // The transport's dispose escapes Transparent mode, which is the one thing that must
        // happen on the way out: a radio left in it is deaf to everything until the next open
        // recovers it.
        await _transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _receiving.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task ReceiveAsync()
    {
        await foreach (Ax25InboundFrame frame in _transport.ReceiveAsync(_stop.Token).ConfigureAwait(false))
        {
            FrameReceived?.Invoke(frame.Ax25.ToArray());
        }
    }
}
