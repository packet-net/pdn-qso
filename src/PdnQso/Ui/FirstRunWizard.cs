using System.Globalization;
using M0LTE.Flex;
using Packet.SoundModem.Modems;
using PdnQso.Config;
using PdnQso.Link.Devices;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>
/// What happens on a first run with no config: which radio, who you are, which mode, and where
/// in the audio passband.
/// </summary>
/// <remarks>
/// <para>
/// Four questions in the order design.md section 6 puts them, each one showing what this
/// machine actually has rather than asking somebody to know a grammar: the sound cards the
/// kernel lists, the FlexRadio that answers a discovery broadcast, the modes the catalogue
/// holds. Anything not in a list can still be typed, because the radio is quite often on
/// another machine.
/// </para>
/// <para>
/// Backing out of any question abandons the whole thing and writes nothing, which is what a
/// first run should do: a half-written config is worse than none, because none is a state this
/// program knows how to recover from.
/// </para>
/// </remarks>
public static class FirstRunWizard
{
    /// <summary>How long to wait for a FlexRadio to answer a discovery broadcast.</summary>
    public static readonly TimeSpan FlexDiscoveryTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Asks the four questions and returns the config, or null if it was backed out of.
    /// </summary>
    /// <param name="app">The application instance.</param>
    /// <param name="defaults">What to start each answer at.</param>
    public static QsoConfig? Run(IApplication app, QsoConfig? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        QsoConfig config = defaults ?? new QsoConfig();

        string? device = AskDevice(app, config.Device);
        if (device is null)
        {
            return null;
        }

        config = config with { Device = device };

        string? callsign = ChoiceDialog.Show(
            app,
            "Callsign",
            "Your callsign. It goes in every frame this station sends.",
            [],
            config.Callsign,
            "CALL or CALL-SSID, e.g. M0LTE or M0LTE-7.");
        if (callsign is null)
        {
            return null;
        }

        config = config with { Callsign = callsign.Trim().ToUpperInvariant() };

        string? mode = ChoiceDialog.Show(
            app,
            "Mode",
            "The modem. Both ends have to be on the same one.",
            [.. ModemCatalog.AllModes.Select(m => new Choice(ModeLabel(m), m))],
            config.Mode);
        if (mode is null)
        {
            return null;
        }

        config = config with { Mode = mode.Trim() };

        if (ModemCatalog.IsKnown(config.Mode) && ModemCatalog.AcceptsCentreFrequency(config.Mode))
        {
            double suggested =
                ModemCatalog.DefaultCentreFrequencyFor(config.Mode) ?? 1500;
            string? centre = ChoiceDialog.Show(
                app,
                "Audio centre",
                $"Where {config.Mode} sits in the audio passband, in Hz.",
                [
                    new Choice(
                        Hz("The mode's own default", suggested),
                        suggested.ToString("0", CultureInfo.InvariantCulture)),
                    new Choice(Hz("Low in the passband", 700), "700"),
                    new Choice(Hz("Mid passband", 1500), "1500"),
                    new Choice(Hz("High in the passband", 2200), "2200"),
                ],
                suggested.ToString("0", CultureInfo.InvariantCulture),
                "Both ends have to agree. Leave the default unless you have a reason.");
            if (centre is null)
            {
                return null;
            }

            config = double.TryParse(
                centre, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz)
                ? config with { AudioCentreHz = hz }
                : config;
        }
        else
        {
            // A mode whose centre its specification fixes has nothing to ask about, and
            // offering the question anyway would invite an answer that gets refused on save.
            config = config with { AudioCentreHz = null };
        }

        IReadOnlyList<string> problems = config.Validate();
        if (problems.Count > 0)
        {
            MessageBox.ErrorQuery(
                app, "Settings", string.Join("\n", problems) + "\n\nOpen the settings and fix it.", "OK");
        }

        return config;
    }

    private static string ModeLabel(string mode) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{mode,-22} {ModemCatalog.DspRateFor(mode)} Hz");

    private static string Hz(string label, double hz) =>
        string.Create(CultureInfo.InvariantCulture, $"{label} ({hz:0} Hz)");

    private static string? AskDevice(IApplication app, string initial)
    {
        var choices = new List<Choice>();

        foreach (AlsaCard card in AlsaCards.List())
        {
            choices.Add(new Choice($"Sound card {card}", card.DeviceString));
        }

        if (Discover() is FlexRadioInfo flex)
        {
            string label = string.IsNullOrWhiteSpace(flex.Name) ? flex.Model : flex.Name;
            choices.Add(new Choice($"FlexRadio {label} at {flex.Ip}", $"flex:{flex.Ip}"));
        }

        choices.Add(new Choice(
            "FlexRadio - find it when the station starts", "flex:discover"));
        choices.Add(new Choice(
            "UberSDR web receiver (receive only) - put the host in the field below",
            "ubersdr:"));
        choices.Add(new Choice(
            "A pipe pair, for two copies of this tool on one machine",
            "pipe:/tmp/pdn-qso-a,/tmp/pdn-qso-b,48000"));

        return ChoiceDialog.Show(
            app,
            "Device",
            choices.Count > 3
                ? "The radio. What this machine has is listed; anything else can be typed."
                : "No sound cards found on this machine. Type the device, or pick one of these.",
            choices,
            initial,
            "An ALSA card, flex:<radio>[:slice][@station], ubersdr:<instance>, or pipe:<in>,<out>[,<rate>].");
    }

    /// <summary>
    /// One discovery broadcast, for the list. A radio that does not answer in a few seconds is
    /// not an error - <c>flex:discover</c> looks again when the station starts, and it may
    /// simply be on a network segment broadcasts do not cross.
    /// </summary>
    private static FlexRadioInfo? Discover()
    {
        try
        {
            using var timeout = new CancellationTokenSource(FlexDiscoveryTimeout + TimeSpan.FromSeconds(2));
            return FlexDiscovery
                .DiscoverAsync(null, FlexDiscoveryTimeout, timeout.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}
