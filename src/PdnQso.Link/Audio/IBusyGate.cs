using Packet.SoundModem.Modems;

namespace PdnQso.Link.Audio;

/// <summary>
/// "Is somebody else using the channel?" - the question a station has to answer before it
/// keys up.
/// </summary>
public interface IBusyGate
{
    /// <summary>True while the channel is in use and this station should hold off.</summary>
    bool Busy { get; }

    /// <summary>Forgets what it has heard; called across our own transmission.</summary>
    void Reset();
}

/// <summary>
/// The library's own answer: the modem's data carrier detect, or any in-band energy at all.
/// </summary>
/// <remarks>
/// <c>IModem.CarrierDetect</c> is a coherent packet signal and <c>IModem.ChannelBusy</c> is
/// energy of any kind in the modem's band, which is the pair the daemon's CSMA uses. Either
/// one is a reason not to transmit: the first is a frame we would collide with, the second is
/// a voice QSO or a carrier we would sit on top of.
/// </remarks>
/// <param name="modem">The modem to ask.</param>
public sealed class ModemBusyGate(IModem modem) : IBusyGate
{
    private readonly IModem _modem = modem ?? throw new ArgumentNullException(nameof(modem));

    /// <inheritdoc />
    public bool Busy => _modem.CarrierDetect || _modem.ChannelBusy;

    /// <inheritdoc />
    public void Reset() => _modem.ResetCarrierState();
}

/// <summary>
/// A plain energy gate over the library's <see cref="EnergyBusyDetector"/>, for a device whose
/// modem has no busy opinion worth having. Feed it the same samples the modem gets.
/// </summary>
/// <remarks>
/// The detector wants band-limited samples; handed wideband ones it measures the whole audio
/// passband, which is a coarser answer than the modem's and is why
/// <see cref="ModemBusyGate"/> is the default.
/// </remarks>
public sealed class EnergyBusyGate : IBusyGate
{
    private readonly EnergyBusyDetector _detector;

    /// <summary>Builds a gate over a detector the caller has configured.</summary>
    /// <param name="detector">The library's detector, already sized for the sample rate.</param>
    public EnergyBusyGate(EnergyBusyDetector detector) =>
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));

    /// <inheritdoc />
    public bool Busy => _detector.Busy;

    /// <summary>Feeds received audio; call from the same place the modem is fed.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _detector.Process(sample);
        }
    }

    /// <inheritdoc />
    public void Reset() => _detector.Reset();
}

/// <summary>
/// A gate a test drives by hand, for pinning that transmit really does wait for the channel.
/// </summary>
public sealed class ManualBusyGate : IBusyGate
{
    private int _queries;

    /// <summary>Whether the channel is busy. Set it from the test.</summary>
    public bool Held { get; set; }

    /// <inheritdoc />
    public bool Busy
    {
        get
        {
            Interlocked.Increment(ref _queries);
            return Held;
        }
    }

    /// <summary>
    /// How many times anything has asked whether the channel is busy - so a test can wait for
    /// the station to have actually reached its wait loop rather than sleeping and hoping.
    /// </summary>
    public int Queries => Volatile.Read(ref _queries);

    /// <summary>How many times <see cref="Reset"/> has been called.</summary>
    public int Resets { get; private set; }

    /// <inheritdoc />
    public void Reset() => Resets++;
}

/// <summary>A gate that never holds anything up: a point-to-point link with nobody else on it.</summary>
public sealed class OpenBusyGate : IBusyGate
{
    /// <summary>The one instance; it has no state.</summary>
    public static OpenBusyGate Instance { get; } = new();

    /// <inheritdoc />
    public bool Busy => false;

    /// <inheritdoc />
    public void Reset()
    {
    }
}
