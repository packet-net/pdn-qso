using M0LTE.Tait.Ccdi;
using PdnQso.Link;
using PdnQso.Link.Tait;
using PdnQso.Tests.Time;

namespace PdnQso.Tests.Tait;

/// <summary>
/// AX.25 frames over a Tait radio's Short Data Messages, against a fake radio: what reaches
/// the radio, what comes back up, and how long a send takes.
/// </summary>
public class TaitSdmLinkTests
{
    private static TaitSettings Settings(string destination = TaitSettings.AllRadios) =>
        new() { PortName = "/dev/ttyUSB0", SdmDestination = destination };

    [Fact]
    public async Task A_Frame_Goes_To_The_Radio_As_A_Binary_Sdm_To_Every_Radio()
    {
        var clock = new VirtualClock();
        var radio = new FakeSdmRadio();
        await using var link = new TaitSdmLink(radio, Settings(), clock);
        byte[] frame = new LinkFrame("M0LTE", LinkFrameType.Chat, 1, "hello"u8).Encode();

        await VirtualTime.RunAsync(clock, link.SendAsync(frame));

        radio.Sent.Should().ContainSingle();
        radio.Sent[0].Destination.Should().Be("********");
        radio.Sent[0].Message.Should().NotContain([0x0A, 0x0D, 0x11, 0x13]);
        SdmFrameCodec.TryDecode(radio.Sent[0].Message, out byte[] carried).Should().BeTrue();
        carried.Should().Equal(frame);
    }

    [Fact]
    public async Task A_Frame_Sent_At_One_End_Arrives_Whole_At_The_Other()
    {
        var clock = new VirtualClock();
        (FakeSdmRadio a, FakeSdmRadio b) = FakeSdmRadio.Pair();
        await using var linkA = new TaitSdmLink(a, Settings(), clock);
        await using var linkB = new TaitSdmLink(b, Settings(), clock);
        var heard = new List<byte[]>();
        linkB.FrameReceived += heard.Add;
        byte[] frame = new LinkFrame("M0LTE-7", LinkFrameType.Chat, 0x0A, "\r\n\u0011\u0013"u8).Encode();

        await VirtualTime.RunAsync(clock, linkA.SendAsync(frame));

        heard.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public async Task A_Send_Ends_When_The_Frame_Is_Off_The_Air_And_Not_When_The_Radio_Took_It()
    {
        // The chat ARQ sets its patience from how long a send took. The radio answers the
        // command at once; a send that ended there would make every frame look free.
        var clock = new VirtualClock();
        await using var link = new TaitSdmLink(new FakeSdmRadio(), Settings(), clock);
        byte[] frame = new byte[100];

        await VirtualTime.RunAsync(clock, link.SendAsync(frame));

        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(400));
        clock.Elapsed.Should().BeCloseTo(link.EstimateAirTime(frame.Length), TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task An_Sdm_That_Is_Not_One_Of_Ours_Is_Not_Passed_Up_As_A_Frame()
    {
        var radio = new FakeSdmRadio();
        await using var link = new TaitSdmLink(radio, Settings(), new VirtualClock());
        var heard = new List<byte[]>();
        link.FrameReceived += heard.Add;

        radio.Receive([0x41]);

        heard.Should().BeEmpty();
    }

    [Fact]
    public async Task Busy_Is_The_Radios_Carrier_Sense_And_Unknown_Is_Clear()
    {
        var radio = new FakeSdmRadio();
        await using var link = new TaitSdmLink(radio, Settings(), new VirtualClock());

        radio.ChannelBusy = true;
        link.Busy.Should().BeTrue();
        radio.ChannelBusy = null;
        link.Busy.Should().BeFalse();
    }

    [Fact]
    public void A_Destination_That_Is_Not_Eight_Characters_Is_Refused()
    {
        Action act = () => _ = new TaitSdmLink(new FakeSdmRadio(), Settings("PDN1"), new VirtualClock());

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{TaitSdmSideChannel.IdentityLength} characters*");
    }

    [Fact]
    public async Task Disposing_The_Link_Closes_The_Radio()
    {
        var radio = new FakeSdmRadio();
        var link = new TaitSdmLink(radio, Settings(), new VirtualClock());

        await link.DisposeAsync();

        radio.Disposed.Should().BeTrue();
    }
}
