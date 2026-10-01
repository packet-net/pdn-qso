using System.Globalization;

namespace PdnQso.Config;

/// <summary>
/// What was on the command line: a config file to use instead of the default, overrides for
/// the three settings somebody is most likely to want to change for one session, and the
/// switch that starts a station with the transmitter locked out.
/// </summary>
/// <remarks>
/// The overrides are for the session only and are never written back. Somebody trying a
/// different mode for ten minutes should not find their config quietly changed under them, and
/// somebody running a second instance on a pipe should not have their real device overwritten.
/// </remarks>
public sealed record CommandLine
{
    /// <summary>The config file to read and write, or null for the default.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>A device string for this session only.</summary>
    public string? Device { get; init; }

    /// <summary>A mode for this session only.</summary>
    public string? Mode { get; init; }

    /// <summary>A callsign for this session only.</summary>
    public string? Callsign { get; init; }

    /// <summary>Start with the transmitter locked out: listen, log, say nothing.</summary>
    public bool MonitorOnly { get; init; }

    /// <summary>Print the help and exit.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Print the version and exit.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>
    /// What to do without the terminal UI, or null to start the UI. Set by any of
    /// <c>--respond</c>, <c>--listen</c>, <c>--ping</c>, <c>--chat</c> or <c>--stream</c>.
    /// </summary>
    public HeadlessCommand? Headless { get; init; }

    /// <summary>The stream payload size from <c>--payload</c>, for <c>--stream</c> only.</summary>
    public int? PayloadBytes { get; init; }

    /// <summary>How long <c>--respond</c> or <c>--listen</c> runs before exiting; null for
    /// until it is stopped.</summary>
    public TimeSpan? RunFor { get; init; }

    /// <summary>Why the command line was refused, or null when it was not.</summary>
    public string? Error { get; init; }

    /// <summary>Reads a command line. Never throws: a bad one comes back with an
    /// <see cref="Error"/> to print.</summary>
    public static CommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var parsed = new CommandLine();

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            string name = argument;
            string? inlineValue = null;

            int equals = argument.IndexOf('=', StringComparison.Ordinal);
            if (argument.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                name = argument[..equals];
                inlineValue = argument[(equals + 1)..];
            }

            switch (name)
            {
                case "--help" or "-h":
                    parsed = parsed with { ShowHelp = true };
                    break;
                case "--version" or "-V":
                    parsed = parsed with { ShowVersion = true };
                    break;
                case "--monitor-only":
                    parsed = parsed with { MonitorOnly = true };
                    break;
                case "--respond":
                    if (!TakeAction(parsed, new HeadlessCommand(HeadlessAction.Respond), out parsed))
                    {
                        return parsed;
                    }

                    break;
                case "--listen":
                    if (!TakeAction(parsed, new HeadlessCommand(HeadlessAction.Listen), out parsed))
                    {
                        return parsed;
                    }

                    break;
                case "--ping" or "--stream":
                {
                    if (!TakeValue(args, ref i, name, inlineValue, out string? countText, out CommandLine? countFailure))
                    {
                        return countFailure;
                    }

                    if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                        || count < 1)
                    {
                        return new CommandLine { Error = $"{name} takes a count of frames, 1 or more - try --help" };
                    }

                    HeadlessAction action = name == "--ping" ? HeadlessAction.Ping : HeadlessAction.Stream;
                    if (!TakeAction(parsed, new HeadlessCommand(action) { Count = count }, out parsed))
                    {
                        return parsed;
                    }

                    break;
                }

                case "--chat":
                    if (!TakeValue(args, ref i, name, inlineValue, out string? text, out CommandLine? chatFailure))
                    {
                        return chatFailure;
                    }

                    if (!TakeAction(parsed, new HeadlessCommand(HeadlessAction.Chat) { Text = text }, out parsed))
                    {
                        return parsed;
                    }

                    break;
                case "--payload":
                    if (!TakeValue(args, ref i, name, inlineValue, out string? payloadText, out CommandLine? payloadFailure))
                    {
                        return payloadFailure;
                    }

                    if (!int.TryParse(payloadText, NumberStyles.None, CultureInfo.InvariantCulture, out int payload))
                    {
                        return new CommandLine { Error = "--payload takes a size in bytes - try --help" };
                    }

                    parsed = parsed with { PayloadBytes = payload };
                    break;
                case "--for":
                    if (!TakeValue(args, ref i, name, inlineValue, out string? forText, out CommandLine? forFailure))
                    {
                        return forFailure;
                    }

                    if (!int.TryParse(forText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                        || seconds < 1)
                    {
                        return new CommandLine { Error = "--for takes a number of seconds - try --help" };
                    }

                    parsed = parsed with { RunFor = TimeSpan.FromSeconds(seconds) };
                    break;
                case "--config":
                    if (!TakeValue(args, ref i, name, inlineValue, out string? config, out CommandLine? configFailure))
                    {
                        return configFailure;
                    }

                    parsed = parsed with { ConfigPath = config };
                    break;
                case "--device":
                    if (!TakeValue(args, ref i, name, inlineValue, out string? device, out CommandLine? deviceFailure))
                    {
                        return deviceFailure;
                    }

                    parsed = parsed with { Device = device };
                    break;
                case "--mode":
                    if (!TakeValue(args, ref i, name, inlineValue, out string? mode, out CommandLine? modeFailure))
                    {
                        return modeFailure;
                    }

                    parsed = parsed with { Mode = mode };
                    break;
                case "--callsign":
                    if (!TakeValue(args, ref i, name, inlineValue, out string? callsign, out CommandLine? callsignFailure))
                    {
                        return callsignFailure;
                    }

                    parsed = parsed with { Callsign = callsign };
                    break;
                default:
                    return parsed with
                    {
                        Error = $"unknown argument '{argument}' - try --help",
                    };
            }
        }

        return Settle(parsed);
    }

    /// <summary>Applies this session's overrides to a config, leaving the config itself alone.</summary>
    public QsoConfig ApplyTo(QsoConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config with
        {
            Device = Device ?? config.Device,
            Mode = Mode ?? config.Mode,
            Callsign = Callsign ?? config.Callsign,
        };
    }

    /// <summary>True when this session's settings are not the ones on disk.</summary>
    public bool HasOverrides => Device is not null || Mode is not null || Callsign is not null;

    /// <summary>The config file this run uses.</summary>
    public string ResolvedConfigPath => ConfigPath ?? QsoConfig.DefaultPath;

    /// <summary>The <c>--help</c> text.</summary>
    public static string HelpText(string version) =>
        $"""
         pdn-qso {version} - interactive two-way testing over pdn-soundmodem and Tait radios

           pdn-qso                     start the terminal UI
           pdn-qso --monitor-only      start it with the transmitter locked out
           pdn-qso --respond           run without the screen, answering the far end
           pdn-qso --version           print the version and exit

         Options:
           --config <path>     use this config file instead of {QsoConfig.DefaultPath}
           --device <string>   an ALSA card (default, plughw:1,0), flex:<radio>[:slice][@station],
                               ubersdr:<instance>, pipe:<in>,<out>[,<rate>], or a Tait
                               TM8100/TM8200 on its serial port, tait:<port>[,<baud>]
           --mode <mode>       a modem mode, e.g. bpsk300, qpsk2400, ms110d-wn13; on a Tait,
                               tait-ffsk or tait-sdm, the radio's own modems
           --callsign <call>   CALL or CALL-SSID
           --monitor-only      never transmit: listen, show and log only
           -h, --help          this
           -V, --version       the version

         Without the screen, for scripts and tests over ssh (plain text out; exit 0 on
         success, 1 on loss or failure, 2 for a bad command line or config):
           --respond           answer chat lines, pings and streams, print what is heard
           --listen            print what is heard, never transmit
           --for <seconds>     with --respond or --listen, stop after this long
           --ping <n>          ping the far end n times and report round trips and loss
           --chat <text>       send one chat line and wait for it to be acknowledged
           --stream <n>        send n numbered frames and report what the far end heard
           --payload <bytes>   with --stream, the size of each frame's payload

         --device, --mode and --callsign are for this session only and are not written back.
         The callsign may carry an SSID: --callsign M0LTE-7.
         With no config file, the first run asks for what it needs; without the screen, a
         missing config is the defaults plus what the command line says.
         """;

    /// <summary>Takes a headless action, refusing a second one.</summary>
    private static bool TakeAction(CommandLine parsed, HeadlessCommand action, out CommandLine result)
    {
        if (parsed.Headless is not null)
        {
            result = new CommandLine
            {
                Error = "one of --respond, --listen, --ping, --chat or --stream at a time - try --help",
            };
            return false;
        }

        result = parsed with { Headless = action };
        return true;
    }

    /// <summary>The checks that need the whole command line, and folding the two modifiers
    /// into the action they modify.</summary>
    private static CommandLine Settle(CommandLine parsed)
    {
        if (parsed.PayloadBytes is not null && parsed.Headless?.Action != HeadlessAction.Stream)
        {
            return new CommandLine { Error = "--payload goes with --stream - try --help" };
        }

        if (parsed.RunFor is not null
            && parsed.Headless?.Action is not (HeadlessAction.Respond or HeadlessAction.Listen))
        {
            return new CommandLine { Error = "--for goes with --respond or --listen - try --help" };
        }

        if (parsed.MonitorOnly
            && parsed.Headless?.Action is HeadlessAction.Respond or HeadlessAction.Ping
                or HeadlessAction.Chat or HeadlessAction.Stream)
        {
            return new CommandLine
            {
                Error = $"--monitor-only never transmits, so it cannot {parsed.Headless.Action.ToString().ToLowerInvariant()}",
            };
        }

        return parsed.Headless is { } action
            ? parsed with { Headless = action with { PayloadBytes = parsed.PayloadBytes, RunFor = parsed.RunFor } }
            : parsed;
    }

    /// <summary>
    /// Takes an option's value, either from after an equals sign or from the next argument.
    /// </summary>
    /// <returns>False when there was none, with <paramref name="failure"/> set to the whole
    /// answer to give back - so the accumulated parse is discarded rather than half-reported.</returns>
    private static bool TakeValue(
        string[] args,
        ref int i,
        string name,
        string? inlineValue,
        out string? value,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out CommandLine? failure)
    {
        failure = null;
        value = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
        if (!string.IsNullOrEmpty(value))
        {
            return true;
        }

        failure = new CommandLine { Error = $"{name} needs a value - try --help" };
        return false;
    }
}

/// <summary>What a headless run does.</summary>
public enum HeadlessAction
{
    /// <summary>Answer chat, pings and streams until stopped.</summary>
    Respond,

    /// <summary>Print what is heard, never transmit.</summary>
    Listen,

    /// <summary>Ping the far end.</summary>
    Ping,

    /// <summary>Send one chat line.</summary>
    Chat,

    /// <summary>Send a numbered stream.</summary>
    Stream,
}

/// <summary>One headless run, as the command line asked for it.</summary>
/// <param name="Action">What to do.</param>
public sealed record HeadlessCommand(HeadlessAction Action)
{
    /// <summary>How many pings or stream frames.</summary>
    public int Count { get; init; } = 1;

    /// <summary>The chat line.</summary>
    public string? Text { get; init; }

    /// <summary>The stream payload size; null for the mode's sensible default.</summary>
    public int? PayloadBytes { get; init; }

    /// <summary>How long to respond or listen; null for until stopped.</summary>
    public TimeSpan? RunFor { get; init; }
}
