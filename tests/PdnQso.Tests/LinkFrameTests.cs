using Packet.SoundModem.Waterfall;
using PdnQso.Link;

namespace PdnQso.Tests;

/// <summary>
/// The wire format of docs/design.md section 3: an ordinary AX.25 UI frame to QSO, with a type
/// and a session byte at the front of the information field.
/// </summary>
public class LinkFrameTests
{
    /// <summary>
    /// The house test frame from pdn-soundmodem's own corpus - a real AX.25 UI frame,
    /// KK4HEJ-7 to KA2DEW-2, control 0x03 PID 0xF0, the IL2P specification's example. It turns
    /// up in a dozen of that repo's tests; here it stands for "traffic that is not ours".
    /// </summary>
    private static readonly byte[] SomebodyElsesUiFrame =
    [
        0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
        0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F,
        0x03, 0xF0,
        (byte)'T', (byte)'e', (byte)'s', (byte)'t',
    ];

    [Fact]
    public void A_Chat_Frame_Round_Trips_Through_Its_Ax25_Encoding()
    {
        byte[] text = "hello from the bench"u8.ToArray();
        var sent = new LinkFrame("M0LTE-7", LinkFrameType.Chat, 0x2A, text);

        byte[] wire = sent.Encode();
        LinkFrame.TryDecode(wire, out LinkFrame? heard).Should().BeTrue();

        heard!.Source.Should().Be("M0LTE-7");
        heard.Destination.Should().Be("QSO");
        heard.Type.Should().Be(LinkFrameType.Chat);
        heard.Session.Should().Be(0x2A);
        heard.Payload.ToArray().Should().Equal(text);
    }

    [Fact]
    public void The_Encoding_Is_A_Ui_Frame_To_Qso_With_Pid_F0()
    {
        byte[] wire = new LinkFrame("G0OLD", LinkFrameType.Hello, 0).Encode();

        wire.Length.Should().Be(LinkFrame.HeaderLength + LinkFrame.InfoHeaderLength);
        wire[14].Should().Be(0x03, "an unnumbered information frame");
        wire[15].Should().Be(0xF0, "no layer 3 protocol");
        wire[16].Should().Be((byte)LinkFrameType.Hello);

        // The library's own address parser has to be able to read it, because that is what the
        // daemon's frame log and every waterfall attribute frames with.
        Ax25AddressParser.TryParse(wire, out string source, out string destination).Should().BeTrue();
        source.Should().Be("G0OLD");
        destination.Should().Be("QSO");
    }

    [Fact]
    public void An_Ssid_Of_Zero_Is_Spelled_Without_The_Suffix()
    {
        LinkFrame frame = LinkFrame.Decode(new LinkFrame("M0LTE-0", LinkFrameType.Hello, 1).Encode());

        frame.Source.Should().Be("M0LTE");
    }

    [Fact]
    public void A_Lower_Case_Callsign_Is_Filed_Upper_Case()
    {
        new LinkFrame("m0lte-3", LinkFrameType.Hello, 0).Source.Should().Be("M0LTE-3");
    }

    [Fact]
    public void Every_Frame_Type_Survives_The_Round_Trip()
    {
        foreach (LinkFrameType type in Enum.GetValues<LinkFrameType>())
        {
            LinkFrame frame = LinkFrame.Decode(new LinkFrame("M0LTE", type, 9, [1, 2, 3]).Encode());
            frame.Type.Should().Be(type);
            frame.Payload.ToArray().Should().Equal([1, 2, 3]);
        }
    }

    [Fact]
    public void Somebody_Elses_Ax25_Ui_Frame_Decodes_As_Not_Ours_Rather_Than_Throwing()
    {
        // The point of the whole design: this tool shares a channel, and a node's traffic must
        // be a "no" and never an exception - Monitor still has to show it.
        LinkFrame.TryDecode(SomebodyElsesUiFrame, out LinkFrame? frame).Should().BeFalse();
        frame.Should().BeNull();

        // And the library still reads it, so Monitor can name the stations on it.
        Ax25AddressParser.TryParse(SomebodyElsesUiFrame, out string source, out string destination)
            .Should().BeTrue();
        source.Should().Be("KK4HEJ-7");
        destination.Should().Be("KA2DEW-2");
    }

    [Fact]
    public void Decode_Says_What_Was_Wrong_When_A_Frame_Is_Not_Ours()
    {
        Action decode = () => LinkFrame.Decode(SomebodyElsesUiFrame);

        decode.Should().Throw<FormatException>().WithMessage("*not a pdn-qso link frame*");
    }

    public static TheoryData<string, byte[]> MalformedFrames()
    {
        byte[] good = new LinkFrame("M0LTE", LinkFrameType.Chat, 1, [7]).Encode();

        byte[] shortOfTheInfoField = good[..17];

        byte[] wrongControl = (byte[])good.Clone();
        wrongControl[14] = 0x00;

        byte[] wrongPid = (byte[])good.Clone();
        wrongPid[15] = 0xCF;

        byte[] unknownType = (byte[])good.Clone();
        unknownType[16] = 0x55;

        byte[] wrongDestination = new LinkFrame("M0LTE", LinkFrameType.Chat, 1, [7], "GB7RDG").Encode();

        // End-of-address clear on the source address: there is a digipeater path behind it, so
        // byte 16 is not the information field at all.
        byte[] digipeated = (byte[])good.Clone();
        digipeated[13] &= 0xFE;

        // End-of-address set on the destination: the source address would be the control byte.
        byte[] destinationEndsTheField = (byte[])good.Clone();
        destinationEndsTheField[6] |= 0x01;

        // A callsign byte with its extension bit set is not a shifted AX.25 address.
        byte[] extensionBitInTheCallsign = (byte[])good.Clone();
        extensionBitInTheCallsign[8] |= 0x01;

        // A space in the middle of a callsign: padding may only trail.
        byte[] embeddedPadding = (byte[])good.Clone();
        embeddedPadding[8] = (byte)(' ' << 1);

        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "one byte short of an information field", shortOfTheInfoField },
            { "control is not UI", wrongControl },
            { "PID is not 0xF0", wrongPid },
            { "the type byte is not one of ours", unknownType },
            { "addressed to somebody else", wrongDestination },
            { "there is a digipeater path", digipeated },
            { "the destination ends the address field", destinationEndsTheField },
            { "an extension bit is set inside a callsign", extensionBitInTheCallsign },
            { "a callsign has a space in the middle", embeddedPadding },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedFrames))]
    public void Malformed_Input_Is_Refused(string why, byte[] frame)
    {
        LinkFrame.TryDecode(frame, out LinkFrame? decoded).Should().BeFalse(why);
        decoded.Should().BeNull(why);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOOLONGCALL")]
    [InlineData("M0LTE-16")]
    [InlineData("M0LTE-")]
    [InlineData("M0/LTE")]
    public void A_Callsign_That_Is_Not_A_Callsign_Is_Refused_At_Construction(string callsign)
    {
        Action build = () => new LinkFrame(callsign, LinkFrameType.Hello, 0);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_Undefined_Type_Cannot_Be_Constructed()
    {
        Action build = () => new LinkFrame("M0LTE", (LinkFrameType)0x55, 0);

        build.Should().Throw<ArgumentException>().WithMessage("*0x55*");
    }
}
