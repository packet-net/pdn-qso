using PdnQso.Config;
using PdnQso.Headless;
using PdnQso.Link;
using PdnQso.Link.Tait;
using PdnQso.Tests.Time;

namespace PdnQso.Tests.Headless;

/// <summary>
/// The headless runs, between two Tait stations whose air costs nothing: a responder at one
/// end and a measurement at the other, as a two-radio lab would run them over ssh.
/// </summary>
public class HeadlessSessionTests
{
    private static QsoConfig Config(string callsign) => new()
    {
        Device = "tait:/dev/ttyUSB0",
        Callsign = callsign,
        Mode = TaitModes.Sdm,
    };

    private static StationOptions Options(string callsign) => new() { Callsign = callsign };

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            (InstantTaitLink a, InstantTaitLink b) = InstantTaitLink.Pair();
            Asker = new TaitStation(Options("G0OLD-12"), a, timeProvider: Clock);
            Answerer = new TaitStation(Options("M0LTE-7"), b, timeProvider: Clock);
            Asker.Start();
            Answerer.Start();
            Responder = new HeadlessSession(Answerer, Config("M0LTE-7"), ResponderOutput, Clock);
        }

        public VirtualClock Clock { get; } = new();

        public TaitStation Asker { get; }

        public TaitStation Answerer { get; }

        public StringWriter ResponderOutput { get; } = new();

        public StringWriter AskerOutput { get; } = new();

        public HeadlessSession Responder { get; }

        public CancellationTokenSource StopResponder { get; } = new();

        public async Task<Task<int>> StartResponderAsync()
        {
            Task<int> responding = Responder.RunAsync(new HeadlessCommand(HeadlessAction.Respond), StopResponder.Token);
            await VirtualTime.WaitForAsync(() => responding.IsCompleted || Responder.Ready);
            return responding;
        }

        public HeadlessSession Asking() => new(Asker, Config("G0OLD-12"), AskerOutput, Clock);

        public async ValueTask DisposeAsync()
        {
            StopResponder.Dispose();
            await Asker.DisposeAsync();
            await Answerer.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_Ping_Run_Against_A_Responder_Is_All_Answered_And_Exits_Zero()
    {
        await using var rig = new Rig();
        Task<int> responding = await rig.StartResponderAsync();

        int code = await rig.Asking().RunAsync(new HeadlessCommand(HeadlessAction.Ping) { Count = 3 }, CancellationToken.None);

        code.Should().Be(0);
        rig.AskerOutput.ToString().Should().Contain("sent=3 heard=3");
        await rig.StopResponder.CancelAsync();
        (await responding).Should().Be(0, "stopping a responder is how it is meant to end");
    }

    [Fact]
    public async Task A_Chat_Line_Is_Acknowledged_And_Shown_At_The_Far_End_With_Both_Ssids()
    {
        await using var rig = new Rig();
        Task<int> responding = await rig.StartResponderAsync();

        int code = await rig.Asking().RunAsync(
            new HeadlessCommand(HeadlessAction.Chat) { Text = "hello from the lab" }, CancellationToken.None);

        code.Should().Be(0);
        rig.AskerOutput.ToString().Should().Contain("chat: delivered");
        await VirtualTime.WaitForAsync(() => rig.ResponderOutput.ToString().Contains("chat from"));
        rig.ResponderOutput.ToString().Should().Contain("chat from G0OLD-12: hello from the lab");
        rig.ResponderOutput.ToString().Should().Contain("responding as M0LTE-7");
        await rig.StopResponder.CancelAsync();
        await responding;
    }

    [Fact]
    public async Task A_Stream_Run_Is_Counted_At_The_Far_End_And_Reported_Back()
    {
        await using var rig = new Rig();
        Task<int> responding = await rig.StartResponderAsync();

        int code = await rig.Asking().RunAsync(
            new HeadlessCommand(HeadlessAction.Stream) { Count = 5 }, CancellationToken.None);

        code.Should().Be(0);
        rig.AskerOutput.ToString().Should().Contain("sent=5 heard=5");
        await rig.StopResponder.CancelAsync();
        await responding;
    }

    [Fact]
    public async Task A_Ping_With_Nobody_Answering_Exits_One()
    {
        await using var rig = new Rig();

        Task<int> run = rig.Asking().RunAsync(new HeadlessCommand(HeadlessAction.Ping) { Count = 2 }, CancellationToken.None);

        (await VirtualTime.RunAsync(rig.Clock, run)).Should().Be(1);
        rig.AskerOutput.ToString().Should().Contain("lost=2");
    }

    [Fact]
    public async Task A_Listener_Prints_What_It_Hears_And_Stops_When_Its_Time_Is_Up()
    {
        await using var rig = new Rig();
        var listener = new HeadlessSession(rig.Answerer, Config("M0LTE-7"), rig.ResponderOutput, rig.Clock);
        Task<int> listening = listener.RunAsync(
            new HeadlessCommand(HeadlessAction.Listen) { RunFor = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        await VirtualTime.WaitForAsync(() => rig.ResponderOutput.ToString().Contains("listening"));

        await rig.Asker.SendAsync(rig.Asker.Frame(LinkFrameType.Chat, 1, "\u000159 in reading"u8));

        rig.ResponderOutput.ToString().Should().Contain("G0OLD-12");
        (await VirtualTime.RunAsync(rig.Clock, listening)).Should().Be(0);
        rig.Clock.Elapsed.Should().Be(TimeSpan.FromSeconds(30));
    }
}
