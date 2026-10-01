using PdnQso.Link.Tait;

namespace PdnQso.Tests.Headless;

/// <summary>
/// A Tait link whose frames cost no air time: a send hands the frame to the far end on the
/// sending thread and is done. With nothing parked on a timer, a run over a pair of these
/// finishes on facts alone and never needs the clock moved.
/// </summary>
internal sealed class InstantTaitLink : ITaitLink
{
    public InstantTaitLink? Peer { get; set; }

    public string Mode => TaitModes.Sdm;

    public string Name { get; init; } = "/dev/ttyUSB0";

    public int MaxFrameBytes => SdmFrameCodec.MaxFrameBytes;

    public bool Busy => false;

    public event Action<byte[]>? FrameReceived;

    public static (InstantTaitLink A, InstantTaitLink B) Pair()
    {
        var a = new InstantTaitLink { Name = "/dev/ttyUSB0" };
        var b = new InstantTaitLink { Name = "/dev/ttyUSB1", Peer = a };
        a.Peer = b;
        return (a, b);
    }

    public Task SendAsync(ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Peer?.FrameReceived?.Invoke(ax25Frame.ToArray());
        return Task.CompletedTask;
    }

    public TimeSpan EstimateAirTime(int ax25FrameBytes) => TimeSpan.FromMilliseconds(ax25FrameBytes);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
