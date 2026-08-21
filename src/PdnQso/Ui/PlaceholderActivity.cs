using PdnQso.Link;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>
/// An activity that is not here yet: it takes its place in the tab strip, says so plainly, and
/// is replaced wholesale when the real one lands.
/// </summary>
/// <remarks>
/// Chat, File and Perf are being built in parallel with this window against
/// <see cref="IActivityView"/>. A placeholder rather than a missing tab because the layout, the
/// F-key switching and the station wiring are all worth having working and provable before the
/// activities arrive, and because "not yet wired" on the screen is an honest thing for a tool
/// to say and a silently dead tab is not.
/// </remarks>
public sealed class PlaceholderActivity : IActivityView
{
    private readonly Label _label;

    /// <summary>Builds the placeholder for one activity.</summary>
    /// <param name="title">The activity's name.</param>
    /// <param name="summary">One line saying what it will do.</param>
    public PlaceholderActivity(string title, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title;

        _label = new Label
        {
            X = Pos.Center(),
            Y = Pos.Center(),
            Text = $"{title}: not yet wired.\n{summary}",
            TextAlignment = Alignment.Center,
        };

        var frame = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        frame.Add(_label);
        View = frame;
    }

    /// <inheritdoc />
    public string Title { get; }

    /// <inheritdoc />
    public View View { get; }

    /// <inheritdoc />
    public void Attach(IStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        _label.Text =
            $"{Title}: not yet wired.\n"
            + $"The station is up: {station.Callsign} on {station.DeviceName}, {station.Mode}.";
    }

    /// <summary>The three activities design.md names, as placeholders.</summary>
    public static IReadOnlyList<IActivityView> All() =>
    [
        new PlaceholderActivity(
            "Chat", "Keyboard to keyboard, with a stop-and-wait ARQ over the link frames."),
        new PlaceholderActivity(
            "File", "A fountain-coded file transfer: offer, symbols, status, done."),
        new PlaceholderActivity(
            "Perf", "Frame error rate, goodput, SNR and round-trip time, with a CSV export."),
    ];
}
