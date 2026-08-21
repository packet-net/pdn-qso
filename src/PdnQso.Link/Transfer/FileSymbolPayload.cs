using System.Buffers.Binary;

namespace PdnQso.Link.Transfer;

/// <summary>
/// The body of a <see cref="LinkFrameType.FileSymbol"/> frame: a symbol index and the symbol.
/// </summary>
/// <remarks>
/// Four bytes of header and nothing else, which is the whole point of a fountain code. The
/// degree and the neighbour set are not on the wire: both ends regenerate them from the index
/// and the seed in the offer (<see cref="Fountain.LtSymbolLayout"/>), so a symbol costs the
/// same to describe whether it combines one block or four hundred.
/// </remarks>
public static class FileSymbolPayload
{
    /// <summary>Bytes before the symbol itself.</summary>
    public const int HeaderLength = 4;

    /// <summary>Writes the header into the front of a frame body.</summary>
    /// <param name="destination">At least <see cref="HeaderLength"/> bytes; the symbol goes
    /// after it.</param>
    /// <param name="index">The symbol index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public static void WriteHeader(Span<byte> destination, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        BinaryPrimitives.WriteInt32BigEndian(destination, index);
    }

    /// <summary>Splits a frame body into its index and its symbol.</summary>
    /// <param name="payload">The frame's payload, after the type and session bytes.</param>
    /// <param name="index">The symbol index.</param>
    /// <param name="symbol">The symbol's bytes.</param>
    /// <returns><see langword="false"/> when the body is too short to be a symbol at all, or
    /// carries a negative index.</returns>
    public static bool TryRead(ReadOnlySpan<byte> payload, out int index, out ReadOnlySpan<byte> symbol)
    {
        if (payload.Length <= HeaderLength)
        {
            index = 0;
            symbol = default;
            return false;
        }

        index = BinaryPrimitives.ReadInt32BigEndian(payload);
        symbol = payload[HeaderLength..];
        return index >= 0;
    }
}
