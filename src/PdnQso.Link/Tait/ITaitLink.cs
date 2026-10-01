namespace PdnQso.Link.Tait;

/// <summary>
/// One of a Tait radio's own modems, as a <see cref="TaitStation"/> sees it: whole AX.25
/// frames in and out, nothing about audio.
/// </summary>
/// <remarks>
/// The seam between the station, which is this tool's, and the radio drivers, which are
/// packet.net's and M0LTE.Tait.Ccdi's. A test puts a pair of fakes here and gets a two-station
/// rig with no radio.
/// </remarks>
public interface ITaitLink : IAsyncDisposable
{
    /// <summary>The mode this link runs, one of <see cref="TaitModes.All"/>.</summary>
    string Mode { get; }

    /// <summary>Where the radio is, for the status bar and the log.</summary>
    string Name { get; }

    /// <summary>The largest frame <see cref="SendAsync"/> will take.</summary>
    int MaxFrameBytes { get; }

    /// <summary>
    /// True while the radio reports somebody else's carrier. False when it cannot tell, which
    /// in Transparent mode is always: the control channel is the byte pipe.
    /// </summary>
    bool Busy { get; }

    /// <summary>Every frame the radio delivered, on whatever thread delivered it.</summary>
    event Action<byte[]>? FrameReceived;

    /// <summary>Sends one frame.</summary>
    /// <returns>A task that completes when the frame has left the antenna, as nearly as the
    /// radio lets that be known, so that a caller timing the send is timing the air.</returns>
    Task SendAsync(ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default);

    /// <summary>How long a frame of <paramref name="ax25FrameBytes"/> takes on air.</summary>
    TimeSpan EstimateAirTime(int ax25FrameBytes);
}
