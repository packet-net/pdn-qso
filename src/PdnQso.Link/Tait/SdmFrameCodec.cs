namespace PdnQso.Link.Tait;

/// <summary>
/// Puts an AX.25 frame into a binary Short Data Message and takes it out again, avoiding the
/// four byte values a binary SDM cannot carry.
/// </summary>
/// <remarks>
/// <para>
/// The CCDI driver refuses 0x0A, 0x0D, 0x11 and 0x13 in a binary SDM, because on the serial
/// leg they are line framing and flow control. An AX.25 frame can hold any of them, so the
/// frame is reshaped here first, and the reshaping is bounded so that what fits is known
/// before anything is sent rather than discovered by the radio's refusal.
/// </para>
/// <para>
/// The scheme: every byte is XORed with a key, the key is the first byte of the message, and
/// any byte that still lands on one of the four (or on the escape byte itself) is sent as
/// the escape byte followed by that byte XORed with 0x20. The key is the one of the 252 legal
/// values that leaves the fewest bytes needing an escape. Each frame byte rules out at most
/// five keys, so summed over every key there are at most 5n escapes for an n-byte frame, and
/// the best key therefore needs no more than 5n/252 of them. For the 125 bytes this allows
/// that is two, so 125 bytes is at most 1 + 125 + 2 = 128 on air, the binary SDM's limit,
/// whatever the frame holds.
/// </para>
/// </remarks>
public static class SdmFrameCodec
{
    /// <summary>The most a binary SDM carries, in bytes (CCDI section 1.9.8).</summary>
    public const int MaxMessageBytes = 128;

    /// <summary>The largest frame that always fits, whatever its bytes are.</summary>
    public const int MaxFrameBytes = 125;

    private const byte Escape = 0x1B;
    private const byte EscapeFlip = 0x20;

    /// <summary>True for the bytes a binary SDM will not take.</summary>
    public static bool IsRefused(byte value) => value is 0x0A or 0x0D or 0x11 or 0x13;

    /// <summary>Reshapes a frame into an SDM message.</summary>
    /// <exception cref="ArgumentException">Empty, or longer than <see cref="MaxFrameBytes"/>.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty || frame.Length > MaxFrameBytes)
        {
            throw new ArgumentException(
                $"an SDM carries a frame of 1 to {MaxFrameBytes} bytes, and this one is {frame.Length}",
                nameof(frame));
        }

        // How many bytes each key would leave needing an escape: a byte b lands on a special
        // value s under key k exactly when k == b ^ s.
        Span<int> cost = stackalloc int[256];
        foreach (byte b in frame)
        {
            cost[b ^ 0x0A]++;
            cost[b ^ 0x0D]++;
            cost[b ^ 0x11]++;
            cost[b ^ 0x13]++;
            cost[b ^ Escape]++;
        }

        int key = -1;
        for (int k = 0; k < 256; k++)
        {
            if (!IsRefused((byte)k) && (key < 0 || cost[k] < cost[key]))
            {
                key = k;
            }
        }

        var message = new byte[1 + frame.Length + cost[key]];
        message[0] = (byte)key;
        int at = 1;
        foreach (byte b in frame)
        {
            byte keyed = (byte)(b ^ key);
            if (IsRefused(keyed) || keyed == Escape)
            {
                message[at++] = Escape;
                message[at++] = (byte)(keyed ^ EscapeFlip);
            }
            else
            {
                message[at++] = keyed;
            }
        }

        return message;
    }

    /// <summary>Recovers the frame from an SDM message.</summary>
    /// <returns>False when the message is not one <see cref="Encode"/> made: empty, or ending
    /// in the middle of an escape.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> message, out byte[] frame)
    {
        frame = [];
        if (message.Length < 2)
        {
            return false;
        }

        byte key = message[0];
        var decoded = new byte[message.Length - 1];
        int length = 0;
        for (int i = 1; i < message.Length; i++)
        {
            byte b = message[i];
            if (b == Escape)
            {
                if (++i == message.Length)
                {
                    return false;
                }

                b = (byte)(message[i] ^ EscapeFlip);
            }

            decoded[length++] = (byte)(b ^ key);
        }

        frame = decoded.AsSpan(0, length).ToArray();
        return true;
    }
}
