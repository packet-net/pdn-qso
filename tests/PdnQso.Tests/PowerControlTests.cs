using PdnQso.Link.Devices;

namespace PdnQso.Tests;

/// <summary>
/// The power interface of docs/design.md section 4a. The Flex and ALSA-mixer implementations
/// arrive in phase A2; what is pinned here is the contract they will have to keep.
/// </summary>
public class PowerControlTests
{
    [Fact]
    public void A_Device_With_No_Power_Control_Says_So_Rather_Than_Pretending()
    {
        IPowerControl power = NoPowerControl.Instance;

        power.Unit.Should().Be(PowerUnit.None);
        power.CanSet.Should().BeFalse();
        power.Maximum.Should().BeNull();
    }

    [Fact]
    public async Task Reading_A_Device_With_No_Power_Control_Gives_A_Printable_Nothing()
    {
        PowerReading reading = await NoPowerControl.Instance.ReadAsync();

        reading.Unit.Should().Be(PowerUnit.None);
        reading.Measured.Should().BeNull();
        reading.Display.Should().Be("n/a");
    }

    [Fact]
    public async Task Setting_Power_On_A_Device_That_Has_None_Throws_Rather_Than_Doing_Nothing()
    {
        Func<Task> set = async () => await NoPowerControl.Instance.SetAsync(10);

        await set.Should().ThrowAsync<NotSupportedException>().WithMessage("*no power control*");
    }
}
