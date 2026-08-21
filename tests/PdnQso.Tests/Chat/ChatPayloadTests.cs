using PdnQso.Link.Chat;

namespace PdnQso.Tests.Chat;

/// <summary>
/// The chat frame's information field: sequence number, waveform flag, text.
/// </summary>
public class ChatPayloadTests
{
    [Fact]
    public void A_Line_Round_Trips_With_Its_Sequence_And_Waveform()
    {
        byte[] payload = ChatPayload.Encode(seq: 42, waveform: 6, "good evening, 59 here");

        ChatPayload.TryDecode(payload, out byte seq, out int? waveform, out string text)
            .Should().BeTrue();
        seq.Should().Be(42);
        waveform.Should().Be(6);
        text.Should().Be("good evening, 59 here");
    }

    [Fact]
    public void A_Station_With_No_Ladder_Reports_No_Waveform()
    {
        byte[] payload = ChatPayload.Encode(seq: 1, waveform: null, "hello");

        payload[1].Should().Be(ChatPayload.NoWaveform);
        ChatPayload.TryDecode(payload, out _, out int? waveform, out _).Should().BeTrue();
        waveform.Should().BeNull();
    }

    [Fact]
    public void An_Empty_Line_Still_Carries_Its_Header()
    {
        byte[] payload = ChatPayload.Encode(seq: 3, waveform: 8, "");

        payload.Should().HaveCount(ChatPayload.HeaderLength);
        ChatPayload.TryDecode(payload, out byte seq, out int? waveform, out string text)
            .Should().BeTrue();
        seq.Should().Be(3);
        waveform.Should().Be(8);
        text.Should().BeEmpty();
    }

    [Fact]
    public void A_Payload_Too_Short_To_Be_A_Chat_Line_Is_Refused()
    {
        ChatPayload.TryDecode([7], out _, out _, out _).Should().BeFalse();
        ChatPayload.TryDecode([], out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Control_Characters_Do_Not_Survive_The_Trip()
    {
        // A line carrying an escape sequence would otherwise be able to drive the terminal UI
        // that displays it: clear the screen, move the cursor, set a colour. Nobody types one
        // in a QSO, so it is dropped at the edge of the protocol rather than passed on.
        const byte Escape = 0x1B;
        byte[] hostile =
        [
            7, ChatPayload.NoWaveform,
            .. "hello"u8, Escape, .. "[2Jworld"u8, (byte)'\r', (byte)'\n',
        ];

        ChatPayload.TryDecode(hostile, out byte seq, out _, out string text).Should().BeTrue();
        seq.Should().Be(7);
        text.Should().Be("hello[2Jworld");
    }

    [Fact]
    public void An_Acknowledgement_Carries_Only_The_Sequence_Number()
    {
        byte[] ack = ChatPayload.EncodeAck(0xC7);

        ack.Should().Equal(0xC7);
        ChatPayload.TryDecodeAck(ack, out byte seq).Should().BeTrue();
        seq.Should().Be(0xC7);
        ChatPayload.TryDecodeAck([], out _).Should().BeFalse();
    }
}
