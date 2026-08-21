using System.Runtime.InteropServices;

namespace PdnQso.Link.Fountain;

/// <summary>The one arithmetic operation an LT code performs.</summary>
/// <remarks>
/// Word at a time rather than byte at a time, because this is the encoder's and the decoder's
/// whole inner loop: a symbol of degree <c>d</c> costs <c>d</c> passes over a block, and a
/// decode costs one pass per edge peeled.
/// </remarks>
internal static class BlockXor
{
    /// <summary><c>destination ^= source</c>, over spans of equal length.</summary>
    internal static void Xor(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(destination);
        ReadOnlySpan<ulong> sourceWords = MemoryMarshal.Cast<byte, ulong>(source);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] ^= sourceWords[i];
        }

        for (int i = words.Length * sizeof(ulong); i < destination.Length; i++)
        {
            destination[i] ^= source[i];
        }
    }
}
