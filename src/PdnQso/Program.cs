// pdn-qso - a terminal tool for interactive two-way testing over the pdn-soundmodem modems.
//
// This file is the wiring and nothing else: read the command line, find or build a config,
// bring a station up over it, and put the screen on top. The protocol is in PdnQso.Link where
// it is tested without a terminal; the layout is in PdnQso.Ui; the parts of this program worth
// testing (the config, the command line, the two line formatters) are pure and are.
using System.Reflection;
using System.Runtime.InteropServices;
using PdnQso;
using PdnQso.Config;
using PdnQso.Ui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

string version =
    Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0";

// The '+' and everything after it is the source-revision suffix the SDK appends; somebody
// reading their version out on the air does not want to read a git hash as well.
int plus = version.IndexOf('+', StringComparison.Ordinal);
if (plus >= 0)
{
    version = version[..plus];
}

CommandLine command = CommandLine.Parse(args);

if (command.Error is string error)
{
    Console.Error.WriteLine($"pdn-qso: {error}");
    return 2;
}

if (command.ShowVersion)
{
    Console.WriteLine($"pdn-qso {version}");
    return 0;
}

if (command.ShowHelp)
{
    Console.WriteLine(CommandLine.HelpText(version));
    return 0;
}

string configPath = command.ResolvedConfigPath;
QsoConfig? onDisk;
try
{
    onDisk = QsoConfig.Load(configPath);
}
catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
{
    // Refusing to start beats writing a fresh default over somebody's hand-edited file.
    Console.Error.WriteLine($"pdn-qso: {e.Message}");
    return 2;
}

// Ctrl+Q, not Terminal.Gui's default of Esc: Esc is the key an operator hits to back out of a
// dialog, and losing the QSO to it would be a poor joke. Set before Init so the binding is in
// place for the first screen drawn.
Application.SetDefaultKeyBinding(Command.Quit, new PlatformKeyBinding { All = [Key.Q.WithCtrl] });

using IApplication app = Application.Create();
try
{
    app.Init();
}
catch (Exception e) when (e is not OutOfMemoryException)
{
    // Over ssh without a pty, or in a CI job, there is no terminal to draw on. Say so plainly
    // rather than dumping a driver stack trace on somebody who just typed the program's name.
    Console.Error.WriteLine($"pdn-qso: cannot start the terminal UI - {e.Message}");
    return 1;
}

QsoConfig config;
if (onDisk is null)
{
    QsoConfig? wizard = FirstRunWizard.Run(app, new QsoConfig());
    if (wizard is null)
    {
        app.Dispose();
        Console.Error.WriteLine("pdn-qso: nothing was set up, so nothing was written.");
        return 1;
    }

    config = wizard;
    try
    {
        config.Save(configPath);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        MessageBox.ErrorQuery(
            app, "Settings", $"Could not write {configPath}: {e.Message}", "OK");
    }
}
else
{
    config = onDisk;
}

// For this session only, and never written back: somebody trying a different mode for ten
// minutes should not find their config quietly changed under them.
config = command.ApplyTo(config);

var host = new StationHost(config, command.MonitorOnly);
using var window = new MainWindow(app, host, PlaceholderActivity.All(), version, configPath);

window.WriteLog($"config: {configPath}");
if (command.HasOverrides)
{
    window.WriteLog("config: overridden for this session by the command line, not saved");
}

IReadOnlyList<string> problems = config.Validate();
if (problems.Count > 0)
{
    foreach (string problem in problems)
    {
        window.WriteLog($"settings: {problem}");
    }

    window.WriteLog("settings: press F5 to fix these - the station is not on the air");
}
else
{
    // Started in the background so the screen is up and readable even while a Flex is
    // connecting or an UberSDR is being talked to, and so a failure lands in the log pane
    // instead of on a terminal nobody is looking at any more.
    _ = Task.Run(async () =>
    {
        try
        {
            await host.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            app.Invoke(() => window.WriteLog($"station: could not start - {e.Message}"));
        }
    });
}

// SIGTERM (systemd, a package upgrade) and SIGINT get the same graceful path Ctrl+Q does: the
// run loop is asked to stop, and the station is torn down below - PTT dropped, pipes closed.
using PosixSignalRegistration term =
    PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
using PosixSignalRegistration interrupt =
    PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

void Stop(PosixSignalContext context)
{
    context.Cancel = true;
    app.Invoke(() => app.RequestStop());
}

app.Run(window);

// Not in a finally: the run loop has ended by now either way, and this has to complete before
// the process does. A transmitter left keyed by an untidy exit is the one failure that reaches
// beyond this machine.
await host.DisposeAsync();
return 0;
