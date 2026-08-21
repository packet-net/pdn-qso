using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Chat;
using PdnQso.Tests.Rig;
using PdnQso.Tests.Time;

namespace PdnQso.Tests.Chat;

/// <summary>
/// Two stations with a chat session each, joined by an <see cref="AudioLink"/>: real modems,
/// real frames, and a channel the test can break and mend between lines.
/// </summary>
/// <remarks>
/// The sessions are not started by the constructor, so a test can subscribe to the stations'
/// own events before the sessions subscribe to them. That ordering matters for the lost
/// acknowledgement test, which has to break the channel after the far station has decoded a
/// line and before its session has queued the answer.
/// </remarks>
internal sealed class ChatRig : IAsyncDisposable
{
    private ChatRig(
        VirtualClock clock, AudioLink link, HalfDuplexChannel medium,
        Station stationA, Station stationB, ChatSession a, ChatSession b)
    {
        Clock = clock;
        _medium = medium;
        Link = link;
        StationA = stationA;
        StationB = stationB;
        A = a;
        B = b;
    }

    private readonly HalfDuplexChannel _medium;

    /// <summary>The clock both ends run on. Nothing here reads the wall clock.</summary>
    public VirtualClock Clock { get; }

    /// <summary>A channel that carries nothing at all: the band has gone out.</summary>
    public static AudioChannel Dead { get; } = new()
    {
        Dropouts = [new SampleRange(0, int.MaxValue)],
        TailSamples = 2400,
    };

    /// <summary>A channel that carries everything, with the shortest tail that still flushes.</summary>
    public static AudioChannel Clean { get; } = new() { TailSamples = 2400 };

    public AudioLink Link { get; }

    public Station StationA { get; }

    public Station StationB { get; }

    public ChatSession A { get; }

    public ChatSession B { get; }

    /// <summary>Builds the rig. Call <see cref="StartAll"/> once the events are wired.</summary>
    /// <param name="mode">The mode both ends run.</param>
    /// <param name="optionsA">The A end's chat options; the defaults when omitted.</param>
    /// <param name="optionsB">The B end's; A's when omitted.</param>
    /// <param name="gateA">The A end's busy gate, for pinning the backoff.</param>
    public static ChatRig Create(
        string mode,
        ChatOptions? optionsA = null,
        ChatOptions? optionsB = null,
        IBusyGate? gateA = null)
    {
        var clock = new VirtualClock();
        AudioLink link = AudioLink.Create(mode, Clean);

        // One medium, as the transfer and perf rigs have. Without it both stations can be
        // inside the same channel object at once, which is a data race and not a collision,
        // and an acknowledgement lost to one costs the sender a retry it should not have had.
        var medium = new HalfDuplexChannel();
        var stationA = new Station(
            Options("M0LTE-7"), link.DeviceA, link.ModemA, gateA ?? OpenBusyGate.Instance,
            timeProvider: clock);
        var stationB = new Station(
            Options("G0OLD-1"), link.DeviceB, link.ModemB, OpenBusyGate.Instance,
            timeProvider: clock);
        stationA.Start();
        stationB.Start();

        ChatOptions a = (optionsA ?? new ChatOptions()) with { SessionId = 0x2A };
        ChatOptions b = (optionsB ?? optionsA ?? new ChatOptions()) with { SessionId = 0x5B };
        IStation onAir = medium.Wrap(stationA);
        IStation farEnd = medium.Wrap(stationB);
        return new ChatRig(
            clock,
            link,
            medium,
            stationA,
            stationB,
            new ChatSession(onAir, a, timeProvider: clock, random: new Random(11)),
            new ChatSession(farEnd, b, timeProvider: clock, random: new Random(23)));
    }

    /// <summary>
    /// True while either end has work in hand that the clock must not be run past: a burst in
    /// the air, or a session that owes the other one an answer.
    /// </summary>
    /// <remarks>
    /// Both parts have to be true from the instant the work is taken on, not from when the
    /// pump that does it wakes up. An acknowledgement is queued inside the far station's own
    /// decode, which happens inside the sender's transmit, so there is no moment at which a
    /// line has been heard and nothing says an answer is owed.
    /// </remarks>
    public bool Busy => Link.Carrying || A.Sending || B.Sending;

    /// <summary>A number that changes whenever the rig does anything.</summary>
    public long Progress => Link.Crossings;

    /// <summary>
    /// Waits for something a far end does on its own thread, for as long as it takes.
    /// </summary>
    /// <remarks>
    /// No deadline, deliberately: a deadline is a wall-clock measurement, and a wall-clock
    /// measurement is what lets a busy machine turn a passing claim into a failing one. This
    /// waits for the fact.
    /// </remarks>
    public static Task WaitUntilAsync(Func<bool> condition) =>
        VirtualTime.WaitForAsync(condition);

    /// <summary>
    /// Lets the clock run, only while nothing is happening, until something is true. This is
    /// how a test gets a protocol timeout to fire without waiting for one in real life.
    /// </summary>
    public Task RunUntilAsync(Func<bool> condition, string what) =>
        VirtualTime.UntilAsync(Clock, condition, what, () => Busy, progress: () => Progress);

    /// <summary>Lets the clock run until a send finishes, and returns what it decided.</summary>
    public Task<ChatDelivery> RunAsync(Task<ChatDelivery> send) =>
        VirtualTime.RunAsync(Clock, send, () => Busy, progress: () => Progress);

    /// <summary>Starts both conversations, which is where the two hellos go out.</summary>
    public void StartAll()
    {
        A.Start();
        B.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await A.DisposeAsync();
        await B.DisposeAsync();
        await StationA.DisposeAsync();
        await StationB.DisposeAsync();
        _medium.Dispose();
        Link.Dispose();
    }

    private static StationOptions Options(string callsign) => new()
    {
        Callsign = callsign,
        TxDelayMilliseconds = 100,
        BusyPollInterval = TimeSpan.FromMilliseconds(5),
        BusyWaitTimeout = TimeSpan.FromSeconds(30),
    };
}
