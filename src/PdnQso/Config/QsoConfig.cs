using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Chat;
using PdnQso.Link.Devices;
using PdnQso.Link.Fountain;
using PdnQso.Link.Transfer;

namespace PdnQso.Config;

/// <summary>
/// Everything the settings dialog holds, as it is written to
/// <c>~/.config/pdn-qso/config.json</c>: design.md section 6's list, plus the two things that
/// list needed and did not have (an RF frequency, and which PTT line a sound card keys with).
/// </summary>
/// <remarks>
/// <para>
/// A record rather than a bag of strings so that a bad value is caught by
/// <see cref="Validate"/> at the moment somebody presses Save, and not thirty minutes into a
/// QSO when the station is restarted. Every field has a default that works, so a config file
/// with one line in it is a valid config file.
/// </para>
/// <para>
/// The ARQ and fountain knobs are here because design.md puts them in the one dialog, even
/// though what reads them arrives with the chat and file activities. Nothing in this phase
/// looks at them; they are persisted so that when it does, the operator's setting is already
/// there.
/// </para>
/// </remarks>
public sealed record QsoConfig
{
    /// <summary>The device string: an ALSA card, <c>flex:</c>, <c>ubersdr:</c> or <c>pipe:</c>.</summary>
    public string Device { get; init; } = "default";

    /// <summary>This station's callsign, <c>CALL</c> or <c>CALL-SSID</c>.</summary>
    public string Callsign { get; init; } = "";

    /// <summary>The modem mode, from <c>ModemCatalog.AllModes</c>.</summary>
    public string Mode { get; init; } = "bpsk300";

    /// <summary>The modem's audio centre in Hz; null takes the mode's own default.</summary>
    public double? AudioCentreHz { get; init; }

    /// <summary>
    /// Where the audio centre should land in RF, in Hz. Needed by a Flex (which is being told
    /// where to tune) and by an UberSDR (which cannot guess); left alone on a sound card,
    /// where the rig's own VFO decides.
    /// </summary>
    public double? RfFrequencyHz { get; init; }

    /// <summary>Lower sideband instead of upper. USB is the data-mode norm.</summary>
    public bool LowerSideband { get; init; }

    /// <summary>TXDELAY: how long the transmitter is keyed before the data, in milliseconds.</summary>
    public int TxDelayMs { get; init; } = 300;

    /// <summary>Linear gain applied to captured audio before the modem sees it.</summary>
    public double InputGain { get; init; } = 1.0;

    /// <summary>Linear gain applied to transmit audio on its way to the radio.</summary>
    public double OutputGain { get; init; } = 1.0;

    /// <summary>What rate to open a sound card at; the adapter resamples to the mode's.</summary>
    public int CaptureRateHz { get; init; } = 48_000;

    /// <summary>Transmit power to set at start-up, in the device's own unit (watts on a Flex,
    /// per cent of the mixer's range on a sound card); null leaves whatever is there.</summary>
    public double? Power { get; init; }

    /// <summary>Which PTT line a sound card keys with: <c>none</c>, <c>cm108</c>, <c>serial</c>.</summary>
    public string PttType { get; init; } = "none";

    /// <summary>The PTT device: a hidraw node for CM108, a tty for serial.</summary>
    public string? PttDevice { get; init; }

    /// <summary>The CM108 GPIO pin. 3 is what the interface boards wire.</summary>
    public int PttGpio { get; init; } = 3;

    /// <summary>Which serial line to assert: <c>rts</c> or <c>dtr</c>.</summary>
    public string PttSerialLine { get; init; } = "rts";

    /// <summary>Send a Morse ident on the schedule the library's rules define.</summary>
    public bool IdentEnabled { get; init; } = true;

    /// <summary>What to identify as; null uses <see cref="Callsign"/>.</summary>
    public string? IdentCallsign { get; init; }

    /// <summary>How long after an identification the next may fall due, in minutes.</summary>
    public int IdentIntervalMinutes { get; init; } = 10;

    /// <summary>Ident sending speed, words per minute.</summary>
    public double IdentWpm { get; init; } = 20;

    /// <summary>
    /// The chat ARQ's ack timeout margin, in milliseconds: how long it waits <i>on top of</i>
    /// the time the mode itself takes to put the line and the answer on air. A fixed timeout
    /// cannot serve both a 9600 baud packet mode and a 300 baud one, so the mode's own frame
    /// time does the work and this is the margin for the far station's turnaround.
    /// </summary>
    public int AckTimeoutMs { get; init; } = 3000;

    /// <summary>How many times the chat ARQ retries one line before giving up.</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>
    /// Let the chat ARQ step the MS110D waveform down when retries pile up, and back up when
    /// the link recovers. Off pins the waveform where the operator put it, which is what a
    /// measurement run wants; on is what a QSO wants. No effect on a mode with no ladder.
    /// </summary>
    public bool StepWaveform { get; init; } = true;

    /// <summary>The robust soliton distribution's c, for the fountain coder.</summary>
    public double FountainC { get; init; } = 0.03;

    /// <summary>The robust soliton distribution's delta, for the fountain coder.</summary>
    public double FountainDelta { get; init; } = 0.5;

    /// <summary>
    /// Where to write the frame log, in the daemon's SQLite schema; null for the default under
    /// <c>~/.local/share/pdn-qso</c>, empty for no log at all.
    /// </summary>
    public string? FrameLogPath { get; init; }

    /// <summary>
    /// Where received files are written; null for <c>~/pdn-qso-received</c>. The directory is
    /// created when the first file arrives.
    /// </summary>
    public string? DownloadDirectory { get; init; }

    /// <summary>
    /// Where Perf's Export appends its CSV row; null for <c>~/pdn-qso-perf.csv</c>. The header
    /// line is written when the file is new, so the file is readable on its own.
    /// </summary>
    public string? PerfCsvPath { get; init; }

    /// <summary>The DAX channel to claim on a Flex. SmartSDR takes 1.</summary>
    public string FlexDaxChannel { get; init; } = "1";

    /// <summary>The Flex antenna to use on a headless bring-up.</summary>
    public string FlexAntenna { get; init; } = "ANT1";

    /// <summary>The UberSDR IQ mode: <c>iq48</c> everywhere, <c>iq96</c> where allowed.</summary>
    public string UberSdrMode { get; init; } = "iq48";

    /// <summary>Password for a protected UberSDR instance; null for a public one.</summary>
    public string? UberSdrPassword { get; init; }

    /// <summary>Drive the sound card's playback mixer as the transmit power control.</summary>
    public bool UseMixerPower { get; init; } = true;

    /// <summary>
    /// This user's home directory. <c>DoNotVerify</c> throughout, like every other path here:
    /// asking where a file should go must not depend on it already existing.
    /// </summary>
    private static string Home =>
        Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>The file this is read from and written to, unless <c>--config</c> says otherwise.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "pdn-qso",
            "config.json");

    /// <summary>Where received files go when <see cref="DownloadDirectory"/> does not say.</summary>
    public static string DefaultDownloadDirectory =>
        Path.Combine(Home, "pdn-qso-received");

    /// <summary>Where Perf's CSV goes when <see cref="PerfCsvPath"/> does not say.</summary>
    public static string DefaultPerfCsvPath =>
        Path.Combine(Home, "pdn-qso-perf.csv");

    /// <summary>Where the frame log goes when <see cref="FrameLogPath"/> does not say.</summary>
    public static string DefaultFrameLogPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "pdn-qso",
            "frames.db");

    /// <summary>The frame log this config asks for, or null when it asks for none.</summary>
    [JsonIgnore]
    public string? ResolvedFrameLogPath => FrameLogPath switch
    {
        null => DefaultFrameLogPath,
        "" => null,
        string path => path,
    };

    /// <summary>The directory received files are written to.</summary>
    [JsonIgnore]
    public string ResolvedDownloadDirectory =>
        string.IsNullOrWhiteSpace(DownloadDirectory) ? DefaultDownloadDirectory : DownloadDirectory;

    /// <summary>The file Perf's Export appends to.</summary>
    [JsonIgnore]
    public string ResolvedPerfCsvPath =>
        string.IsNullOrWhiteSpace(PerfCsvPath) ? DefaultPerfCsvPath : PerfCsvPath;

    /// <summary>Who this station identifies as.</summary>
    [JsonIgnore]
    public string ResolvedIdentCallsign =>
        string.IsNullOrWhiteSpace(IdentCallsign) ? Callsign : IdentCallsign;

    /// <summary>
    /// The audio centre the modem will actually run at: what was set, or the mode's own default,
    /// or nothing for a mode whose centre is fixed by its specification.
    /// </summary>
    [JsonIgnore]
    public double? ResolvedAudioCentreHz =>
        !ModemCatalog.IsKnown(Mode) || !ModemCatalog.AcceptsCentreFrequency(Mode)
            ? null
            : AudioCentreHz ?? ModemCatalog.DefaultCentreFrequencyFor(Mode);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads the config at <paramref name="path"/>, or null if there is not one.</summary>
    /// <exception cref="InvalidDataException">The file is there and is not readable as this
    /// config. An operator who has hand-edited it into a corner needs to be told which file and
    /// what was wrong with it, not handed a fresh default over the top of their work.</exception>
    public static QsoConfig? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<QsoConfig>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path} is empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is not readable as a pdn-qso config: {e.Message}", e);
        }
    }

    /// <summary>Writes the config to <paramref name="path"/>, creating the directory.</summary>
    /// <remarks>
    /// Written to a temporary file beside the real one and moved over it, so a crash halfway
    /// through leaves the previous config intact rather than half a file.
    /// </remarks>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Everything wrong with this config, in lines an operator can act on; empty when it will
    /// start a station.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Callsign))
        {
            problems.Add("Callsign: a station has to have one.");
        }
        else
        {
            try
            {
                _ = new LinkFrame(Callsign, LinkFrameType.Hello, 0);
            }
            catch (ArgumentException e)
            {
                problems.Add($"Callsign: {e.Message.Split(" (Parameter")[0]}");
            }
        }

        if (!DeviceString.TryParse(Device, out DeviceString? device, out string? deviceError))
        {
            problems.Add($"Device: {deviceError}");
        }

        if (!ModemCatalog.IsKnown(Mode))
        {
            string[] near = ModemCatalog.NearestModes(Mode);
            problems.Add(
                $"Mode: '{Mode}' is not a mode this tool has."
                + (near.Length > 0 ? $" Did you mean {string.Join(" or ", near)}?" : ""));
        }
        else if (AudioCentreHz is double centre)
        {
            if (!ModemCatalog.AcceptsCentreFrequency(Mode))
            {
                problems.Add(
                    $"Audio centre: {Mode} has a centre fixed by its specification and will not "
                    + "take one. Leave it blank.");
            }
            else if (centre <= 0 || centre >= ModemCatalog.DspRateFor(Mode) / 2.0)
            {
                problems.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Audio centre: {centre:0} Hz is outside the "
                        + $"{ModemCatalog.DspRateFor(Mode) / 2.0:0} Hz Nyquist of a "
                        + $"{ModemCatalog.DspRateFor(Mode)} Hz mode."));
            }
        }

        if (device is UberSdrDeviceString && RfFrequencyHz is null)
        {
            problems.Add(
                "RF frequency: an UberSDR receiver has to be told where to listen.");
        }

        if (RfFrequencyHz is double rf && rf <= 0)
        {
            problems.Add("RF frequency: it is a frequency in Hz, so it is above zero.");
        }

        if (TxDelayMs < 0)
        {
            problems.Add("TX delay: it cannot be negative.");
        }

        if (InputGain <= 0 || OutputGain <= 0)
        {
            problems.Add("Gain: a gain of zero is silence, and a negative one is nonsense.");
        }

        if (CaptureRateHz <= 0)
        {
            problems.Add("Capture rate: it is a sample rate in Hz.");
        }
        else if (ModemCatalog.IsKnown(Mode)
                 && device is AlsaDeviceString
                 && CaptureRateHz % ModemCatalog.DspRateFor(Mode) != 0)
        {
            problems.Add(
                $"Capture rate: {CaptureRateHz} Hz is not a whole multiple of the "
                + $"{ModemCatalog.DspRateFor(Mode)} Hz {Mode} runs at. 48000 works for every mode.");
        }

        if (Power is double power && power < 0)
        {
            problems.Add("Power: it cannot be negative.");
        }

        if (PttType is not ("none" or "cm108" or "serial"))
        {
            problems.Add($"PTT: '{PttType}' is not one of none, cm108, serial.");
        }
        else if (PttType != "none" && string.IsNullOrWhiteSpace(PttDevice))
        {
            problems.Add(
                $"PTT: a {PttType} line needs a device - /dev/hidraw0 for a CM108 widget, "
                + "/dev/ttyUSB0 for a serial line.");
        }

        if (PttSerialLine is not ("rts" or "dtr"))
        {
            problems.Add($"PTT line: '{PttSerialLine}' is not rts or dtr.");
        }

        if (IdentEnabled && IdentIntervalMinutes <= 0)
        {
            problems.Add("Ident interval: it has to be more than nothing.");
        }

        if (IdentEnabled && IdentWpm <= 0)
        {
            problems.Add("Ident speed: it is words per minute, so it is above zero.");
        }

        if (AckTimeoutMs <= 0)
        {
            problems.Add("Ack timeout: a timeout of zero never waits for anything.");
        }

        if (MaxRetries < 0)
        {
            problems.Add("Retries: it cannot be negative.");
        }

        if (DownloadDirectory is not null && DownloadDirectory.Trim().Length > 0
            && !Path.IsPathRooted(DownloadDirectory.Trim()))
        {
            problems.Add(
                "Download directory: give a full path. A relative one lands wherever the "
                + "program happened to be started from.");
        }

        if (PerfCsvPath is not null && PerfCsvPath.Trim().Length > 0
            && !Path.IsPathRooted(PerfCsvPath.Trim()))
        {
            problems.Add("Perf CSV: give a full path.");
        }

        if (FountainC <= 0 || FountainDelta is <= 0 or >= 1)
        {
            problems.Add("Fountain: c is above zero and delta is between zero and one.");
        }

        return problems;
    }

    /// <summary>The device options this config asks for.</summary>
    public DeviceOptions ToDeviceOptions(Action<string>? log = null, Action<Exception>? faulted = null) =>
        new()
        {
            Ptt = PttType switch
            {
                "cm108" => PttKind.Cm108,
                "serial" => PttKind.Serial,
                _ => PttKind.None,
            },
            PttDevice = PttDevice,
            Cm108Gpio = PttGpio,
            SerialLine = PttSerialLine,
            CaptureRateHz = CaptureRateHz,
            InputGain = (float)InputGain,
            OutputGain = (float)OutputGain,
            UseMixerPower = UseMixerPower,
            RfFrequencyHz = RfFrequencyHz,
            // Zero for a baseband mode (fsk*/c4fsk*), which occupies DC upwards and has no
            // centre to speak of: for those the dial is the RF frequency itself, and offsetting
            // it by a made-up 1500 Hz would put the whole signal in the wrong place.
            AudioCentreHz = ResolvedAudioCentreHz ?? 0,
            LowerSideband = LowerSideband,
            FlexAntenna = FlexAntenna,
            FlexDaxChannel = FlexDaxChannel,
            UberSdrMode = UberSdrMode,
            UberSdrPassword = UberSdrPassword,
            Log = log,
            Faulted = faulted,
        };

    /// <summary>The modem options this config asks for.</summary>
    public ModemOptions ToModemOptions() => new(CentreFrequencyHz: ResolvedAudioCentreHz);

    /// <summary>The chat ARQ options this config asks for.</summary>
    /// <remarks>
    /// Only the three knobs design.md puts in front of an operator are set here; the rest of
    /// <see cref="ChatOptions"/> keeps the library's own defaults, which are the ones the
    /// hermetic tests pin.
    /// </remarks>
    public ChatOptions ToChatOptions() => new()
    {
        AckTimeoutBase = TimeSpan.FromMilliseconds(Math.Max(1, AckTimeoutMs)),
        MaxRetries = MaxRetries,
        StepWaveform = StepWaveform,
    };

    /// <summary>The file transfer options this config asks for.</summary>
    public FileTransferOptions ToFileTransferOptions() => new()
    {
        Fountain = LtParameters.Default with { C = FountainC, Delta = FountainDelta },
    };

    /// <summary>The station options this config asks for.</summary>
    public StationOptions ToStationOptions() => new()
    {
        Callsign = Callsign,
        TxDelayMilliseconds = TxDelayMs,
        AudioCentreHz = ResolvedAudioCentreHz,
        RfHz = RfFrequencyHz,
    };
}
