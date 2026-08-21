using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Chat;

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
    private ChatRig(AudioLink link, Station stationA, Station stationB, ChatSession a, ChatSession b)
    {
        Link = link;
        StationA = stationA;
        StationB = stationB;
        A = a;
        B = b;
    }

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
        AudioLink link = AudioLink.Create(mode, Clean);
        var stationA = new Station(Options("M0LTE-7"), link.DeviceA, link.ModemA, gateA ?? OpenBusyGate.Instance);
        var stationB = new Station(Options("G0OLD-1"), link.DeviceB, link.ModemB, OpenBusyGate.Instance);
        stationA.Start();
        stationB.Start();

        ChatOptions a = (optionsA ?? new ChatOptions()) with { SessionId = 0x2A };
        ChatOptions b = (optionsB ?? optionsA ?? new ChatOptions()) with { SessionId = 0x5B };
        return new ChatRig(
            link,
            stationA,
            stationB,
            new ChatSession(stationA, a, random: new Random(11)),
            new ChatSession(stationB, b, random: new Random(23)));
    }

    /// <summary>
    /// Waits, in real time, for something a far end does on its own thread. Everything here is
    /// driven by real modems on real threads, so a test that needs the acknowledgement pump to
    /// have got somewhere waits for the fact rather than for a duration.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan patience)
    {
        DateTime deadline = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

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
