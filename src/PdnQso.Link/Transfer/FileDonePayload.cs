using System.Buffers.Binary;

namespace PdnQso.Link.Transfer;

/// <summary>
/// The body of a <see cref="LinkFrameType.FileDone"/> frame: decoded, checked and written.
/// </summary>
/// <remarks>
/// Wire layout, big endian: <c>fileId(4) symbols(4)</c>. The file id is repeated so that a
/// Done heard out of the blue names what it is about, and the symbol count is what the sender
/// prints as the transfer's real cost.
/// </remarks>
/// <param name="FileId">The file this is about.</param>
/// <param name="Symbols">How many symbols the receiver took in to get it.</param>
public readonly record struct FileDonePayload(uint FileId, int Symbols)
{
    /// <summary>The length of a done body.</summary>
    public const int Length = 8;

    /// <summary>The frame body these fields encode to.</summary>
    public byte[] Encode()
    {
        var payload = new byte[Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, FileId);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), Symbols);
        return payload;
    }

    /// <summary>Reads a done out of a frame body.</summary>
    /// <param name="payload">The frame's payload, after the type and session bytes.</param>
    /// <param name="done">The done, or default.</param>
    /// <returns><see langword="false"/> when the body is not exactly a done.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out FileDonePayload done)
    {
        if (payload.Length != Length)
        {
            done = default;
            return false;
        }

        done = new FileDonePayload(
            BinaryPrimitives.ReadUInt32BigEndian(payload),
            BinaryPrimitives.ReadInt32BigEndian(payload[4..]));
        return done.Symbols >= 0;
    }
}
