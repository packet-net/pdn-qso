namespace PdnQso.Link;

/// <summary>
/// What a <see cref="Station"/> needs to know about itself that is not the device or the
/// modem: who it is, how long to hold the transmitter open before the data, and how patient
/// to be about a busy channel.
/// </summary>
public sealed record StationOptions
{
    /// <summary>This station's callsign, <c>CALL</c> or <c>CALL-SSID</c>.</summary>
    public required string Callsign { get; init; }

    /// <summary>
    /// The destination every frame is addressed to. <see cref="LinkFrame.DefaultDestination"/>
    /// unless something has a very good reason.
    /// </summary>
    public string Destination { get; init; } = LinkFrame.DefaultDestination;

    /// <summary>
    /// TXDELAY: how long the transmitter is keyed before the data starts, in milliseconds.
    /// Expressed in the modulated audio by the modem, so it costs air time and nothing else.
    /// </summary>
    public int TxDelayMilliseconds { get; init; } = 300;

    /// <summary>How often to look again while waiting for the channel to clear.</summary>
    public TimeSpan BusyPollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How long to wait for a clear channel before giving up on a frame. A station that waits
    /// for ever looks identical to one that has crashed.
    /// </summary>
    public TimeSpan BusyWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The KISS sub-channel number to record in the frame log.</summary>
    public int SubChannel { get; init; }

    /// <summary>The modem's audio centre, for the frame log's <c>audio_hz</c> column.</summary>
    public double? AudioCentreHz { get; init; }

    /// <summary>The dial frequency, for the frame log's <c>rf_hz</c> column.</summary>
    public double? RfHz { get; init; }
}
