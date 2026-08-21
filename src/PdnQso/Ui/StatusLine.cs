using System.Globalization;
using System.Text;

namespace PdnQso.Ui;

/// <summary>What the status bar is showing at one instant.</summary>
/// <param name="Device">The device string the station is on.</param>
/// <param name="Mode">The modem mode.</param>
/// <param name="AudioCentreHz">The modem's audio centre, or null for a mode with a fixed one.</param>
/// <param name="RfHz">Where that lands in RF, or null on a rig this tool does not tune.</param>
/// <param name="Power">The power control's own one-liner, e.g. <c>set 10 W, last 9.5 W</c>.</param>
/// <param name="Ptt">True while this station is keyed.</param>
/// <param name="Dcd">True while somebody else is using the channel.</param>
/// <param name="LastSnrDb">The SNR of the last frame heard, or null before there is one.</param>
/// <param name="Correspondent">Who we are working, or null before anyone has said hello.</param>
/// <param name="MonitorOnly">True when the transmitter is locked out for this session.</param>
public readonly record struct StatusSnapshot(
    string Device,
    string Mode,
    double? AudioCentreHz,
    double? RfHz,
    string Power,
    bool Ptt,
    bool Dcd,
    double? LastSnrDb,
    string? Correspondent,
    bool MonitorOnly);

/// <summary>
/// The status bar of design.md section 6, as a pure function: device, mode, centre, power with
/// its read-back, the PTT and DCD lamps, the last SNR and who we are working.
/// </summary>
/// <remarks>
/// Everything is ASCII, so it reads the same over a serial console as it does in a terminal
/// emulator: the lamps are <c>[TX]</c> and <c>[DCD]</c> in square brackets rather than coloured
/// blocks, and an unlit lamp keeps its width so the line does not shuffle sideways every time
/// somebody keys up.
/// </remarks>
public static class StatusLine
{
    /// <summary>Renders the status bar.</summary>
    public static string Format(StatusSnapshot status)
    {
        var line = new StringBuilder(160);
        line.Append(status.MonitorOnly ? "[MONITOR] " : "");
        line.Append(status.Ptt ? "[TX ] " : "[   ] ");
        line.Append(status.Dcd ? "[DCD] " : "[   ] ");
        line.Append(status.Device).Append("  ");
        line.Append(status.Mode);

        if (status.AudioCentreHz is double centre)
        {
            line.Append(string.Create(CultureInfo.InvariantCulture, $" @ {centre:0} Hz"));
        }

        if (status.RfHz is double rf)
        {
            line.Append(string.Create(CultureInfo.InvariantCulture, $" ({rf / 1_000_000.0:0.000000} MHz)"));
        }

        if (!string.IsNullOrWhiteSpace(status.Power))
        {
            line.Append("  pwr ").Append(status.Power);
        }

        line.Append(status.LastSnrDb is double snr
            ? string.Create(CultureInfo.InvariantCulture, $"  snr {snr:0.0} dB")
            : "  snr -");

        if (!string.IsNullOrWhiteSpace(status.Correspondent))
        {
            line.Append("  with ").Append(status.Correspondent);
        }

        return line.ToString();
    }
}
