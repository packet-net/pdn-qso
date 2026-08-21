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

    /// <summary>Fetch the current release and install it over this one, then exit.</summary>
    public bool Upgrade { get; init; }

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
                case "--upgrade":
                    parsed = parsed with { Upgrade = true };
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

        return parsed;
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
         pdn-qso {version} - interactive two-way testing over pdn-soundmodem

           pdn-qso                     start the terminal UI
           pdn-qso --monitor-only      start it with the transmitter locked out
           pdn-qso --upgrade           install the current release over this one
           pdn-qso --version           print the version and exit

         Options:
           --config <path>     use this config file instead of {QsoConfig.DefaultPath}
           --device <string>   an ALSA card (default, plughw:1,0), flex:<radio>[:slice][@station],
                               ubersdr:<instance>, or pipe:<in>,<out>[,<rate>]
           --mode <mode>       a modem mode, e.g. bpsk300, qpsk2400, ms110d-wn13
           --callsign <call>   CALL or CALL-SSID
           --monitor-only      never transmit: listen, show and log only
           --upgrade           fetch the current release's package for this machine,
                               check it against the release's checksums and install it
           -h, --help          this
           -V, --version       the version

         --device, --mode and --callsign are for this session only and are not written back.
         With no config file, the first run asks for what it needs.
         """;

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
