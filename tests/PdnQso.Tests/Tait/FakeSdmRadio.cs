using PdnQso.Link.Tait;

namespace PdnQso.Tests.Tait;

/// <summary>
/// A radio's SDM service with nothing behind it: what is sent is recorded and, when a peer is
/// paired, arrives at the peer at once, on the sending thread, as the audio rig's bursts do.
/// </summary>
internal sealed class FakeSdmRadio : ISdmRadio
{
    private readonly List<(string Destination, byte[] Message)> _sent = [];

    public FakeSdmRadio? Peer { get; set; }

    public bool? ChannelBusy { get; set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<(string Destination, byte[] Message)> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public event Action<byte[]>? MessageReceived;

    public static (FakeSdmRadio A, FakeSdmRadio B) Pair()
    {
        var a = new FakeSdmRadio();
        var b = new FakeSdmRadio { Peer = a };
        a.Peer = b;
        return (a, b);
    }

    public Task SendAsync(string destination, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] copy = message.ToArray();
        lock (_sent)
        {
            _sent.Add((destination, copy));
        }

        Peer?.Receive(copy);
        return Task.CompletedTask;
    }

    public void Receive(byte[] message) => MessageReceived?.Invoke(message);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
