using System.Buffers.Binary;

namespace PdnQso.Link.Transfer;

/// <summary>
/// The body of a <see cref="LinkFrameType.FileStatus"/> frame: the receiver's "I have n of K".
/// </summary>
/// <remarks>
/// <para>
/// Wire layout, big endian: <c>decoded(4) blockCount(4) received(4)</c>. Twelve bytes, sent
/// every <see cref="FileTransferOptions.StatusInterval"/> and whenever the sender asks by
/// re-sending its offer.
/// </para>
/// <para>
/// It says how many blocks are decoded, never <em>which</em>. That is the fountain's bargain:
/// the sender does not need to know, so the status frame stays the same twelve bytes whether
/// the file is one block or ten thousand, and a status frame that is lost costs nothing but
/// the interval.
/// </para>
/// <para>
/// <c>received</c> is the count of symbols the receiver has taken in. The sender does not act
/// on it; it is there so that both ends can show, and a test can assert, what the transfer
/// actually cost in symbols against the K it should have cost.
/// </para>
/// </remarks>
/// <param name="Decoded">How many of the K source blocks the receiver has.</param>
/// <param name="BlockCount">K, echoed back so a status frame is readable on its own.</param>
/// <param name="Received">How many symbols the receiver has taken in.</param>
public readonly record struct FileStatusPayload(int Decoded, int BlockCount, int Received)
{
    /// <summary>The length of a status body.</summary>
    public const int Length = 12;

    /// <summary>True when the receiver says it has the lot.</summary>
    public bool IsComplete => BlockCount > 0 && Decoded >= BlockCount;

    /// <summary>The frame body these fields encode to.</summary>
    public byte[] Encode()
    {
        var payload = new byte[Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, Decoded);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), BlockCount);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), Received);
        return payload;
    }

    /// <summary>Reads a status out of a frame body.</summary>
    /// <param name="payload">The frame's payload, after the type and session bytes.</param>
    /// <param name="status">The status, or default.</param>
    /// <returns><see langword="false"/> when the body is not exactly a status.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out FileStatusPayload status)
    {
        if (payload.Length != Length)
        {
            status = default;
            return false;
        }

        status = new FileStatusPayload(
            BinaryPrimitives.ReadInt32BigEndian(payload),
            BinaryPrimitives.ReadInt32BigEndian(payload[4..]),
            BinaryPrimitives.ReadInt32BigEndian(payload[8..]));
        return status is { Decoded: >= 0, BlockCount: >= 0, Received: >= 0 };
    }
}
