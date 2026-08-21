using M0LTE.Radio.Audio;
using PdnQso.Link.Audio;

namespace PdnQso.Tests;

/// <summary>
/// The receive side of the rate bridge: a sound card at 48 kHz feeding a modem at 12. The
/// claim being pinned is that a tone in the band arrives at its own amplitude and a tone above
/// the new Nyquist is filtered out rather than folded down on top of the modem's passband,
/// which is what a decimation with no filter in front of it would do.
/// </summary>
public class DecimatingAudioInputTests
{
    /// <summary>A capture device that hands out a sine for ever, in blocks.</summary>
    private sealed class ToneInput(int sampleRate, double toneHz, float amplitude = 0.5f) : IAudioInput
    {
        private long _n;

        public int SampleRate => sampleRate;

        public int Read(Span<float> destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = (float)(amplitude * Math.Sin(2 * Math.PI * toneHz * _n++ / sampleRate));
            }

            return destination.Length;
        }
    }

    /// <summary>
    /// A capture device that hands out a known ramp in whatever awkward block sizes it is told
    /// to, which is what a device paced to wall clock does: it returns what it owes, and that
    /// is a multiple of the decimation factor only by accident.
    /// </summary>
    private sealed class RaggedRamp(int sampleRate, IReadOnlyList<int> reads) : IAudioInput
    {
        private int _n;
        private int _read;

        public int SampleRate => sampleRate;

        public int Read(Span<float> destination)
        {
            int want = Math.Min(destination.Length, reads[_read++ % reads.Count]);
            for (int i = 0; i < want; i++)
            {
                // A ramp, so a dropped sample shows up as a step rather than hiding in a
                // periodic signal that looks much the same one sample later.
                destination[i] = _n++ / 100000f;
            }

            return want;
        }
    }

    private static float[] Drain(IAudioInput input, int samples)
    {
        var taken = new float[samples];
        int at = 0;
        while (at < samples)
        {
            int got = input.Read(taken.AsSpan(at, Math.Min(512, samples - at)));
            if (got <= 0)
            {
                break;
            }

            at += got;
        }

        return taken;
    }

    /// <summary>Peak absolute value over the settled part of a run, past the filter's ramp-up.</summary>
    private static double Peak(float[] samples, int skip)
    {
        double peak = 0;
        for (int i = skip; i < samples.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        return peak;
    }

    [Fact]
    public void A_Card_At_Four_Times_The_Modes_Rate_Presents_Itself_At_The_Modes_Rate()
    {
        using var decimating = new DecimatingAudioInput(new ToneInput(48000, 1000), 12000);

        decimating.SampleRate.Should().Be(12000);
        decimating.Factor.Should().Be(4);
    }

    [Fact]
    public void A_Tone_In_The_Band_Comes_Through_At_Its_Own_Level()
    {
        using var decimating = new DecimatingAudioInput(new ToneInput(48000, 1500, 0.5f), 12000);

        float[] audio = Drain(decimating, 6000);

        Peak(audio, skip: 200).Should().BeApproximately(0.5, 0.03);
    }

    [Fact]
    public void A_Tone_Above_The_New_Nyquist_Is_Filtered_Out_Rather_Than_Folded_Onto_The_Modem()
    {
        // 10 kHz decimated by four with no filter would alias to 2 kHz, right on top of where
        // the modems live. That is the failure this exists to prevent.
        using var decimating = new DecimatingAudioInput(new ToneInput(48000, 10000, 0.5f), 12000);

        float[] audio = Drain(decimating, 6000);

        Peak(audio, skip: 200).Should().BeLessThan(0.02, "a 10 kHz tone has no business in a 12 kHz stream");
    }

    [Fact]
    public void A_Rate_That_Is_Not_A_Whole_Multiple_Is_Refused()
    {
        Action build = () => _ = new DecimatingAudioInput(new ToneInput(44100, 1000), 12000);

        build.Should().Throw<ArgumentException>().WithMessage("*whole number*");
    }

    [Fact]
    public void A_Card_Already_At_The_Modes_Rate_Passes_Audio_Straight_Through()
    {
        using var decimating = new DecimatingAudioInput(new ToneInput(12000, 1500, 0.5f), 12000);

        decimating.Factor.Should().Be(1);
        Peak(Drain(decimating, 4000), skip: 200).Should().BeApproximately(0.5, 0.03);
    }

    [Fact]
    public void A_Part_Frame_Tail_Is_Carried_Into_The_Next_Read_Rather_Than_Dropped()
    {
        // The bug this pins cost an afternoon: a device paced to wall clock returns 4,801
        // samples, the decimator takes 4,800 whole frames and threw the last one away, and the
        // stream slipped by one sample per read for ever after. A tone still measures at its
        // own level through that, so the tone tests above stayed green while nothing could
        // decode: two copies of the program over a 48 kHz pipe pair heard silence.
        const int deviceRate = 48000;
        const int modeRate = 12000;
        const int samples = 2048;

        // The same stream twice: once in tidy whole frames, once in the ragged sizes a paced
        // device really returns. The decimator's output has to be the same either way.
        var tidy = new DecimatingAudioInput(
            new RaggedRamp(deviceRate, [1024]), modeRate, blockSamples: 512);
        var ragged = new DecimatingAudioInput(
            new RaggedRamp(deviceRate, [1023, 37, 4801, 1, 2, 3, 511]), modeRate, blockSamples: 512);

        float[] fromTidy = Drain(tidy, samples);
        float[] fromRagged = Drain(ragged, samples);

        fromRagged.Should().Equal(fromTidy);
    }
}
