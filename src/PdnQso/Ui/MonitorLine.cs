using System.Globalization;
using System.Text;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;
using PdnQso.Link;

namespace PdnQso.Ui;

/// <summary>How the Monitor pane renders a frame's payload.</summary>
public enum PayloadView
{
    /// <summary>Printable ASCII, with anything else as a dot.</summary>
    Text,

    /// <summary>Two hex digits per byte.</summary>
    Hex,
}

/// <summary>
/// One line of the Monitor pane, as a pure function of the frame and what the decode
/// established about it.
/// </summary>
/// <remarks>
/// <para>
/// Pure on purpose. The pane itself is a scrolling list of strings and has nothing in it worth
/// testing; what is worth testing is that a frame from somebody else's node shows its
/// callsigns, that a chased-erasure decode says how much work it took, and that a payload full
/// of binary does not scribble control characters across a terminal. All of that is here.
/// </para>
/// <para>
/// The addresses come from the library's <c>Ax25AddressParser</c> - the same label maker the
/// waterfall attributes bursts with - so a frame this tool cannot decode as its own still shows
/// who sent it. On a shared channel most of what is heard belongs to somebody else, and the
/// whole point of the pane is to show it.
/// </para>
/// </remarks>
public static class MonitorLine
{
    /// <summary>The column headings the rendered lines line up under.</summary>
    public const string Header =
        "time     from       to         type      snr  offset  er  ch  payload";

    /// <summary>How much payload one line carries before it is cut short.</summary>
    public const int DefaultPayloadWidth = 120;

    /// <summary>Renders one heard or sent frame.</summary>
    /// <param name="at">When it was heard.</param>
    /// <param name="frame">The AX.25 frame, as the modem delivered it.</param>
    /// <param name="quality">What the decode established.</param>
    /// <param name="view">Payload as text or as hex.</param>
    /// <param name="payloadWidth">How much payload to show.</param>
    /// <param name="outgoing">True for a frame this station sent, which is marked so an
    /// operator is never left wondering whether they are hearing themselves.</param>
    public static string Format(
        DateTimeOffset at,
        ReadOnlySpan<byte> frame,
        FrameQuality quality,
        PayloadView view = PayloadView.Text,
        int payloadWidth = DefaultPayloadWidth,
        bool outgoing = false)
    {
        Ax25AddressParser.TryParse(frame, out string source, out string destination);

        bool ours = LinkFrame.TryDecode(frame, out LinkFrame? link);
        string type = ours ? link!.Type.ToString().ToUpperInvariant() : "-";
        ReadOnlySpan<byte> payload = PayloadOf(frame, ours);

        var line = new StringBuilder(Header.Length + payloadWidth);
        line.Append(at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        line.Append(outgoing ? " >" : "  ");
        line.Append(Pad(source, 9)).Append(' ');
        line.Append(Pad(destination, 10)).Append(' ');
        line.Append(Pad(type, 9)).Append(' ');
        line.Append(Number(quality.SnrDb, "0.0", 5)).Append(' ');
        line.Append(Number(quality.FrequencyOffsetHz, "0", 6)).Append(' ');
        line.Append(Count(quality.ErasedBytes, 3)).Append(' ');
        line.Append(Count(quality.ChasedBits, 3)).Append(' ');

        string flags = Flags(quality);
        if (flags.Length > 0)
        {
            line.Append(flags).Append(' ');
        }

        line.Append(Payload(payload, view, payloadWidth));
        return line.ToString();
    }

    /// <summary>The short flags a decode earns: how the frame was read, and what it cost.</summary>
    /// <remarks>
    /// <c>RS</c> is a plain-IL2P frame read by a link expecting IL2P+CRC: the Reed-Solomon
    /// decode says the bytes are right, but nothing checked them end to end. <c>BAD</c> is a
    /// trailing CRC that did not verify. Both are shown because an operator watching a marginal
    /// path wants to know which of the two they are getting.
    /// </remarks>
    public static string Flags(FrameQuality quality)
    {
        var flags = new List<string>(3);
        if (quality.CrcValid == false)
        {
            flags.Add("BAD");
        }

        if (quality.PlainIl2p || quality.MonitorOnly)
        {
            flags.Add("RS");
        }

        return flags.Count == 0 ? "" : string.Join(",", flags);
    }

    private static ReadOnlySpan<byte> PayloadOf(ReadOnlySpan<byte> frame, bool ours)
    {
        int start = ours
            ? LinkFrame.HeaderLength + LinkFrame.InfoHeaderLength
            : LinkFrame.HeaderLength;
        return frame.Length > start ? frame[start..] : [];
    }

    private static string Payload(ReadOnlySpan<byte> payload, PayloadView view, int width)
    {
        if (payload.Length == 0)
        {
            return "";
        }

        if (view == PayloadView.Hex)
        {
            int bytes = Math.Min(payload.Length, Math.Max(1, width / 3));
            string hex = Convert.ToHexString(payload[..bytes]);
            var spaced = new StringBuilder(bytes * 3);
            for (int i = 0; i < bytes; i++)
            {
                if (i > 0)
                {
                    spaced.Append(' ');
                }

                spaced.Append(hex[i * 2]).Append(hex[(i * 2) + 1]);
            }

            return bytes < payload.Length ? spaced.Append("...").ToString() : spaced.ToString();
        }

        int shown = Math.Min(payload.Length, width);
        var text = new StringBuilder(shown + 3);
        for (int i = 0; i < shown; i++)
        {
            // Printable ASCII only. A frame full of binary is a normal thing to hear, and a
            // terminal handed its escape sequences is a terminal that stops being readable.
            char c = (char)payload[i];
            text.Append(c is >= ' ' and <= '~' ? c : '.');
        }

        return shown < payload.Length ? text.Append("...").ToString() : text.ToString();
    }

    private static string Pad(string value, int width) =>
        value.Length >= width ? value[..width] : value.PadRight(width);

    private static string Number(double? value, string format, int width)
    {
        if (value is not double number)
        {
            return "-".PadLeft(width);
        }

        // A carrier offset of -0.3 Hz in a column of whole numbers renders as "-0", which
        // reads as a measurement rather than as the rounding it is. Drop the sign when
        // everything that survived the rounding is a zero.
        string text = number.ToString(format, CultureInfo.InvariantCulture);
        if (text.StartsWith('-') && text.AsSpan(1).IndexOfAnyExcept('0', '.') < 0)
        {
            text = text[1..];
        }

        return text.PadLeft(width);
    }

    private static string Count(int? value, int width) =>
        value is int number
            ? number.ToString(CultureInfo.InvariantCulture).PadLeft(width)
            : "-".PadLeft(width);
}
