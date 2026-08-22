using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Devices;

namespace PdnQso.Tests.Rig;

/// <summary>
/// One shared medium: whichever station is transmitting, the other one waits, and - when the
/// medium is built to collide - hears nothing of what it talked over.
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
/// <para>
/// <b>Which is exactly the fault in issue #8</b>, so a medium that only ever queues cannot see
/// it. A half-duplex station hears nothing at all while it talks, and the receiver's Done goes
/// out at the instant the sender is transmitting the offer that ends its systematic pass: on
/// air both are lost, and on a queueing medium both are heard a moment late. Built with
/// <c>colliding: true</c> this medium keeps the queue, because two threads inside one channel
/// object is still a data race, and adds the part that matters:
/// </para>
/// <list type="bullet">
/// <item><description>a station is <b>keyed</b> from the moment it enters a send until its
/// burst has finished, which is what <see cref="IStation.Busy"/> answers at the far end: the
/// carrier a receiver's DCD would see;</description></item>
/// <item><description>a burst that begins with the far end already keyed is <b>talked
/// over</b>. It still costs its air, and neither station hears the other's frame: this one is
/// dropped on the way up, and the far end's own pending burst is marked so that it is dropped
/// too when its turn comes.</description></item>
/// </list>
/// <para>
/// <b>What it costs to model it this way.</b> Two bursts that overlap on air occupy about as
/// much of it as the longer of them; queued one behind the other here they cost the sum. A
/// collision is therefore a little dearer in this rig than on air, by about one frame time.
/// Everything this measures is intervals of several seconds, so that is noise, and it errs
/// towards making a collision look worse rather than better.
/// </para>
/// <para>
/// <b>Why the far end is let settle first.</b> A collision is two stations keying up at the
/// same instant of the protocol's time, and the two decisions to key up run on different
/// threads. Which of them the machine happens to schedule first is not a property of anything,
/// so before a burst begins this waits for the far end to either key up or have nothing in
/// hand, using the same <c>Busy</c> flags the clock's own settle loop is given (design.md 6d).
/// It costs nothing in the clock's time - the clock cannot move while either party has work in
/// hand - and it turns "whichever thread won" into a fact about the protocol.
/// </para>
/// </remarks>
internal sealed class HalfDuplexChannel : IDisposable
{
    private readonly SemaphoreSlim _medium = new(1, 1);
    private readonly End _a = new();
    private readonly End _b = new();
    private readonly bool _colliding;
    private int _wrapped;

    /// <summary>Builds a medium.</summary>
    /// <param name="colliding">Whether a station that keys up over another loses both frames,
    /// or merely waits its turn.</param>
    public HalfDuplexChannel(bool colliding = false) => _colliding = colliding;

    /// <summary>Whether two stations on air at once lose both frames.</summary>
    private bool Colliding => _colliding;

    /// <summary>Puts a station on this medium: the first is the A end, the second the B.</summary>
    /// <param name="station">The station to wrap.</param>
    /// <exception cref="InvalidOperationException">A third station. One frequency has two ends
    /// here; a rig with more stations on it needs a medium that models them.</exception>
    public IStation Wrap(IStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        int index = Interlocked.Increment(ref _wrapped) - 1;
        if (index > 1)
        {
            throw new InvalidOperationException(
                "this medium has two ends; a third station wants a medium that models one");
        }

        End me = index == 0 ? _a : _b;
        End other = index == 0 ? _b : _a;
        return new SharedMediumStation(station, this, me, other);
    }

    /// <summary>
    /// Tells the medium what has work in hand at each end, so that a burst does not begin
    /// while the far station is still deciding whether to answer.
    /// </summary>
    /// <param name="a">The A end's party: <c>FileSender.Busy</c> or the like.</param>
    /// <param name="b">The B end's party.</param>
    public void WorkInHand(Func<bool>? a, Func<bool>? b)
    {
        _a.WorkInHand = a;
        _b.WorkInHand = b;
    }

    /// <inheritdoc />
    public void Dispose() => _medium.Dispose();

    /// <summary>Takes the medium, transmits, and says whether anybody could hear it.</summary>
    private async Task TransmitAsync(End me, End other, Func<Task> send, CancellationToken cancellationToken)
    {
        if (!_colliding)
        {
            await _medium.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await send().ConfigureAwait(false);
            }
            finally
            {
                _medium.Release();
            }

            return;
        }

        // The carrier is up from here: a station that has decided to transmit has keyed the
        // radio, and the far end's DCD sees that whether or not this medium has let it start.
        me.Keyed = true;
        try
        {
            await _medium.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SettleAsync(other).ConfigureAwait(false);

                bool talkedOver = me.TakeTalkedOver();
                if (other.Keyed)
                {
                    // Both stations are on air at once. Neither hears the other: this burst is
                    // dropped below, and the far end's is marked so that its own turn drops it.
                    talkedOver = true;
                    other.TalkedOver = true;
                }

                other.Buffering = true;
                try
                {
                    await send().ConfigureAwait(false);
                }
                finally
                {
                    other.Release(heard: !talkedOver);
                }
            }
            finally
            {
                _medium.Release();
            }
        }
        finally
        {
            me.Keyed = false;
        }
    }

    /// <summary>
    /// Waits for the far end to finish deciding: it has either keyed up or has nothing in
    /// hand. Nothing moves on the clock while this runs, because nothing may.
    /// </summary>
    private static async Task SettleAsync(End other)
    {
        int spins = 0;
        while (other.WorkInHand?.Invoke() == true && !other.Keyed)
        {
            // The same ladder VirtualTime.WaitForAsync uses: yield while the far end's turn is
            // merely queued, then give the core back rather than spinning for it. Neither is a
            // deadline; a far end that never resolves hangs, and the runner says so.
            if (++spins < 1000)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }

    /// <summary>What one station's place on the medium looks like from the other side.</summary>
    private sealed class End
    {
        private readonly List<Action> _pending = [];
        private volatile bool _keyed;

        /// <summary>The party whose turn the medium must not run past; null for none.</summary>
        public Func<bool>? WorkInHand { get; set; }

        /// <summary>True from this station deciding to transmit until its burst is over.</summary>
        public bool Keyed
        {
            get => _keyed;
            set => _keyed = value;
        }

        /// <summary>This station's next burst was talked over and nobody will hear it.</summary>
        public bool TalkedOver { get; set; }

        /// <summary>
        /// True while a burst is crossing towards this station, so what its modem decodes is
        /// held rather than handed up: whether it was heard at all is not known until the
        /// burst is over.
        /// </summary>
        public bool Buffering { get; set; }

        /// <summary>Holds a decoded frame, or hands it up when nothing is in the air.</summary>
        public void Deliver(Action raise)
        {
            if (Buffering)
            {
                _pending.Add(raise);
                return;
            }

            raise();
        }

        /// <summary>Ends the burst: everything it delivered is handed up, or dropped.</summary>
        public void Release(bool heard)
        {
            Buffering = false;
            if (heard)
            {
                foreach (Action raise in _pending)
                {
                    raise();
                }
            }

            _pending.Clear();
        }

        /// <summary>Reads and clears the mark the far end left on this station's burst.</summary>
        public bool TakeTalkedOver()
        {
            bool was = TalkedOver;
            TalkedOver = false;
            return was;
        }
    }

    /// <summary>
    /// One station as everything above it sees it: the same station, on a medium it shares.
    /// </summary>
    /// <remarks>
    /// The frame events are re-raised rather than passed straight through, because on a
    /// colliding medium what the modem decoded and what the station heard are not the same
    /// thing: a frame that arrived while this station was talking is held until the burst is
    /// over and then dropped.
    /// </remarks>
    private sealed class SharedMediumStation : IStation
    {
        private readonly IStation _inner;
        private readonly HalfDuplexChannel _channel;
        private readonly End _me;
        private readonly End _other;

        public SharedMediumStation(IStation inner, HalfDuplexChannel channel, End me, End other)
        {
            _inner = inner;
            _channel = channel;
            _me = me;
            _other = other;
            inner.FrameReceived += OnInnerFrame;
            inner.RawFrameReceived += OnInnerRawFrame;
            inner.FrameTransmitted += (link, raw) => FrameTransmitted?.Invoke(link, raw);
        }

        public string Callsign => _inner.Callsign;

        public string Mode => _inner.Mode;

        public string DeviceName => _inner.DeviceName;

        public bool CanTransmit => _inner.CanTransmit;

        /// <summary>
        /// What this station's DCD says: on a colliding medium, that the other one is on air.
        /// On a queueing one there is nothing to hear, because nothing here ever collides.
        /// </summary>
        /// <remarks>
        /// A station with work in hand counts as on air as well as one actually inside a burst,
        /// and that is the faithful reading rather than a generous one. A sender pouring frames
        /// back to back drops its transmitter for the microseconds it takes to code the next
        /// one, and no DCD with any hang time at all - certainly not the modem's in-band energy
        /// detector - reports that as a quiet channel. Reading it literally would mean the
        /// opposite error, because this rig computes a burst in a few milliseconds of the
        /// machine's time however many seconds of air it is charged, so the gaps between the
        /// sender's bursts are the wrong length by three orders of magnitude in the direction
        /// that flatters a receiver looking for quiet.
        /// </remarks>
        public bool Busy => _channel.Colliding
            ? _other.Keyed || _other.WorkInHand?.Invoke() == true
            : _inner.Busy;

        public bool Transmitting => _inner.Transmitting;

        public IPowerControl Power => _inner.Power;

        public IModem Modem => _inner.Modem;

        public event Action<LinkFrame, FrameQuality>? FrameReceived;

        public event Action<byte[], FrameQuality>? RawFrameReceived;

        public event Action<LinkFrame?, byte[]>? FrameTransmitted;

        public void Start() => _inner.Start();

        public LinkFrame Frame(LinkFrameType type, byte session, ReadOnlySpan<byte> payload = default) =>
            _inner.Frame(type, session, payload);

        public Task SendAsync(LinkFrame frame, CancellationToken cancellationToken = default) =>
            _channel.TransmitAsync(
                _me, _other, () => _inner.SendAsync(frame, cancellationToken), cancellationToken);

        public Task SendRawAsync(
            ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default) =>
            _channel.TransmitAsync(
                _me, _other, () => _inner.SendRawAsync(ax25Frame, cancellationToken), cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private void OnInnerFrame(LinkFrame frame, FrameQuality quality) =>
            _me.Deliver(() => FrameReceived?.Invoke(frame, quality));

        private void OnInnerRawFrame(byte[] frame, FrameQuality quality) =>
            _me.Deliver(() => RawFrameReceived?.Invoke(frame, quality));
    }
}
