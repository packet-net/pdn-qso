using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;
using PdnQso.Link;
using PdnQso.Ui;

namespace PdnQso.Tests;

/// <summary>
/// The Monitor pane's line, as a pure function. The pane itself is a scrolling list of these
/// and has nothing in it worth a test; what is worth pinning is that a frame from somebody
/// else's node still shows who sent it, that a chased-erasure decode says what it cost, and
/// that a payload full of binary cannot scribble escape sequences across a terminal.
/// </summary>
public class MonitorLineTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 21, 19, 42, 7, TimeSpan.Zero);

    private static FrameQuality Quality(
        double? snr = 12.4,
        double? offset = -3,
        int? erased = 0,
        int? chased = 0,
        bool? crc = true,
        bool plain = false,
        bool monitorOnly = false) =>
        new(
            "bpsk300", 32, CorrectedBytes: 0, CrcValid: crc, FrequencyOffsetHz: offset,
            PlainIl2p: plain, MonitorOnly: monitorOnly, ErasedBytes: erased, ChasedBits: chased,
            SnrDb: snr);

    private static byte[] OurFrame(string source, LinkFrameType type, string text) =>
        new LinkFrame(source, type, 0x2A, System.Text.Encoding.UTF8.GetBytes(text)).Encode();

    [Fact]
    public void One_Of_Our_Frames_Shows_Its_Callsigns_Its_Type_And_Its_Text()
    {
        string line = MonitorLine.Format(
            At, OurFrame("M0LTE-7", LinkFrameType.Chat, "good evening"), Quality());

        line.Should().Contain("M0LTE-7");
        line.Should().Contain("QSO");
        line.Should().Contain("CHAT");
        line.Should().Contain("12.4");
        line.Should().EndWith("good evening", "the payload is the last column");
    }

    [Fact]
    public void Somebody_Elses_Traffic_Still_Shows_Who_Sent_It()
    {
        // A node's beacon: not addressed to QSO and not one of our types, so it decodes as
        // nobody's link frame. On a shared channel most of what is heard is like this, and the
        // whole point of the pane is to show it.
        byte[] beacon = Ax25UiFrame.Build("GB7RDG-1", "BEACON", "net node"u8.ToArray());

        string line = MonitorLine.Format(At, beacon, Quality());

        line.Should().Contain("GB7RDG-1");
        line.Should().Contain("BEACON");
        line.Should().Contain("net node");
    }

    [Fact]
    public void A_Decode_That_Took_Work_Says_How_Much()
    {
        string line = MonitorLine.Format(
            At, OurFrame("M0LTE", LinkFrameType.Hello, "hi"), Quality(erased: 4, chased: 11));

        // The erased-byte and chased-bit columns, in that order.
        line.Should().MatchRegex(@"\s4\s+11\s");
    }

    [Fact]
    public void A_Frame_Whose_Crc_Did_Not_Verify_Is_Marked()
    {
        MonitorLine.Flags(Quality(crc: false)).Should().Be("BAD");
    }

    [Fact]
    public void A_Frame_Read_Only_By_Reed_Solomon_Is_Marked_As_Such()
    {
        MonitorLine.Flags(Quality(crc: null, plain: true)).Should().Be("RS");
        MonitorLine.Flags(Quality(crc: null, monitorOnly: true)).Should().Be("RS");
        MonitorLine.Flags(Quality()).Should().BeEmpty("a clean decode needs no adjective");
    }

    [Fact]
    public void A_Decode_That_Established_Nothing_Numeric_Shows_Dashes_Rather_Than_Zeroes()
    {
        string line = MonitorLine.Format(
            At,
            OurFrame("M0LTE", LinkFrameType.Hello, ""),
            Quality(snr: null, offset: null, erased: null, chased: null));

        line.Should().Contain("-");
        line.Should().NotContain("0.0", "a missing SNR is not an SNR of zero");
    }

    [Fact]
    public void Binary_In_A_Payload_Cannot_Reach_The_Terminal()
    {
        // An ANSI clear-screen sequence and a bell, which is what a hostile or simply binary
        // payload looks like on the way to a terminal that would obey them.
        byte[] payload = [0x1B, (byte)'[', (byte)'2', (byte)'J', 0x07, 0xFF];
        byte[] frame = new LinkFrame("M0LTE", LinkFrameType.FileSymbol, 1, payload).Encode();

        string line = MonitorLine.Format(At, frame, Quality());

        line.ToCharArray().Should().OnlyContain(c => c >= 0x20 && c <= 0x7E);
        line.Should().EndWith(".[2J..", "escape, bell and 0xFF become dots; printable bytes stay");
    }

    [Fact]
    public void The_Hex_View_Shows_The_Bytes_Instead_Of_The_Text()
    {
        byte[] frame = new LinkFrame("M0LTE", LinkFrameType.Chat, 1, [0xDE, 0xAD, 0xBE, 0xEF]).Encode();

        string line = MonitorLine.Format(At, frame, Quality(), PayloadView.Hex);

        line.Should().EndWith("DE AD BE EF");
    }

    [Fact]
    public void A_Long_Payload_Is_Cut_Short_Rather_Than_Wrapping_The_Pane()
    {
        byte[] frame = new LinkFrame(
            "M0LTE", LinkFrameType.Chat, 1, System.Text.Encoding.ASCII.GetBytes(new string('x', 400)))
            .Encode();

        string line = MonitorLine.Format(At, frame, Quality(), PayloadView.Text, payloadWidth: 20);

        line.Should().EndWith("xxxxxxxxxxxxxxxxxxxx...");
    }

    [Fact]
    public void An_Outgoing_Frame_Is_Marked_So_Nobody_Wonders_If_They_Are_Hearing_Themselves()
    {
        string ours = MonitorLine.Format(
            At, OurFrame("M0LTE", LinkFrameType.Hello, "hi"), Quality(), outgoing: true);
        string theirs = MonitorLine.Format(
            At, OurFrame("M0LTE", LinkFrameType.Hello, "hi"), Quality(), outgoing: false);

        ours.Should().Contain(">");
        ours.Should().NotBe(theirs);
    }

    [Fact]
    public void The_Columns_Line_Up_Under_The_Header()
    {
        string line = MonitorLine.Format(At, OurFrame("M0LTE-7", LinkFrameType.Chat, "x"), Quality());

        // Not a character-for-character claim about the header, which would break on every
        // tweak; what matters is that a line reaches the payload column it sits under, so
        // nothing has silently overflowed.
        line.Length.Should().BeGreaterThanOrEqualTo(MonitorLine.Header.Length - 7);
        MonitorLine.Header.Should().Contain("payload").And.Contain("snr");
    }

    [Fact]
    public void A_Frame_Too_Short_To_Be_A_Frame_Renders_Rather_Than_Throwing()
    {
        Action render = () => MonitorLine.Format(At, [0x01, 0x02], Quality());

        render.Should().NotThrow();
    }
}
