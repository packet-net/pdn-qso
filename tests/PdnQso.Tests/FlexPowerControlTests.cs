using M0LTE.Flex;
using PdnQso.Link.Devices;
using PdnQso.Tests.Time;

namespace PdnQso.Tests;

/// <summary>
/// The Flex half of design.md section 4a: watts set through <c>rfpower</c>, the radio's own
/// forward-power meter read back beside the setting, and a station ceiling that is refused
/// rather than clamped.
/// </summary>
/// <remarks>
/// Everything here runs against <c>M0LTE.Flex</c>'s <see cref="MockFlexRadio"/>, which speaks
/// the radio's command protocol over a real socket and answers <c>transmit set rfpower=</c>
/// exactly as a radio does, including refusing a value above its <c>max_power_level</c>. Meter
/// samples are pushed through the same in-process VITA delivery pdn-soundmodem's own interlock
/// tests use.
/// </remarks>
public class FlexPowerControlTests
{
    // FWDPWR is meter id 6 in the mock's FLEX-6500 meter set, unit dBm, scaled raw/128.
    private const int ForwardPowerId = 6;

    private static short Dbm(double dbm) => (short)Math.Round(dbm * 128.0);

    private static async Task<(MockFlexRadio Mock, FlexClient Client)> RadioAsync(int maxPowerLevel)
    {
        var mock = new MockFlexRadio(
            DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless)
        {
            MaxPowerLevel = maxPowerLevel,
        };
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        mock.RxDelivery = client.DeliverVitaPacket;
        return (mock, client);
    }

    [Fact]
    public async Task Setting_The_Power_Writes_Rfpower_To_The_Radio()
    {
        (MockFlexRadio mock, FlexClient client) = await RadioAsync(maxPowerLevel: 100);
        await using (mock)
        await using (client)
        {
            using var power = new FlexPowerControl(client);

            await power.SetAsync(10);

            mock.RfPower.Should().Be(10, "100 W PA, so 10 W is level 10");
            mock.CommandLog.Should().Contain("transmit set rfpower=10");
        }
    }

    [Fact]
    public async Task The_Setting_Is_Read_Back_From_The_Radio_Not_Remembered()
    {
        (MockFlexRadio mock, FlexClient client) = await RadioAsync(maxPowerLevel: 100);
        await using (mock)
        await using (client)
        {
            using var power = new FlexPowerControl(client);
            await power.SetAsync(10);

            // Somebody else's client moves it - which on a shared radio is exactly what happens.
            await client.SendCommandAsync("transmit set rfpower=25");

            // The command is answered on the command stream and the new value arrives a moment
            // later on the status stream, so this waits for the fact rather than for a
            // duration: reading straight after the write is a race, and it is the race that
            // made this test fail about one run in five.
            PowerReading reading = await WaitForSettingAsync(power, 25);

            reading.Unit.Should().Be(PowerUnit.Watts);
            reading.Setting.Should().Be(25, "the radio's answer is what shapes the transmission");
        }
    }

    [Fact]
    public async Task A_Setting_Above_What_The_Radio_Reports_Is_Refused_Not_Clamped()
    {
        (MockFlexRadio mock, FlexClient client) = await RadioAsync(maxPowerLevel: 15);
        await using (mock)
        await using (client)
        {
            using FlexPowerControl power = await FlexPowerControl.OpenAsync(client);

            power.Maximum.Should().Be(15, "the radio reports max_power_level=15 on a 100 W PA");

            Func<Task> tooMuch = async () => await power.SetAsync(50);

            await tooMuch.Should().ThrowAsync<ArgumentOutOfRangeException>();
            mock.CommandLog.Should().NotContain(
                c => c.Contains("rfpower", StringComparison.Ordinal),
                "the radio was never asked for anything, least of all a quietly substituted 15 W");
        }
    }

    [Fact]
    public async Task A_Radio_That_Refuses_The_Write_Itself_Says_So()
    {
        (MockFlexRadio mock, FlexClient client) = await RadioAsync(maxPowerLevel: 15);
        await using (mock)
        await using (client)
        {
            // No maximum read at bring-up, so nothing is refused locally and the radio has the
            // last word. It must not look like a success.
            using var power = new FlexPowerControl(client, meters: null, maximumWatts: null);

            Func<Task> tooMuch = async () => await power.SetAsync(50);

            await tooMuch.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*rfpower=50*");
        }
    }

    [Fact]
    public async Task The_Reading_Carries_The_Measured_Watts_Beside_The_Setting()
    {
        (MockFlexRadio mock, FlexClient client) = await RadioAsync(maxPowerLevel: 100);
        await using (mock)
        await using (client)
        {
            FlexMeters meters = await FlexMeters.SubscribeAsync(client);
            using var power = new FlexPowerControl(client, meters, maximumWatts: 100);
            await power.SetAsync(10);

            // A burst: the transmitter comes up to just under 10 W and drops again.
            mock.PushMeters((ForwardPowerId, Dbm(39.5)));   // ~8.9 W
            mock.PushMeters((ForwardPowerId, Dbm(39.8)));   // ~9.5 W
            await WaitForMeasuredAsync(power);
            mock.PushMeters((ForwardPowerId, Dbm(0)));      // ~1 mW: key-down

            PowerReading reading = await WaitForMeasuredAsync(power);

            reading.Setting.Should().Be(10);
            reading.Measured.Should().NotBeNull().And.BeApproximately(9.5, 0.2);
            reading.Display.Should().Be("set 10 W, last 9.5 W");
        }
    }

    [Fact]
    public async Task With_No_Meters_The_Reading_Is_Honest_About_Having_Only_The_Setting()
    {
        (MockFlexRadio mock, FlexClient client) = await RadioAsync(maxPowerLevel: 100);
        await using (mock)
        await using (client)
        {
            using var power = new FlexPowerControl(client, meters: null, maximumWatts: 100);
            await power.SetAsync(5);

            PowerReading reading = await power.ReadAsync();

            reading.Measured.Should().BeNull();
            reading.Display.Should().Be("set 5 W");
        }
    }

    [Fact]
    public void Watts_And_The_Radios_Power_Level_Convert_Both_Ways()
    {
        FlexPowerControl.ToWatts(15).Should().Be(15, "the 6000-series PA is 100 W");
        FlexPowerControl.ToLevel(15).Should().Be(15);
        FlexPowerControl.ToLevel(0.4).Should().Be(0, "the radio takes whole numbers");
    }

    /// <summary>
    /// Reads until the radio reports <paramref name="watts"/>, or until patience runs out and
    /// the last reading is returned for the assertion to fail on honestly.
    /// </summary>
    private static async Task<PowerReading> WaitForSettingAsync(FlexPowerControl power, double watts)
    {
        // For as long as it takes, rather than a hundred goes at twenty milliseconds: the radio
        // answering is a fact, and how many turns the machine needed to get there is not part
        // of what this asserts.
        PowerReading reading = await power.ReadAsync();
        while (reading.Setting != watts)
        {
            await VirtualTime.YieldAsync();
            reading = await power.ReadAsync();
        }

        return reading;
    }

    private static async Task<PowerReading> WaitForMeasuredAsync(FlexPowerControl power)
    {
        while (true)
        {
            PowerReading reading = await power.ReadAsync();
            if (reading.Measured is not null)
            {
                return reading;
            }

            await VirtualTime.YieldAsync();
        }
    }
}
