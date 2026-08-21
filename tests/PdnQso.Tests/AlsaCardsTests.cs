using PdnQso.Config;

namespace PdnQso.Tests;

/// <summary>
/// The sound cards the first-run wizard lists, read out of <c>/proc/asound/cards</c>.
/// </summary>
/// <remarks>
/// The sample is a real one from a box with the motherboard's audio and a CM108 widget on it,
/// which is exactly the pair this tool meets: the operator wants the second one, and the
/// suggested device string has to name it in a way that survives a reboot.
/// </remarks>
public class AlsaCardsTests
{
    private const string ProcCards =
        """
         0 [PCH            ]: HDA-Intel - HDA Intel PCH
                              HDA Intel PCH at 0xf7f10000 irq 33
         1 [Device         ]: USB-Audio - USB PnP Sound Device
                              C-Media Electronics Inc. USB PnP Sound Device at usb-0000:00:14.0-1, full speed

        """;

    [Fact]
    public void Every_Card_The_Kernel_Lists_Is_Read()
    {
        IReadOnlyList<AlsaCard> cards = AlsaCards.Parse(ProcCards);

        cards.Should().HaveCount(2);
        cards[0].Should().Be(new AlsaCard(0, "PCH", "HDA-Intel", "HDA Intel PCH"));
        cards[1].Should().Be(new AlsaCard(1, "Device", "USB-Audio", "USB PnP Sound Device"));
    }

    [Fact]
    public void The_Suggested_Device_Names_The_Card_By_Id_So_It_Survives_A_Reboot()
    {
        AlsaCard card = AlsaCards.Parse(ProcCards)[1];

        card.DeviceString.Should().Be("plughw:CARD=Device,DEV=0");
    }

    [Fact]
    public void A_Card_Reads_As_One_Line_An_Operator_Can_Recognise()
    {
        AlsaCards.Parse(ProcCards)[1].ToString()
            .Should().Be("1: USB PnP Sound Device [Device] (USB-Audio)");
    }

    [Fact]
    public void A_Machine_With_No_Sound_At_All_Has_No_Cards_Rather_Than_An_Error()
    {
        AlsaCards.Parse("--- no soundcards ---\n").Should().BeEmpty();
        AlsaCards.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Listing_This_Machine_Does_Not_Throw_Whatever_It_Has()
    {
        Action list = () => AlsaCards.List();

        list.Should().NotThrow();
    }
}
