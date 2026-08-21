using System.Diagnostics.CodeAnalysis;
using Packet.SoundModem.Waterfall;

namespace PdnQso.Link;

/// <summary>
/// One frame of the pdn-qso link protocol, carried as an ordinary AX.25 UI frame so that
/// every monitor, node and frame log on the channel sees well-formed traffic.
/// </summary>
/// <remarks>
/// <para>
/// The wire shape is docs/design.md section 3: destination <see cref="DefaultDestination"/>,
/// source the station's callsign with its SSID, control 0x03 (UI), PID 0xF0 (no layer 3), and
/// an information field whose first two bytes are the <see cref="LinkFrameType"/> and the
/// session id, followed by the type's own payload.
/// </para>
/// <para>
/// <see cref="Encode"/> produces exactly the byte array
/// <c>Packet.SoundModem.Modems.IModem.Modulate</c> takes, and <see cref="TryDecode"/> takes
/// exactly the byte array <c>IModem.FrameDecoded</c> delivers - no flags, no FCS, no KISS
/// framing. A frame that is not ours (a node's traffic, a beacon, an APRS position) is a
/// <see langword="false"/> from <see cref="TryDecode"/> and never an exception: on a shared
/// channel most of what is heard belongs to somebody else, and Monitor has to show it.
/// </para>
/// <para>
/// The address encoding is the library's - <see cref="Ax25UiFrame.Build"/> - so a pdn-qso
/// frame and a pdn-soundmodem frame cannot drift apart in their shifted ASCII, SSID bits or
/// end-of-address bit. Decoding is this repo's own, because it has to be stricter than a
/// label maker: a digipeater path would move the information field and must be rejected,
/// not read past.
/// </para>
/// </remarks>
public sealed class LinkFrame
{
    /// <summary>The destination callsign every pdn-qso frame is addressed to.</summary>
    public const string DefaultDestination = "QSO";

    /// <summary>AX.25 control byte for an unnumbered information frame.</summary>
    public const byte UiControl = 0x03;

    /// <summary>AX.25 PID for "no layer 3 protocol".</summary>
    public const byte NoLayer3Pid = 0xF0;

    /// <summary>Bytes before the information field: two 7-byte addresses, control, PID.</summary>
    public const int HeaderLength = 16;

    /// <summary>Information-field bytes before the type's own payload: type, session.</summary>
    public const int InfoHeaderLength = 2;

    private readonly byte[] _payload;

    /// <summary>Builds a link frame.</summary>
    /// <param name="source">The sending station, <c>CALL</c> or <c>CALL-SSID</c> (SSID 0-15).</param>
    /// <param name="type">What this frame is.</param>
    /// <param name="session">The session id: random per conversation, transfer or perf run,
    /// so two overlapping activities on one channel cannot be confused for one another.</param>
    /// <param name="payload">The type's own fields; empty for a frame that has none.</param>
    /// <param name="destination">Normally <see cref="DefaultDestination"/>; overridable only
    /// so a future mode can address a frame elsewhere.</param>
    /// <exception cref="ArgumentException">A callsign is not <c>CALL</c> or <c>CALL-SSID</c>,
    /// or <paramref name="type"/> is not a defined <see cref="LinkFrameType"/>.</exception>
    public LinkFrame(
        string source,
        LinkFrameType type,
        byte session,
        ReadOnlySpan<byte> payload = default,
        string destination = DefaultDestination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        Source = Normalise(source, nameof(source));
        Destination = Normalise(destination, nameof(destination));
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentException($"0x{(byte)type:X2} is not a link frame type", nameof(type));
        }

        Type = type;
        Session = session;
        _payload = payload.ToArray();
    }

    /// <summary>The sending station, <c>CALL</c> or <c>CALL-SSID</c>, upper case.</summary>
    public string Source { get; }

    /// <summary>The destination, normally <see cref="DefaultDestination"/>.</summary>
    public string Destination { get; }

    /// <summary>What this frame is.</summary>
    public LinkFrameType Type { get; }

    /// <summary>The session id this frame belongs to.</summary>
    public byte Session { get; }

    /// <summary>The type's own fields, after the type and session bytes.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>The AX.25 UI frame this encodes to: what <c>IModem.Modulate</c> takes.</summary>
    public byte[] Encode()
    {
        var info = new byte[InfoHeaderLength + _payload.Length];
        info[0] = (byte)Type;
        info[1] = Session;
        _payload.CopyTo(info, InfoHeaderLength);
        return Ax25UiFrame.Build(Source, Destination, info);
    }

    /// <summary>
    /// Reads a link frame out of a decoded AX.25 frame, or reports that the frame is not ours.
    /// </summary>
    /// <param name="ax25Frame">A frame as <c>IModem.FrameDecoded</c> delivers it: addresses
    /// first, no flags and no FCS.</param>
    /// <param name="frame">The decoded link frame, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only for a UI frame addressed to
    /// <see cref="DefaultDestination"/>, with no digipeater path, PID 0xF0, and a defined
    /// <see cref="LinkFrameType"/> in its first information byte.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> ax25Frame, [NotNullWhen(true)] out LinkFrame? frame)
    {
        frame = null;
        if (ax25Frame.Length < HeaderLength + InfoHeaderLength)
        {
            return false;
        }

        // A set end-of-address bit on the destination, or a clear one on the source, means a
        // digipeater path: the information field is not at byte 16 and reading it as though it
        // were would decode somebody else's routing as a chat line.
        if (!TryReadAddress(ax25Frame[..7], out string destination, out bool destinationLast)
            || destinationLast
            || !TryReadAddress(ax25Frame.Slice(7, 7), out string source, out bool sourceLast)
            || !sourceLast)
        {
            return false;
        }

        if (ax25Frame[14] != UiControl || ax25Frame[15] != NoLayer3Pid)
        {
            return false;
        }

        if (!string.Equals(destination, DefaultDestination, StringComparison.Ordinal))
        {
            return false;
        }

        var type = (LinkFrameType)ax25Frame[HeaderLength];
        if (!Enum.IsDefined(type))
        {
            return false;
        }

        frame = new LinkFrame(
            source,
            type,
            ax25Frame[HeaderLength + 1],
            ax25Frame[(HeaderLength + InfoHeaderLength)..],
            destination);
        return true;
    }

    /// <summary>
    /// <see cref="TryDecode"/> for a frame that is known to be ours, throwing if it is not.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a pdn-qso link frame.</exception>
    public static LinkFrame Decode(ReadOnlySpan<byte> ax25Frame) =>
        TryDecode(ax25Frame, out LinkFrame? frame)
            ? frame
            : throw new FormatException(
                $"not a pdn-qso link frame: {ax25Frame.Length} bytes, expected an AX.25 UI frame "
                + $"to {DefaultDestination} with PID 0x{NoLayer3Pid:X2}, no digipeaters, and a "
                + "known type byte");

    /// <summary>A one-line rendering for a monitor pane, e.g. <c>M0LTE-1 CHAT s=0x2A 14 bytes</c>.</summary>
    public override string ToString() =>
        $"{Source} {Type} s=0x{Session:X2} {_payload.Length} bytes";

    /// <summary>
    /// Reads one shifted 7-byte AX.25 address field.
    /// </summary>
    /// <param name="field">The seven bytes.</param>
    /// <param name="address">The callsign, <c>CALL</c> or <c>CALL-SSID</c>.</param>
    /// <param name="last">The end-of-address bit: set on the final address of the field.</param>
    /// <remarks>
    /// Stricter than a display parser on purpose (see the class remarks): the extension bit of
    /// each of the six callsign bytes must be clear, padding may only trail, and the characters
    /// must be upper-case alphanumerics. The two reserved bits of the SSID byte are NOT
    /// required, because stations in the wild clear them and a frame is still perfectly
    /// readable, and the command/response bit is ignored for the same reason.
    /// </remarks>
    private static bool TryReadAddress(ReadOnlySpan<byte> field, out string address, out bool last)
    {
        address = "";
        last = (field[6] & 0x01) != 0;

        Span<char> call = stackalloc char[6];
        int length = 0;
        bool padding = false;
        for (int i = 0; i < 6; i++)
        {
            if ((field[i] & 0x01) != 0)
            {
                return false;
            }

            char c = (char)(field[i] >> 1);
            if (c == ' ')
            {
                padding = true;
                continue;
            }

            if (padding || c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                return false;
            }

            call[length++] = c;
        }

        if (length == 0)
        {
            return false;
        }

        int ssid = (field[6] >> 1) & 0x0F;
        address = ssid == 0
            ? new string(call[..length])
            : string.Concat(call[..length], "-", ssid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Validates and upper-cases a callsign, the same grammar the encoder takes.</summary>
    private static string Normalise(string address, string parameter)
    {
        string trimmed = address.Trim().ToUpperInvariant();
        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        ReadOnlySpan<char> call = dash < 0 ? trimmed : trimmed.AsSpan(0, dash);
        int ssid = 0;
        if (dash >= 0 && !int.TryParse(
                trimmed.AsSpan(dash + 1), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out ssid))
        {
            throw new ArgumentException($"'{address}' is not CALL or CALL-SSID (SSID 0-15)", parameter);
        }

        if (call.Length is 0 or > 6 || ssid is < 0 or > 15)
        {
            throw new ArgumentException($"'{address}' is not CALL or CALL-SSID (SSID 0-15)", parameter);
        }

        foreach (char c in call)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                throw new ArgumentException(
                    $"'{address}' has '{c}' in the callsign; AX.25 addresses are upper-case "
                    + "letters and digits only", parameter);
            }
        }

        return trimmed;
    }
}
