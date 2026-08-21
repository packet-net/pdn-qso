using System.Text;
using PdnQso.Link.Transfer;

namespace PdnQso.Tests.Transfer;

/// <summary>The check value that closes a transfer, against the published ones.</summary>
public class Crc32Tests
{
    [Fact]
    public void The_Check_Value_Is_The_Published_One()
    {
        // The standard CRC catalogue's check value for CRC-32/ISO-HDLC.
        Crc32.Compute("123456789"u8).Should().Be(0xCBF43926);
    }

    [Fact]
    public void Nothing_Has_A_Crc_Of_Zero()
    {
        Crc32.Compute([]).Should().Be(0);
    }

    [Fact]
    public void One_Bit_Changes_The_Answer()
    {
        byte[] data = Encoding.ASCII.GetBytes("the quick brown fox");
        uint before = Crc32.Compute(data);
        data[7] ^= 0x01;

        Crc32.Compute(data).Should().NotBe(before);
    }

    [Fact]
    public void A_Longer_Run_Agrees_With_The_Framework()
    {
        var data = new byte[10_000];
        new Random(20260821).NextBytes(data);

        // Nothing here is a second implementation to trust; this is the zip CRC, so a stream
        // the framework can check is the honest cross-check available without a dependency.
        uint mine = Crc32.Compute(data);
        uint theirs = Reference(data);

        mine.Should().Be(theirs);
    }

    /// <summary>The bitwise definition, deliberately not the table-driven one under test.</summary>
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
