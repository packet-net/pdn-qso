using System.Runtime.InteropServices;
using PdnQso.Config;
using PdnQso.Link;

namespace PdnQso.Headless;

/// <summary>
/// The process around a <see cref="HeadlessSession"/>: check the config, bring the station up,
/// run, and take it down again whatever happened, PTT dropped and the radio left as found.
/// </summary>
public static class HeadlessHost
{
    /// <summary>Runs one headless command and returns the process exit code.</summary>
    /// <param name="config">The config, command-line overrides already applied.</param>
    /// <param name="command">What to do.</param>
    /// <param name="monitorOnly">Lock the transmitter out.</param>
    public static async Task<int> RunAsync(QsoConfig config, HeadlessCommand command, bool monitorOnly)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(command);

        IReadOnlyList<string> problems = config.Validate();
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
            {
                Console.Error.WriteLine($"pdn-qso: {problem}");
            }

            return 2;
        }

        using var stop = new CancellationTokenSource();
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
        }

        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

        await using var host = new StationHost(config, monitorOnly || command.Action == HeadlessAction.Listen);
        host.Log += Console.WriteLine;
        try
        {
            await host.StartAsync(stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"pdn-qso: station could not start - {e.Message}");
            return 1;
        }

        IStation station = host.Station
            ?? throw new InvalidOperationException("the station started and then was not there");
        return await new HeadlessSession(station, config, Console.Out).RunAsync(command, stop.Token).ConfigureAwait(false);
    }
}
