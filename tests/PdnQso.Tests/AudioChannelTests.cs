using PdnQso.Link.Audio;

namespace PdnQso.Tests;

/// <summary>
/// The channel between two stations: noise at a stated SNR in 3 kHz, a delay, and holes.
/// </summary>
public class AudioChannelTests
{
    private const int Rate = 48000;

    /// <summary>A 1800 Hz tone at unit amplitude - mean power 0.5, easy to check against.</summary>
    private static float[] Tone(int samples)
    {
        var tone = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            tone[i] = (float)Math.Sin(2 * Math.PI * 1800 * i / Rate);
        }

        return tone;
    }

    [Fact]
    public void A_Clean_Channel_Passes_The_Burst_Through_Untouched()
    {
        float[] tone = Tone(4800);

        float[] through = new AudioChannel { TailSamples = 0 }.Apply(tone, Rate);

        through.Should().Equal(tone);
    }

    [Fact]
    public void The_Noise_Is_Calibrated_To_The_Stated_Snr_In_Three_Kilohertz()
    {
        float[] tone = Tone(Rate);
        var channel = new AudioChannel { SnrDb = 10, DelaySamples = 0, TailSamples = 0 };

        float[] through = channel.Apply(tone, Rate);

        // Recover the noise by subtracting the known signal, then convert its full-band power
        // to the 3 kHz slice the figure is quoted in.
        double noisePower = 0;
        for (int i = 0; i < tone.Length; i++)
        {
            double n = through[i] - tone[i];
            noisePower += n * n;
        }

        noisePower /= tone.Length;
        double noisePower3k = noisePower * 3000.0 / (Rate / 2.0);
        double measured = 10 * Math.Log10(0.5 / noisePower3k);

        measured.Should().BeApproximately(10, 0.5);
    }

    [Fact]
    public void The_Same_Seed_Gives_The_Same_Noise()
    {
        float[] tone = Tone(2400);
        var channel = new AudioChannel { SnrDb = 3, Seed = 4242 };

        channel.Apply(tone, Rate).Should().Equal(channel.Apply(tone, Rate));
    }

    [Fact]
    public void A_Different_Seed_Gives_Different_Noise()
    {
        float[] tone = Tone(2400);

        float[] one = new AudioChannel { SnrDb = 3, Seed = 1 }.Apply(tone, Rate);
        float[] two = new AudioChannel { SnrDb = 3, Seed = 2 }.Apply(tone, Rate);

        one.Should().NotEqual(two);
    }

    [Fact]
    public void The_Delay_Is_Lead_In_Before_The_Burst_And_The_Tail_Comes_After_It()
    {
        float[] tone = Tone(1000);
        var channel = new AudioChannel { DelaySamples = 250, TailSamples = 100 };

        float[] through = channel.Apply(tone, Rate);

        through.Length.Should().Be(250 + 1000 + 100);
        through.AsSpan(0, 250).ToArray().Should().AllSatisfy(s => s.Should().Be(0));
        through.AsSpan(250, 1000).ToArray().Should().Equal(tone);
        through.AsSpan(1250, 100).ToArray().Should().AllSatisfy(s => s.Should().Be(0));
    }

    [Fact]
    public void A_Dropout_Leaves_Nothing_At_All_In_Its_Window()
    {
        float[] tone = Tone(1000);
        var channel = new AudioChannel
        {
            SnrDb = 20,
            TailSamples = 0,
            Dropouts = [new SampleRange(200, 300)],
        };

        float[] through = channel.Apply(tone, Rate);

        through.AsSpan(200, 300).ToArray().Should().AllSatisfy(s => s.Should().Be(0));
        through.AsSpan(500, 500).ToArray().Should().Contain(s => s != 0);
    }

    [Fact]
    public void A_Dropout_Off_The_End_Of_The_Burst_Is_Clamped_Rather_Than_Throwing()
    {
        float[] tone = Tone(100);
        var channel = new AudioChannel { TailSamples = 0, Dropouts = [new SampleRange(50, 10_000)] };

        Action apply = () => channel.Apply(tone, Rate);

        apply.Should().NotThrow();
    }

    [Fact]
    public void Padding_Does_Not_Dilute_The_Stated_Snr()
    {
        // Signal power is measured over the burst alone; a long noise-only tail would drag the
        // mean down and quietly hand the receiver a better SNR than the test asked for.
        float[] tone = Tone(4800);
        var shortTail = new AudioChannel { SnrDb = 6, TailSamples = 0, Seed = 7 };
        var longTail = new AudioChannel { SnrDb = 6, TailSamples = 48000, Seed = 7 };

        shortTail.NoiseSigma(tone, Rate).Should().Be(longTail.NoiseSigma(tone, Rate));
    }

    [Fact]
    public void A_Noiseless_Channel_Adds_No_Noise()
    {
        AudioChannel.Clean.NoiseSigma(Tone(1000), Rate).Should().Be(0);
    }
}
