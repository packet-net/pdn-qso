using Packet.SoundModem.Modems;
using PdnQso.Link.Devices;

namespace PdnQso.Link.Audio;

/// <summary>Which end of an <see cref="AudioLink"/> is meant.</summary>
public enum LinkEnd
{
    /// <summary>The first station.</summary>
    A,

    /// <summary>The second station.</summary>
    B,
}

/// <summary>A frame one end decoded, with what the decode established about it.</summary>
/// <param name="Frame">The AX.25 frame, as <c>IModem.FrameDecoded</c> delivered it.</param>
/// <param name="Quality">SNR, corrections, CRC state and the rest.</param>
public readonly record struct ReceivedFrame(byte[] Frame, Packet.SoundModem.Modems.FrameQuality Quality)
{
    /// <summary>The link frame this is, or <see langword="null"/> when it is somebody else's.</summary>
    public LinkFrame? AsLinkFrame =>
        LinkFrame.TryDecode(Frame, out LinkFrame? link) ? link : null;
}

/// <summary>
/// The hermetic two-station rig of design.md section 1: two modems of the same mode, joined so
/// that what one modulates the other processes, through an <see cref="AudioChannel"/>.
/// </summary>
/// <remarks>
/// <para>
/// There are two ways to drive it, and a test picks one:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="RunBurst"/> is the modem-level way: hand it a frame, get back
/// what the far modem decoded. It pumps the modems itself.</description></item>
/// <item><description><see cref="DeviceA"/> and <see cref="DeviceB"/> are the station-level
/// way: build a <see cref="Station"/> over each, with <see cref="ModemA"/> and
/// <see cref="ModemB"/>, and the stations pump their own modems as they will over a real
/// device.</description></item>
/// </list>
/// <para>
/// The two compose rather than conflict: an end feeds its own modem only while nothing is
/// subscribed to its <see cref="IAudioDevice.SamplesReceived"/>, so a bare link decodes for
/// itself and an end with a <see cref="Station"/> attached leaves the pumping to the station.
/// <see cref="RunBurst"/> reads what was decoded off the modem's own event either way.
/// </para>
/// <para>
/// Everything here is synchronous. A burst crosses the channel and is demodulated on the
/// calling thread inside <see cref="IAudioDevice.TransmitAsync"/>, which is what makes a
/// two-station test deterministic and repeatable rather than a sleep with a prayer attached.
/// The price is that the far station's frame handler runs while the near one is still inside
/// its send, so a handler that transmits straight back re-enters the link; phase B's ARQ
/// posts its replies rather than sending them from the handler for that reason.
/// </para>
/// </remarks>
public sealed class AudioLink : IDisposable
{
    private readonly Endpoint _a;
    private readonly Endpoint _b;
    private readonly Random _noise;
    private bool _disposed;

    private AudioLink(string mode, int sampleRate, AudioChannel channel, ModemOptions options)
    {
        Mode = mode;
        SampleRate = sampleRate;
        Channel = channel;
        _noise = new Random(channel.Seed);

        // The catalogue's frame sink is not the monitor path: a plain-IL2P frame an IL2P+CRC
        // link was not told to accept never reaches it. FrameDecoded gets everything the
        // station heard, which is what a test - and Monitor - wants, so the sink is a no-op
        // and every reading comes off the event.
        _a = new Endpoint(this, LinkEnd.A, ModemCatalog.Create(mode, sampleRate, _ => { }, options));
        _b = new Endpoint(this, LinkEnd.B, ModemCatalog.Create(mode, sampleRate, _ => { }, options));
    }

    /// <summary>Builds a link with a modem of <paramref name="mode"/> at each end.</summary>
    /// <param name="mode">A mode string from <c>ModemCatalog.KnownModes</c>.</param>
    /// <param name="channel">The channel between them; <see cref="AudioChannel.Clean"/> when
    /// omitted.</param>
    /// <param name="options">Per-mode modem knobs, applied to both ends alike.</param>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not a known mode, or
    /// <paramref name="options"/> is not valid for it.</exception>
    public static AudioLink Create(string mode, AudioChannel? channel = null, ModemOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return new AudioLink(mode, ModemCatalog.DspRateFor(mode), channel ?? AudioChannel.Clean, options);
    }

    /// <summary>The mode both ends run.</summary>
    public string Mode { get; }

    /// <summary>The DSP rate both ends run at, from <c>ModemCatalog.DspRateFor</c>.</summary>
    public int SampleRate { get; }

    /// <summary>The channel between the two ends.</summary>
    public AudioChannel Channel { get; set; }

    /// <summary>The A end's modem.</summary>
    public IModem ModemA => _a.Modem;

    /// <summary>The B end's modem.</summary>
    public IModem ModemB => _b.Modem;

    /// <summary>The A end as a station's audio device.</summary>
    public IAudioDevice DeviceA => _a;

    /// <summary>The B end as a station's audio device.</summary>
    public IAudioDevice DeviceB => _b;

    /// <summary>How many samples have crossed the channel, in total.</summary>
    public long SamplesCarried { get; private set; }

    /// <summary>
    /// Modulates one frame at <paramref name="from"/>, puts it through the channel, and
    /// returns what the other end decoded.
    /// </summary>
    /// <param name="ax25Frame">The frame to send, as <c>IModem.Modulate</c> takes it.</param>
    /// <param name="from">Which end transmits; the other receives.</param>
    /// <param name="txDelayMilliseconds">TXDELAY, expressed in the modulated audio.</param>
    /// <returns>Every frame the far modem reported, in order. Usually one; empty when the
    /// channel ate it.</returns>
    public IReadOnlyList<ReceivedFrame> RunBurst(
        ReadOnlySpan<byte> ax25Frame,
        LinkEnd from = LinkEnd.A,
        int txDelayMilliseconds = 300)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Endpoint source = from == LinkEnd.A ? _a : _b;
        Endpoint target = from == LinkEnd.A ? _b : _a;

        float[] audio = source.Modem.Modulate(ax25Frame, txDelayMilliseconds);
        var heard = new List<ReceivedFrame>();
        void OnDecoded(byte[] frame, Packet.SoundModem.Modems.FrameQuality quality) =>
            heard.Add(new ReceivedFrame(frame, quality));

        target.Modem.FrameDecoded += OnDecoded;
        try
        {
            Carry(audio, target);
        }
        finally
        {
            target.Modem.FrameDecoded -= OnDecoded;
        }

        return heard;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_a.Modem as IDisposable)?.Dispose();
        (_b.Modem as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Puts a burst through the channel and feeds it to <paramref name="target"/> in blocks,
    /// as a real capture device would, so nothing can pass here that would fail on a stream.
    /// </summary>
    private void Carry(ReadOnlySpan<float> audio, Endpoint target)
    {
        float[] through = Channel.Apply(audio, SampleRate, _noise);
        SamplesCarried += through.Length;

        const int Block = 1024;
        for (int offset = 0; offset < through.Length; offset += Block)
        {
            int length = Math.Min(Block, through.Length - offset);
            target.Deliver(through, offset, length);
        }
    }

    /// <summary>One end of the link: a modem, and the audio device a station sees.</summary>
    private sealed class Endpoint(AudioLink link, LinkEnd end, IModem modem) : IAudioDevice
    {
        private bool _ptt;

        public IModem Modem { get; } = modem;

        public string Name { get; } = $"audiolink:{end}";

        public int SampleRate => link.SampleRate;

        public bool CanTransmit => true;

        public bool Ptt => _ptt;

        public IPowerControl Power => NoPowerControl.Instance;

        public event Action<float[]>? SamplesReceived;

        public event Action<bool>? PttChanged;

        public void Start()
        {
        }

        public Task TransmitAsync(
            ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetPtt(true);
            try
            {
                link.Carry(samples.Span, end == LinkEnd.A ? link._b : link._a);
            }
            finally
            {
                SetPtt(false);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        /// <summary>Hands one block to whatever this end is listening with.</summary>
        internal void Deliver(float[] samples, int offset, int length)
        {
            // Nothing listening means no station has attached, so this end feeds its own modem -
            // which is what lets RunBurst work on a bare link. A station that has attached
            // pumps the modem itself, exactly as it will over a real capture device.
            Action<float[]>? handler = SamplesReceived;
            if (handler is null)
            {
                Modem.Process(samples.AsSpan(offset, length));
                return;
            }

            handler(samples.AsSpan(offset, length).ToArray());
        }

        private void SetPtt(bool value)
        {
            if (_ptt == value)
            {
                return;
            }

            _ptt = value;
            PttChanged?.Invoke(value);
        }
    }
}
