// pdn-qso - a terminal tool for interactive two-way testing over the pdn-soundmodem modems.
//
// Phase A ships the skeleton: this brings up a Terminal.Gui window and quits on Ctrl+Q. The
// real screen - the always-on Monitor pane, the status bar, and the Chat / File / Perf panes
// below it - is phase A2's, and the protocol underneath it lives in PdnQso.Link, where it is
// tested without a terminal at all.
using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
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

foreach (string arg in args)
{
    switch (arg)
    {
        case "--version" or "-V":
            Console.WriteLine($"pdn-qso {version}");
            return 0;
        case "--help" or "-h":
            Console.WriteLine($"pdn-qso {version} - interactive two-way testing over pdn-soundmodem");
            Console.WriteLine();
            Console.WriteLine("  pdn-qso            start the terminal UI (Ctrl+Q quits)");
            Console.WriteLine("  pdn-qso --version  print the version and exit");
            Console.WriteLine();
            Console.WriteLine("Settings will live in ~/.config/pdn-qso/config.json.");
            return 0;
        default:
            Console.Error.WriteLine($"pdn-qso: unknown argument '{arg}' - try --help");
            return 2;
    }
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

using var window = new Window
{
    Title = $"pdn-qso {version} (Ctrl+Q to quit)",
};

window.Add(new Label
{
    X = Pos.Center(),
    Y = Pos.Center(),
    Text = "pdn-qso",
});

app.Run(window);
return 0;
