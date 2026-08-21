using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Transfer;
using PdnQso.Tests.Time;

namespace PdnQso.Tests.Rig;

/// <summary>
/// Two stations, one shared medium, one clock, and a link that charges for its own air time.
/// </summary>
/// <remarks>
/// <para>
/// The mode is <c>afsk1200-il2p</c>: an ordinary IL2P+CRC packet mode, and one of the cheapest
/// in the catalogue to simulate at about five milliseconds of CPU per frame, so a whole
/// transfer runs in the time one 300 baud frame would take to modulate. Nothing measured
/// through it is a statement about a modem; the claims are all about the protocol.
/// </para>
/// <para>
/// Transmitting costs what it costs: the clock moves by each burst's own air time. Without that
/// a sender's patience is unreachable, because pouring symbols on this rig is free and no
/// amount of it brings a timeout measured in seconds any closer.
/// </para>
/// </remarks>
internal sealed class TransferRig : IAsyncDisposable
{
    /// <summary>The mode both stations run.</summary>
    public const string Mode = "afsk1200-il2p";

    private AudioLink _link = null!;
    private Station _a = null!;
    private Station _b = null!;
    private HalfDuplexChannel _medium = null!;

    /// <summary>The clock both stations run on.</summary>
    public VirtualClock Clock { get; } = new();

    /// <summary>True while a burst is in the air.</summary>
    public bool Carrying => _link.Carrying;

    /// <summary>A number that changes whenever a burst crosses.</summary>
    public long Crossings => _link.Crossings;

    /// <summary>The transmitting station, on the shared medium.</summary>
    public IStation A { get; private set; } = null!;

    /// <summary>The receiving station, on the shared medium.</summary>
    public IStation B { get; private set; } = null!;

    /// <summary>Builds the rig over one channel.</summary>
    /// <param name="channel">What happens to a burst between the two stations.</param>
    public static TransferRig Build(AudioChannel channel)
    {
        var rig = new TransferRig();
        var link = AudioLink.Create(Mode, channel);
        var medium = new HalfDuplexChannel();
        var a = new Station(
            new StationOptions { Callsign = "M0LTE-7", TxDelayMilliseconds = 100 },
            link.DeviceA, link.ModemA, OpenBusyGate.Instance, timeProvider: rig.Clock);
        var b = new Station(
            new StationOptions { Callsign = "G0OLD-3", TxDelayMilliseconds = 100 },
            link.DeviceB, link.ModemB, OpenBusyGate.Instance, timeProvider: rig.Clock);
        link.Carried += rig.Clock.Advance;

        a.Start();
        b.Start();
        rig._link = link;
        rig._a = a;
        rig._b = b;
        rig._medium = medium;
        rig.A = medium.Wrap(a);
        rig.B = medium.Wrap(b);
        return rig;
    }

    /// <summary>
    /// Lets the clock run until a transfer finishes, moving it on only while nothing is
    /// happening.
    /// </summary>
    /// <param name="work">The transfer under test.</param>
    /// <param name="answering">Whatever owes the other end an answer: a receiver has heard
    /// frames it has not acted on, and the clock must not be run past that or the sender
    /// times out against a status that was already on its way.</param>
    /// <param name="sending">The sender, when the test holds one. A sender has work in hand
    /// for everything except its listening gap, and running the clock past that gives the
    /// receiver's patience a head start on a station that was about to transmit.</param>
    /// <param name="alsoBusy">Anything else that owes an answer.</param>
    /// <param name="budget">How much of the clock's time to allow.</param>
    public Task<FileTransferResult> RunAsync(
        Task<FileTransferResult> work,
        FileReceiver? answering = null,
        Func<bool>? alsoBusy = null,
        TimeSpan? budget = null,
        FileSender? sending = null) =>
        VirtualTime.RunAsync(
            Clock,
            work,
            () => Carrying || answering?.Busy == true || sending?.Busy == true
                || alsoBusy?.Invoke() == true,
            budget ?? TimeSpan.FromMinutes(5),
            progress: () => Crossings);

    /// <summary>
    /// Lets the clock run until <paramref name="done"/> is true or the budget is spent, and
    /// says which. For a caller that has to cope with the thing never happening.
    /// </summary>
    /// <param name="done">The thing being waited for.</param>
    /// <param name="answering">Whatever owes the other end an answer.</param>
    /// <param name="budget">How much of the clock's time to allow.</param>
    /// <param name="sending">The sender, when the test holds one.</param>
    public Task<bool> SettleAsync(
        Func<bool> done,
        FileReceiver? answering = null,
        TimeSpan? budget = null,
        FileSender? sending = null) =>
        VirtualTime.SettleAsync(
            Clock,
            done,
            () => Carrying || answering?.Busy == true || sending?.Busy == true,
            budget ?? TimeSpan.FromMinutes(5),
            progress: () => Crossings);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        _medium.Dispose();
        _link.Dispose();
    }
}
