using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Devices;

namespace PdnQso.Tests.Rig;

/// <summary>
/// One shared medium: whichever station is transmitting, the other one waits.
/// </summary>
/// <remarks>
/// <para>
/// The hermetic <c>AudioLink</c> is a pair of modems joined by a channel, and a burst crosses
/// it synchronously on the transmitting thread. That makes a one-way test deterministic, and
/// it makes a two-way one a race: two stations that key up at the same moment would be inside
/// the same channel object at the same moment, which is not a collision, it is a data race.
/// </para>
/// <para>
/// A real pair of stations on one frequency cannot do that, so this puts the constraint back:
/// a station wraps its <see cref="IStation"/> in <see cref="Wrap"/>, and transmitting takes
/// the channel. Everything else - the events, the callsign, the frame builder - passes
/// straight through. It is a test rig for the half-duplex world, not a model of one; a station
/// that has to wait here waits, where on air it would have collided.
/// </para>
/// </remarks>
internal sealed class HalfDuplexChannel : IDisposable
{
    private readonly SemaphoreSlim _medium = new(1, 1);

    /// <summary>Puts a station on this medium.</summary>
    public IStation Wrap(IStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return new SharedMediumStation(station, this);
    }

    /// <inheritdoc />
    public void Dispose() => _medium.Dispose();

    private async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _medium.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(_medium);
    }

    private sealed class Release(SemaphoreSlim medium) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                medium.Release();
            }
        }
    }

    private sealed class SharedMediumStation(IStation inner, HalfDuplexChannel channel) : IStation
    {
        public string Callsign => inner.Callsign;

        public string Mode => inner.Mode;

        public string DeviceName => inner.DeviceName;

        public bool CanTransmit => inner.CanTransmit;

        public bool Busy => inner.Busy;

        public bool Transmitting => inner.Transmitting;

        public IPowerControl Power => inner.Power;

        public IModem Modem => inner.Modem;

        public event Action<LinkFrame, FrameQuality>? FrameReceived
        {
            add => inner.FrameReceived += value;
            remove => inner.FrameReceived -= value;
        }

        public event Action<byte[], FrameQuality>? RawFrameReceived
        {
            add => inner.RawFrameReceived += value;
            remove => inner.RawFrameReceived -= value;
        }

        public event Action<LinkFrame?, byte[]>? FrameTransmitted
        {
            add => inner.FrameTransmitted += value;
            remove => inner.FrameTransmitted -= value;
        }

        public void Start() => inner.Start();

        public LinkFrame Frame(LinkFrameType type, byte session, ReadOnlySpan<byte> payload = default) =>
            inner.Frame(type, session, payload);

        public async Task SendAsync(LinkFrame frame, CancellationToken cancellationToken = default)
        {
            using IDisposable held = await channel.AcquireAsync(cancellationToken).ConfigureAwait(false);
            await inner.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendRawAsync(
            ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default)
        {
            using IDisposable held = await channel.AcquireAsync(cancellationToken).ConfigureAwait(false);
            await inner.SendRawAsync(ax25Frame, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
