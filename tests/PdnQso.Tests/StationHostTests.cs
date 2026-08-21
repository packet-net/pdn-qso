using PdnQso;
using PdnQso.Config;
using PdnQso.Link;

namespace PdnQso.Tests;

/// <summary>
/// The lifetime the UI hangs off: build a station from a config, replace it when the config
/// changes, and take everything down at the end.
/// </summary>
/// <remarks>
/// Run over a pipe pair, which is the one device that opens on a machine with no radio. What
/// is being pinned is the host's own behaviour - that a config which will not start a station
/// is refused before anything is opened, that applying a new one really does build a new
/// station, and that a monitor-only session cannot transmit however hard anything above it
/// tries.
/// </remarks>
public class StationHostTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"pdn-qso-host-{Guid.NewGuid():N}");

    private QsoConfig Config(string mode = "bpsk300") => new()
    {
        Device = $"pipe:{Path.Combine(_directory, "in")},{Path.Combine(_directory, "out")},12000",
        Callsign = "M0LTE-7",
        Mode = mode,
        AudioCentreHz = 1500,
        FrameLogPath = "",
        IdentEnabled = false,
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Starting_Brings_Up_A_Station_On_The_Configured_Device()
    {
        await using var host = new StationHost(Config());

        await host.StartAsync();

        host.Station.Should().NotBeNull();
        host.Station!.Callsign.Should().Be("M0LTE-7");
        host.Station.CanTransmit.Should().BeTrue();
        host.Station.DeviceName.Should().StartWith("pipe:");
    }

    [Fact]
    public async Task Applying_A_New_Mode_Replaces_The_Station_And_Says_So()
    {
        await using var host = new StationHost(Config());
        var replaced = new List<IStation>();
        host.StationChanged += replaced.Add;

        await host.StartAsync();
        IStation first = host.Station!;
        await host.ApplyAsync(Config("afsk1200"));

        host.Station.Should().NotBeSameAs(first, "a new mode is a new modem over a reopened device");
        host.Config.Mode.Should().Be("afsk1200");
        replaced.Should().HaveCount(2, "every activity is re-attached each time");
    }

    [Fact]
    public async Task A_Config_That_Will_Not_Start_A_Station_Is_Refused_Before_Anything_Is_Opened()
    {
        await using var host = new StationHost(Config());

        Func<Task> apply = async () => await host.ApplyAsync(Config() with { Callsign = "" });

        await apply.Should().ThrowAsync<ArgumentException>().WithMessage("*Callsign*");
        host.Station.Should().BeNull("nothing was opened, so nothing is running");
    }

    [Fact]
    public async Task A_Monitor_Only_Session_Refuses_To_Transmit()
    {
        await using var host = new StationHost(Config(), monitorOnly: true);

        await host.StartAsync();

        host.Station!.CanTransmit.Should().BeFalse();
        Func<Task> send = async () =>
            await host.Station.SendAsync(host.Station.Frame(LinkFrameType.Hello, 1));
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*receive-only*");
    }

    [Fact]
    public async Task Disposing_Takes_The_Station_Down()
    {
        var host = new StationHost(Config());
        await host.StartAsync();

        await host.DisposeAsync();

        host.Station.Should().BeNull();
    }

    [Fact]
    public async Task The_Log_Says_What_Came_Up()
    {
        await using var host = new StationHost(Config());
        var lines = new List<string>();
        host.Log += lines.Add;

        await host.StartAsync();

        lines.Should().Contain(l => l.Contains("station:", StringComparison.Ordinal)
                                    && l.Contains("bpsk300", StringComparison.Ordinal));
    }
}
