using PdnQso.Link;
using PdnQso.Link.Transfer;
using PdnQso.Link.Fountain;

namespace PdnQso.Tests.Transfer;

/// <summary>
/// The four frame bodies of docs/design.md section 3, and what they refuse.
/// </summary>
public class FilePayloadTests
{
    [Fact]
    public void An_Offer_Round_Trips()
    {
        var parameters = new LtParameters { C = 0.037, Delta = 0.42, Seed = 0xC0FFEE01 };
        var offer = new FileOfferPayload(
            0xDEADBEEF, "picture.jpg", 4097, 5, 1000, 0x12345678, parameters);

        byte[] wire = offer.Encode();
        FileOfferPayload.TryDecode(wire, out FileOfferPayload read).Should().BeTrue();

        read.Should().Be(offer);
        wire.Length.Should().Be(FileOfferPayload.HeaderLength + "picture.jpg".Length);
    }

    [Fact]
    public void An_Offer_Carries_The_Fountain_Parameters_Exactly()
    {
        // Not "approximately": the two ends build degree distributions from these, and a
        // distribution a hair different from the sender's decodes into a different file.
        var parameters = new LtParameters { C = 0.1, Delta = 0.5, Seed = 12345 };
        var offer = new FileOfferPayload(1, "f", 10, 1, 10, 0, parameters);

        FileOfferPayload.TryDecode(offer.Encode(), out FileOfferPayload read).Should().BeTrue();

        read.Parameters.C.Should().Be(0.1);
        read.Parameters.Delta.Should().Be(0.5);
        read.Parameters.Seed.Should().Be(12345u);
    }

    [Fact]
    public void An_Offer_Fits_In_One_Frame_At_The_Largest_Name_It_Allows()
    {
        var offer = new FileOfferPayload(
            1, new string('n', LinkCapacity.MaxNameBytes), 1000, 1, 1000, 0, new LtParameters());

        byte[] payload = offer.Encode();
        byte[] frame = new LinkFrame("M0LTE", LinkFrameType.FileOffer, 1, payload).Encode();

        frame.Length.Should().BeLessThanOrEqualTo(LinkCapacity.MaxAx25FrameBytes);
    }

    [Fact]
    public void A_Name_That_Will_Not_Fit_Is_Refused()
    {
        var offer = new FileOfferPayload(
            1, new string('n', LinkCapacity.MaxNameBytes + 1), 1000, 1, 1000, 0, new LtParameters());

        Action encode = () => offer.Encode();

        encode.Should().Throw<InvalidOperationException>().WithMessage("*at most*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(FileOfferPayload.HeaderLength - 1)]
    public void An_Offer_Too_Short_To_Be_One_Is_Refused(int length)
    {
        FileOfferPayload.TryDecode(new byte[length], out _).Should().BeFalse();
    }

    [Fact]
    public void An_Offer_Of_An_Unknown_Version_Is_Refused()
    {
        var offer = new FileOfferPayload(1, "f", 10, 1, 10, 0, new LtParameters());
        byte[] wire = offer.Encode();
        wire[0] = 99;

        FileOfferPayload.TryDecode(wire, out _).Should().BeFalse();
    }

    [Fact]
    public void An_Offer_Whose_K_Does_Not_Match_Its_Length_Is_Refused()
    {
        // The guard that stops a damaged offer talking the receiver into allocating a
        // decoder for a file that was never sent.
        var offer = new FileOfferPayload(1, "f", 1000, 1_000_000, 100, 0, new LtParameters());
        byte[] wire = offer.Encode();

        FileOfferPayload.TryDecode(wire, out _).Should().BeFalse();
    }

    [Fact]
    public void An_Offer_With_Impossible_Fountain_Parameters_Is_Refused()
    {
        var offer = new FileOfferPayload(1, "f", 10, 1, 10, 0, new LtParameters());
        byte[] wire = offer.Encode();
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(wire.AsSpan(31), 7.5);

        FileOfferPayload.TryDecode(wire, out _).Should().BeFalse("delta has to be below 1");
    }

    [Fact]
    public void A_Truncated_Name_Is_Refused()
    {
        var offer = new FileOfferPayload(1, "hello.txt", 10, 1, 10, 0, new LtParameters());
        byte[] wire = offer.Encode();

        FileOfferPayload.TryDecode(wire.AsSpan(0, wire.Length - 1), out _).Should().BeFalse();
    }

    [Fact]
    public void A_Symbol_Round_Trips()
    {
        byte[] body = new byte[FileSymbolPayload.HeaderLength + 8];
        FileSymbolPayload.WriteHeader(body, 70_000);
        "abcdefgh"u8.CopyTo(body.AsSpan(FileSymbolPayload.HeaderLength));

        FileSymbolPayload.TryRead(body, out int index, out ReadOnlySpan<byte> symbol)
            .Should().BeTrue();

        index.Should().Be(70_000);
        symbol.ToArray().Should().Equal("abcdefgh"u8.ToArray());
    }

    [Fact]
    public void A_Symbol_With_No_Symbol_In_It_Is_Refused()
    {
        FileSymbolPayload.TryRead(new byte[FileSymbolPayload.HeaderLength], out _, out _)
            .Should().BeFalse();
        FileSymbolPayload.TryRead([], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_Symbol_With_A_Negative_Index_Is_Refused()
    {
        byte[] body = [0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3];

        FileSymbolPayload.TryRead(body, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_Status_Round_Trips()
    {
        var status = new FileStatusPayload(37, 100, 52);

        byte[] wire = status.Encode();
        wire.Length.Should().Be(FileStatusPayload.Length);
        FileStatusPayload.TryDecode(wire, out FileStatusPayload read).Should().BeTrue();

        read.Should().Be(status);
        read.IsComplete.Should().BeFalse();
        new FileStatusPayload(100, 100, 140).IsComplete.Should().BeTrue();
    }

    [Fact]
    public void A_Status_Of_The_Wrong_Length_Is_Refused()
    {
        FileStatusPayload.TryDecode(new byte[FileStatusPayload.Length - 1], out _).Should().BeFalse();
        FileStatusPayload.TryDecode(new byte[FileStatusPayload.Length + 1], out _).Should().BeFalse();
    }

    [Fact]
    public void A_Done_Round_Trips()
    {
        var done = new FileDonePayload(0xABCDEF01, 143);

        byte[] wire = done.Encode();
        wire.Length.Should().Be(FileDonePayload.Length);
        FileDonePayload.TryDecode(wire, out FileDonePayload read).Should().BeTrue();

        read.Should().Be(done);
    }

    [Fact]
    public void A_Done_Of_The_Wrong_Length_Is_Refused()
    {
        FileDonePayload.TryDecode(new byte[7], out _).Should().BeFalse();
    }

    [Fact]
    public void A_Full_Size_Symbol_Fits_In_One_Frame()
    {
        byte[] body = new byte[FileSymbolPayload.HeaderLength + LinkCapacity.MaxBlockSize];
        FileSymbolPayload.WriteHeader(body, 1);

        byte[] frame = new LinkFrame("M0LTE-15", LinkFrameType.FileSymbol, 0xFF, body).Encode();

        frame.Length.Should().Be(LinkCapacity.MaxAx25FrameBytes);
    }
}
