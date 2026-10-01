using PdnQso.Link;
using PdnQso.Link.Tait;

namespace PdnQso.Tests.Tait;

/// <summary>
/// The reshaping that gets an AX.25 frame past the four bytes a binary SDM refuses, and the
/// bound that says how big a frame always fits.
/// </summary>
public class SdmFrameCodecTests
{
    private static readonly byte[] Refused = [0x0A, 0x0D, 0x11, 0x13];

    [Fact]
    public void A_Link_Frame_Comes_Back_As_It_Went_In()
    {
        byte[] frame = new LinkFrame("M0LTE-7", LinkFrameType.Chat, 0x0D, "59 in reading\r\n"u8).Encode();

        byte[] message = SdmFrameCodec.Encode(frame);

        SdmFrameCodec.TryDecode(message, out byte[] back).Should().BeTrue();
        back.Should().Equal(frame);
    }

    [Fact]
    public void No_Refused_Byte_Ever_Reaches_The_Radio()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 2000; trial++)
        {
            byte[] frame = new byte[random.Next(1, SdmFrameCodec.MaxFrameBytes + 1)];
            random.NextBytes(frame);

            byte[] message = SdmFrameCodec.Encode(frame);

            message.Should().NotContain(Refused);
            message.Length.Should().BeLessThanOrEqualTo(SdmFrameCodec.MaxMessageBytes);
            SdmFrameCodec.TryDecode(message, out byte[] back).Should().BeTrue();
            back.Should().Equal(frame);
        }
    }

    [Theory]
    [InlineData((byte)0x0A)]
    [InlineData((byte)0x0D)]
    [InlineData((byte)0x11)]
    [InlineData((byte)0x13)]
    [InlineData((byte)0x1B)]
    [InlineData((byte)0x00)]
    public void The_Worst_Frame_Of_The_Largest_Size_Still_Fits(byte fill)
    {
        // A frame made entirely of one awkward byte is what a naive escape doubles. The key
        // moves every byte off the awkward values at once, so it costs one byte, not 125.
        byte[] frame = Enumerable.Repeat(fill, SdmFrameCodec.MaxFrameBytes).ToArray();

        byte[] message = SdmFrameCodec.Encode(frame);

        message.Length.Should().BeLessThanOrEqualTo(SdmFrameCodec.MaxMessageBytes);
        message.Should().NotContain(Refused);
        SdmFrameCodec.TryDecode(message, out byte[] back).Should().BeTrue();
        back.Should().Equal(frame);
    }

    [Fact]
    public void A_Frame_Holding_Every_Awkward_Value_In_Turn_Still_Fits()
    {
        // The adversarial case for the key: the five values that need escaping, cycled, so no
        // single key clears them all.
        byte[] awkward = [0x0A, 0x0D, 0x11, 0x13, 0x1B];
        byte[] frame = Enumerable.Range(0, SdmFrameCodec.MaxFrameBytes).Select(i => awkward[i % 5]).ToArray();

        byte[] message = SdmFrameCodec.Encode(frame);

        message.Length.Should().BeLessThanOrEqualTo(SdmFrameCodec.MaxMessageBytes);
        SdmFrameCodec.TryDecode(message, out byte[] back).Should().BeTrue();
        back.Should().Equal(frame);
    }

    [Fact]
    public void A_Frame_Too_Big_For_One_Sdm_Is_Refused_Before_It_Is_Sent()
    {
        Action act = () => SdmFrameCodec.Encode(new byte[SdmFrameCodec.MaxFrameBytes + 1]);

        act.Should().Throw<ArgumentException>().WithMessage("*126*");
    }

    [Fact]
    public void A_Message_That_Ends_Inside_An_Escape_Is_Not_A_Frame()
    {
        SdmFrameCodec.TryDecode([0x00, 0x41, 0x1B], out _).Should().BeFalse();
        SdmFrameCodec.TryDecode([0x00], out _).Should().BeFalse();
    }
}
