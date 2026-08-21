namespace PdnQso.Link.Transfer;

/// <summary>
/// CRC-32/ISO-HDLC, the one that closes a file transfer: the receiver has decoded something
/// the right length, and this says whether it is the right something.
/// </summary>
/// <remarks>
/// <para>
/// The ordinary reflected CRC-32 (polynomial 0x04C11DB7, reflected 0xEDB88320, initial and
/// final value 0xFFFFFFFF) that zip, gzip, PNG and Ethernet use; check value 0xCBF43926 for
/// the nine bytes "123456789". Written here rather than taken from a package because the whole
/// of it is thirty lines and the alternative is a new dependency in a repository whose licence
/// rules make every dependency a decision.
/// </para>
/// <para>
/// This is not the frame check the modem does. Every frame that reaches the link layer has
/// already passed IL2P's own CRC-16, so a corrupt symbol is normally an absent symbol. This
/// CRC catches what that cannot: the wrong file, a truncated one, or a decode that finished on
/// symbols that were individually valid and collectively wrong.
/// </para>
/// </remarks>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320;

    private static readonly uint[] Table = BuildTable();

    /// <summary>The CRC-32 of some bytes.</summary>
    /// <param name="data">The bytes to check.</param>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
