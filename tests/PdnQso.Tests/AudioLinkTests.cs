using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;

namespace PdnQso.Tests;

/// <summary>
/// The two-station rig: one modem's burst through a channel into another's demodulator, with
/// no radio and no wall clock anywhere in it.
/// </summary>
/// <remarks>
/// Two modes are exercised rather than one because they are the two ends of what this tool is
/// for: <c>bpsk300</c> is the 300 baud HF packet mode the live network runs, at a 12 kHz DSP
/// rate and through a nine-branch frequency-diversity bank, and <c>ms110d-wn4</c> is a
/// MIL-STD-188-110D single-carrier waveform at 48 kHz whose transmit waveform the ARQ will
/// later be allowed to step. A rig that only ever saw one of them would be pinning an accident.
/// </remarks>
public class AudioLinkTests
{
    private static byte[] Chat(byte sequence, string text) =>
        new LinkFrame("M0LTE-7", LinkFrameType.Chat, 0x11, [sequence, .. System.Text.Encoding.UTF8.GetBytes(text)])
            .Encode();

    [Theory]
    [InlineData("bpsk300", 12000)]
    [InlineData("ms110d-wn4", 48000)]
    public void The_Link_Runs_At_The_Rate_The_Catalogue_Gives_The_Mode(string mode, int expectedRate)
    {
        using AudioLink link = AudioLink.Create(mode);

        link.SampleRate.Should().Be(expectedRate);
        link.SampleRate.Should().Be(ModemCatalog.DspRateFor(mode));
    }

    [Theory]
    [InlineData("bpsk300")]
    [InlineData("ms110d-wn4")]
    public void A_Frame_Crosses_A_Clean_Channel(string mode)
    {
        using AudioLink link = AudioLink.Create(mode);
        byte[] sent = Chat(1, "hello from the bench");

        IReadOnlyList<ReceivedFrame> heard = link.RunBurst(sent);

        heard.Should().ContainSingle();
        heard[0].Frame.Should().Equal(sent);
        heard[0].Quality.FrameBytes.Should().Be(sent.Length);
        heard[0].AsLinkFrame.Should().NotBeNull();
        heard[0].AsLinkFrame!.Source.Should().Be("M0LTE-7");
        heard[0].AsLinkFrame!.Type.Should().Be(LinkFrameType.Chat);
    }

    [Fact]
    public void A_Frame_Crosses_In_Either_Direction()
    {
        using AudioLink link = AudioLink.Create("bpsk300");

        link.RunBurst(Chat(1, "A to B"), LinkEnd.A).Should().ContainSingle();
        link.RunBurst(Chat(2, "B to A"), LinkEnd.B).Should().ContainSingle();
    }

    [Fact]
    public void The_Quality_Says_Which_Mode_Decoded_It_And_Whether_The_Crc_Checked_Out()
    {
        using AudioLink link = AudioLink.Create("bpsk300");

        FrameQuality quality = link.RunBurst(Chat(1, "quality"))[0].Quality;

        quality.Mode.Should().StartWith("bpsk300");
        quality.CrcValid.Should().BeTrue("bpsk300 runs IL2P+CRC");
        quality.CorrectedBytes.Should().Be(0, "a clean channel corrects nothing");
    }

    [Fact]
    public void At_A_Low_Snr_Some_Frames_Do_Not_Arrive()
    {
        // bpsk300 through this channel copies everything down to about -6 dB in 3 kHz and
        // nothing by -9 dB (measured, seed 99), which puts -8 dB solidly on the far side of
        // the knee. The claim under test is only that the rig can make frames fail: the
        // threshold itself is pdn-soundmodem's to defend, not this repo's.
        const int Frames = 8;
        using var clean = AudioLink.Create("bpsk300");
        using var noisy = AudioLink.Create("bpsk300", new AudioChannel { SnrDb = -8, Seed = 99 });

        int deliveredClean = 0;
        int deliveredNoisy = 0;
        for (byte i = 0; i < Frames; i++)
        {
            deliveredClean += clean.RunBurst(Chat(i, "ladder")).Count;
            deliveredNoisy += noisy.RunBurst(Chat(i, "ladder")).Count;
        }

        deliveredClean.Should().Be(Frames, "a clean channel loses nothing");
        deliveredNoisy.Should().BeLessThan(Frames, "the noise has to cost something");
    }

    [Fact]
    public void A_Dropout_Over_The_Burst_Loses_It()
    {
        using AudioLink link = AudioLink.Create("bpsk300");
        byte[] frame = Chat(1, "into the hole");

        // How long the burst is, so the hole can be put squarely over it.
        int burstSamples = link.ModemA.Modulate(frame, 300).Length;
        link.Channel = new AudioChannel { Dropouts = [new SampleRange(0, burstSamples)] };

        link.RunBurst(frame).Should().BeEmpty();
    }

    [Fact]
    public void A_Dropout_Clear_Of_The_Burst_Costs_Nothing()
    {
        using AudioLink link = AudioLink.Create("bpsk300");
        byte[] frame = Chat(1, "past the end");
        int burstSamples = link.ModemA.Modulate(frame, 300).Length;

        // In the tail, after the frame has gone by.
        link.Channel = new AudioChannel
        {
            TailSamples = 24000,
            Dropouts = [new SampleRange(burstSamples + 12000, 12000)],
        };

        link.RunBurst(frame).Should().ContainSingle();
    }

    [Fact]
    public void A_Delay_Does_Not_Cost_The_Frame()
    {
        using var link = AudioLink.Create("bpsk300", new AudioChannel { DelaySamples = 6000 });

        link.RunBurst(Chat(1, "late but present")).Should().ContainSingle();
    }

    [Fact]
    public void The_Same_Seed_Gives_The_Same_Run()
    {
        static int Run()
        {
            using var link = AudioLink.Create("bpsk300", new AudioChannel { SnrDb = -7, Seed = 1234 });
            int delivered = 0;
            for (byte i = 0; i < 6; i++)
            {
                delivered += link.RunBurst(Chat(i, "repeatable")).Count;
            }

            return delivered;
        }

        Run().Should().Be(Run());
    }

    [Fact]
    public void An_Unknown_Mode_Is_Refused_By_Name()
    {
        Action create = () => AudioLink.Create("no-such-mode");

        create.Should().Throw<ArgumentException>().WithMessage("*no-such-mode*");
    }
}
