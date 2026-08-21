using System.Text;

namespace PdnQso.Link.Chat;

/// <summary>
/// The information field of a <see cref="LinkFrameType.Chat"/> frame, after the link header's
/// type and session bytes: <c>seq(1) | waveform(1) | UTF-8 text</c>.
/// </summary>
/// <remarks>
/// <para>
/// docs/design.md section 3 specifies <c>seq(1)</c> then the text; the waveform byte is the
/// one addition phase B makes to it, so that a receiver can show that the far station has
/// stepped down without anything being negotiated. It is a statement, not a request: an
/// MS110D receiver is autobaud and does not need to be told what to listen for.
/// </para>
/// <para>
/// A station whose modem has no waveform ladder sends <see cref="NoWaveform"/>, which cannot
/// collide with a real waveform number (MIL-STD-188-110D Phase A defines 0-8 and 13).
/// </para>
/// </remarks>
public static class ChatPayload
{
    /// <summary>The waveform byte of a station that has no waveform to report.</summary>
    public const byte NoWaveform = 0xFF;

    /// <summary>Bytes before the text: the sequence number and the waveform flag.</summary>
    public const int HeaderLength = 2;

    /// <summary>Builds the payload of a chat frame.</summary>
    /// <param name="seq">The line's sequence number, which the acknowledgement echoes.</param>
    /// <param name="waveform">The sender's current transmit waveform, or null for a station
    /// with no ladder.</param>
    /// <param name="text">The line, which must already be clean (see <see cref="Sanitise"/>).</param>
    public static byte[] Encode(byte seq, int? waveform, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int textBytes = Encoding.UTF8.GetByteCount(text);
        var payload = new byte[HeaderLength + textBytes];
        payload[0] = seq;
        payload[1] = waveform is int wn and >= 0 and < NoWaveform ? (byte)wn : NoWaveform;
        Encoding.UTF8.GetBytes(text, payload.AsSpan(HeaderLength));
        return payload;
    }

    /// <summary>
    /// Reads a chat payload, or reports that it is too short to be one.
    /// </summary>
    /// <param name="payload">The bytes after the link frame's type and session.</param>
    /// <param name="seq">The line's sequence number.</param>
    /// <param name="waveform">The sender's transmit waveform, or null when it reported none.</param>
    /// <param name="text">The line, with control characters removed.</param>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload, out byte seq, out int? waveform, out string text)
    {
        seq = 0;
        waveform = null;
        text = "";
        if (payload.Length < HeaderLength)
        {
            return false;
        }

        seq = payload[0];
        waveform = payload[1] == NoWaveform ? null : payload[1];
        text = Sanitise(Encoding.UTF8.GetString(payload[HeaderLength..]));
        return true;
    }

    /// <summary>Builds the payload of an acknowledgement: the sequence number being answered.</summary>
    public static byte[] EncodeAck(byte seq) => [seq];

    /// <summary>Reads the sequence number an acknowledgement carries.</summary>
    public static bool TryDecodeAck(ReadOnlySpan<byte> payload, out byte seq)
    {
        seq = payload.Length > 0 ? payload[0] : (byte)0;
        return payload.Length > 0;
    }

    /// <summary>
    /// Strips the C0 control characters and DEL from a line that came off the air.
    /// </summary>
    /// <remarks>
    /// This is a safety rule, not a style one. The text goes to a terminal UI, and a frame
    /// carrying an escape sequence would otherwise be able to drive it: move the cursor, set
    /// a colour, or worse. Nobody sends a control character in a keyboard-to-keyboard QSO, so
    /// there is nothing to lose by dropping them at the edge of the protocol.
    /// </remarks>
    public static string Sanitise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool clean = true;
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
