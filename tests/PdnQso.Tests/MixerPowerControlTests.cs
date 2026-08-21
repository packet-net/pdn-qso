using PdnQso.Link.Devices;

namespace PdnQso.Tests;

/// <summary>
/// The sound-card half of design.md section 4a: the card's playback volume as transmit power,
/// in per cent of the control's range with the dB the card reports beside it.
/// </summary>
/// <remarks>
/// Driven through a fake control rather than a card, because the machine this is developed and
/// tested on has neither a sound card nor libasound's mixer. What is being pinned is the
/// arithmetic, the refusal, and the wording the operator reads - the P/Invoke in
/// <c>AlsaSimpleMixer</c> is the part no test here can reach.
/// </remarks>
public class MixerPowerControlTests
{
    /// <summary>A playback control with a range, and optionally a dB scale.</summary>
    private sealed class FakeMixer : IMixerDevice
    {
        private readonly Func<long, double?>? _decibels;

        public FakeMixer(long minimum = 0, long maximum = 100, Func<long, double?>? decibels = null)
        {
            Minimum = minimum;
            Maximum = maximum;
            Volume = minimum;
            _decibels = decibels;
        }

        public string Card => "hw:1";

        public string ElementName => "Speaker";

        public long Minimum { get; }

        public long Maximum { get; }

        public long Volume { get; set; }

        public double? Decibels => _decibels?.Invoke(Volume);

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void The_Control_Reports_Per_Cent_Of_The_Cards_Own_Range()
    {
        var mixer = new FakeMixer(minimum: 0, maximum: 255);
        using var power = new MixerPowerControl(mixer);

        power.Unit.Should().Be(PowerUnit.Percent);
        power.CanSet.Should().BeTrue();
        power.Maximum.Should().Be(100);
        power.ElementName.Should().Be("Speaker", "the control is discovered, not assumed");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 128)]
    [InlineData(73, 186)]
    [InlineData(100, 255)]
    public async Task Setting_A_Percentage_Lands_On_The_Cards_Raw_Value(double percent, long raw)
    {
        var mixer = new FakeMixer(minimum: 0, maximum: 255);
        using var power = new MixerPowerControl(mixer);

        await power.SetAsync(percent);

        mixer.Volume.Should().Be(raw);
    }

    [Fact]
    public async Task A_Card_Whose_Range_Does_Not_Start_At_Zero_Still_Reads_Back_What_Was_Set()
    {
        // Plenty of cards report a range like -10239..400 in hundredths of a dB.
        var mixer = new FakeMixer(minimum: -10239, maximum: 400);
        using var power = new MixerPowerControl(mixer);

        await power.SetAsync(40);
        PowerReading reading = await power.ReadAsync();

        reading.Setting.Should().BeApproximately(40, 0.01);
    }

    [Fact]
    public async Task The_Reading_Shows_The_Percentage_And_The_dB_The_Card_Claims()
    {
        var mixer = new FakeMixer(minimum: 0, maximum: 100, decibels: v => (v - 100) * 0.35);
        using var power = new MixerPowerControl(mixer);

        await power.SetAsync(73);
        PowerReading reading = await power.ReadAsync();

        reading.Unit.Should().Be(PowerUnit.Percent);
        reading.Setting.Should().Be(73);
        reading.Measured.Should().NotBeNull().And.BeApproximately(-9.45, 0.01);
        reading.Display.Should().Be("73 % (-9.5 dB)");
    }

    [Fact]
    public async Task A_Card_With_No_dB_Scale_Shows_The_Percentage_Alone_Rather_Than_A_Made_Up_Figure()
    {
        var mixer = new FakeMixer(minimum: 0, maximum: 31);
        using var power = new MixerPowerControl(mixer);

        await power.SetAsync(50);
        PowerReading reading = await power.ReadAsync();

        reading.Measured.Should().BeNull();
        reading.Display.Should().Be("52 %", "16 of the card's 31 steps, and it is not asked to lie about dB");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task A_Setting_Outside_The_Range_Is_Refused_Not_Clamped(double percent)
    {
        var mixer = new FakeMixer();
        using var power = new MixerPowerControl(mixer);
        await power.SetAsync(20);

        Func<Task> outside = async () => await power.SetAsync(percent);

        await outside.Should().ThrowAsync<ArgumentOutOfRangeException>();
        mixer.Volume.Should().Be(20, "the control was left exactly where it was");
    }

    [Fact]
    public void A_Control_With_No_Range_Is_Refused_At_Construction()
    {
        var mixer = new FakeMixer(minimum: 40, maximum: 40);

        Action build = () => _ = new MixerPowerControl(mixer);

        build.Should().Throw<ArgumentException>().WithMessage("*not a range*");
    }

    [Fact]
    public void Disposing_The_Power_Control_Closes_The_Mixer()
    {
        var mixer = new FakeMixer();
        var power = new MixerPowerControl(mixer);

        power.Dispose();

        mixer.Disposed.Should().BeTrue();
    }

    [Fact]
    public void The_Playback_Control_Is_The_First_One_The_Card_Offers_With_A_Volume()
    {
        MixerElementInfo? chosen = MixerElement.Choose(
        [
            new MixerElementInfo("Mic", 0, HasPlaybackVolume: false),
            new MixerElementInfo("Speaker", 0, HasPlaybackVolume: true),
            new MixerElementInfo("PCM", 0, HasPlaybackVolume: true),
        ]);

        chosen.Should().NotBeNull();
        chosen!.Value.Name.Should().Be("Speaker", "the card's order is the card's opinion of which comes first");
    }

    [Fact]
    public void A_Card_With_Nothing_To_Set_Says_So_Rather_Than_Picking_A_Capture_Control()
    {
        MixerElementInfo? chosen = MixerElement.Choose(
            [new MixerElementInfo("Mic", 0, HasPlaybackVolume: false)]);

        chosen.Should().BeNull();
    }

    [Theory]
    [InlineData("plughw:1,0", "hw:1")]
    [InlineData("alsa:plughw:1,0", "hw:1")]
    [InlineData("hw:2,0", "hw:2")]
    [InlineData("hw:CARD=Device,DEV=0", "hw:CARD=Device")]
    [InlineData("plughw:CARD=Device", "hw:CARD=Device")]
    [InlineData("sysdefault:CARD=Device", "hw:CARD=Device")]
    [InlineData("default", "default")]
    [InlineData("alsa:default", "default")]
    [InlineData("myrig", "myrig")]
    public void A_Pcm_Device_Name_Maps_To_The_Card_Its_Controls_Are_On(string device, string card) =>
        MixerCard.ForDevice(device).Should().Be(card);
}
