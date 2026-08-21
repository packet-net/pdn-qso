using Packet.SoundModem.Modems;
using PdnQso.Link.Devices;

namespace PdnQso.Link;

/// <summary>
/// One radio, as the rest of the tool sees it: link frames in, link frames out, and the lamps
/// the status bar needs.
/// </summary>
/// <remarks>
/// Design.md section 5. Everything above a station talks in <see cref="LinkFrame"/>s;
/// everything below it is pdn-soundmodem. Receive is always on.
/// </remarks>
public interface IStation : IAsyncDisposable
{
    /// <summary>This station's callsign, <c>CALL</c> or <c>CALL-SSID</c>.</summary>
    string Callsign { get; }

    /// <summary>The modem mode, as the modem reports itself.</summary>
    string Mode { get; }

    /// <summary>The device this station is on.</summary>
    string DeviceName { get; }

    /// <summary>False on a receive-only device: <see cref="SendAsync"/> will throw.</summary>
    bool CanTransmit { get; }

    /// <summary>True while somebody else is using the channel - the DCD lamp.</summary>
    bool Busy { get; }

    /// <summary>True while this station is keyed - the PTT lamp.</summary>
    bool Transmitting { get; }

    /// <summary>This station's transmit power, or <see cref="NoPowerControl"/>.</summary>
    IPowerControl Power { get; }

    /// <summary>
    /// The modem underneath, for a caller that needs the mode's own controls - MS110D's
    /// <c>IHardwareControllable</c> waveform lever, or the modulator, to measure a burst's air
    /// time from its own sample count.
    /// </summary>
    /// <remarks>
    /// On the contract rather than on the concrete station because the chat ladder and the perf
    /// run both need it, and reaching for it by casting to <see cref="Station"/> made every
    /// other implementation of this interface - a test's shared-medium wrapper, say - silently
    /// lose the lever.
    /// </remarks>
    IModem Modem { get; }

    /// <summary>
    /// Every frame heard that is one of ours, decoded. Fires after
    /// <see cref="RawFrameReceived"/> for the same frame.
    /// </summary>
    event Action<LinkFrame, FrameQuality>? FrameReceived;

    /// <summary>
    /// Every frame heard, ours or not, exactly as the modem delivered it. This is Monitor's
    /// feed: on a shared channel most of what is heard belongs to somebody else and the whole
    /// point of the pane is to show it.
    /// </summary>
    event Action<byte[], FrameQuality>? RawFrameReceived;

    /// <summary>
    /// Every frame this station has finished transmitting: the decoded link frame when it is
    /// one of ours and <see langword="null"/> when it is not (an ident, a raw frame from a
    /// corpus), and the bytes that went on air either way.
    /// </summary>
    /// <remarks>
    /// Raised after the transmitter has dropped, not before it comes up, so a Monitor pane
    /// showing it is showing something that happened. It is the pane's other half: without it
    /// an operator sees everybody's traffic but their own, and on a link where nothing is
    /// coming back that is the one thing they need to be sure of. The frame log has always
    /// recorded these; this is the same fact, on the screen.
    /// </remarks>
    event Action<LinkFrame?, byte[]>? FrameTransmitted;

    /// <summary>Opens the device and starts receiving.</summary>
    void Start();

    /// <summary>
    /// Sends one link frame, waiting for the channel to clear first.
    /// </summary>
    /// <returns>A task that completes when the frame has left the transmitter.</returns>
    /// <exception cref="InvalidOperationException">The device is receive only.</exception>
    /// <exception cref="TimeoutException">The channel never cleared.</exception>
    Task SendAsync(LinkFrame frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an AX.25 frame this station did not build - an ident, a beacon, a frame from a
    /// test corpus - through the same queue and the same channel discipline.
    /// </summary>
    Task SendRawAsync(ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default);

    /// <summary>Builds a link frame from this station, ready to send.</summary>
    LinkFrame Frame(LinkFrameType type, byte session, ReadOnlySpan<byte> payload = default);
}
