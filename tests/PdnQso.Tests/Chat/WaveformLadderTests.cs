using Packet.SoundModem.Modems;
using PdnQso.Link.Chat;

namespace PdnQso.Tests.Chat;

/// <summary>
/// The MS110D waveform lever on its own, with a modem that only records what it was told.
/// The two-station tests pin that a real modem follows it; these pin the rule.
/// </summary>
public class WaveformLadderTests
{
    [Fact]
    public void The_Default_Ladder_Is_The_One_The_Design_Fixes()
    {
        WaveformLadder.DefaultSteps.Should().Equal(8, 7, 6, 5, 4, 2);
    }

    [Fact]
    public void Stepping_Down_Walks_The_Ladder_To_Its_Floor()
    {
        var modem = new FakeWaveformModem();
        var ladder = new WaveformLadder(modem, "ms110d-wn8");
        var steps = new List<int>();
        ladder.Changed += (wn, _) => steps.Add(wn);

        while (ladder.TryStepDown("test"))
        {
        }

        steps.Should().Equal(7, 6, 5, 4, 2);
        ladder.Current.Should().Be(2);
        modem.Applied.Should().Equal(7, 6, 5, 4, 2);
        ladder.TryStepDown("test").Should().BeFalse("2 is the bottom of the ladder");
    }

    [Fact]
    public void Stepping_Up_Walks_Back_To_The_Top_And_Stops()
    {
        var modem = new FakeWaveformModem();
        var ladder = new WaveformLadder(modem, "ms110d-wn2");

        ladder.TryStepUp("test").Should().BeTrue();
        ladder.Current.Should().Be(4);
        ladder.TryStepUp("test").Should().BeTrue();
        ladder.Current.Should().Be(5);

        while (ladder.TryStepUp("test"))
        {
        }

        ladder.Current.Should().Be(8, "8 is the most capable waveform on the ladder");
        ladder.TryStepUp("test").Should().BeFalse();
    }

    [Fact]
    public void A_Waveform_The_Modem_Refuses_Is_Skipped()
    {
        var modem = new FakeWaveformModem { Refuse = [7, 6] };
        var ladder = new WaveformLadder(modem, "ms110d-wn8");

        ladder.TryStepDown("two attempts unacknowledged").Should().BeTrue();

        ladder.Current.Should().Be(5, "7 and 6 were refused, so the step lands on 5");
        modem.Applied.Should().Equal([7, 6, 5], "each was tried in turn");
        ladder.Refusals.Should().Be(2);
    }

    [Fact]
    public void A_Ladder_Whose_Every_Step_Is_Refused_Stays_Where_It_Is()
    {
        var modem = new FakeWaveformModem { Refuse = [7, 6, 5, 4, 2] };
        var ladder = new WaveformLadder(modem, "ms110d-wn8");
        var steps = new List<int>();
        ladder.Changed += (wn, _) => steps.Add(wn);

        ladder.TryStepDown("test").Should().BeFalse();

        ladder.Current.Should().Be(8);
        steps.Should().BeEmpty("nothing moved, so nothing is announced");
    }

    [Fact]
    public void A_Waveform_Off_The_Ladder_Still_Knows_Which_Way_Is_Down()
    {
        // wn13 is a Phase A waveform the ladder does not mention. A station that starts there
        // must still be able to step, or it is stranded at the first bad hour of propagation.
        var modem = new FakeWaveformModem();
        var ladder = new WaveformLadder(modem, "ms110d-wn13");

        ladder.TryStepDown("test").Should().BeTrue();
        ladder.Current.Should().Be(8);
        ladder.TryStepUp("test").Should().BeFalse("there is nothing above 8 on the ladder");
    }

    [Fact]
    public void A_Modem_With_No_Lever_Has_No_Ladder()
    {
        var ladder = new WaveformLadder(control: null, "ms110d-wn8");

        ladder.Enabled.Should().BeFalse();
        ladder.Current.Should().Be(-1);
        ladder.CurrentOrNull.Should().BeNull();
        ladder.TryStepDown("test").Should().BeFalse();
        ladder.TryStepUp("test").Should().BeFalse();
    }

    [Theory]
    [InlineData("qpsk2400")]
    [InlineData("bpsk300")]
    [InlineData("freedv-datac3")]
    [InlineData("ofdm-fm:nb")]
    [InlineData(null)]
    public void A_Modem_That_Is_Not_Ms110d_Never_Steps(string? mode)
    {
        // The lever is SETHW, whose payload means whatever the modem says it means. Another
        // modem implementing the same interface would read a waveform number as something
        // else entirely, so the mode name is checked as well as the interface.
        var modem = new FakeWaveformModem();
        var ladder = new WaveformLadder(modem, mode);

        ladder.Enabled.Should().BeFalse();
        ladder.TryStepDown("test").Should().BeFalse();
        modem.Applied.Should().BeEmpty();
    }

    [Fact]
    public void The_Waveform_Comes_Out_Of_The_Mode_Name()
    {
        WaveformLadder.TryReadWaveform("ms110d-wn13", out int wn).Should().BeTrue();
        wn.Should().Be(13);
        WaveformLadder.TryReadWaveform("ms110d-wn0", out wn).Should().BeTrue();
        wn.Should().Be(0);
        WaveformLadder.TryReadWaveform("ms110d-wnX", out _).Should().BeFalse();
        WaveformLadder.TryReadWaveform("ms110d", out _).Should().BeFalse();
        WaveformLadder.TryReadWaveform(null, out _).Should().BeFalse();
    }

    /// <summary>A modem's SETHW lever, without the modem: it records and it can say no.</summary>
    private sealed class FakeWaveformModem : IHardwareControllable
    {
        public List<int> Applied { get; } = [];

        public int[] Refuse { get; init; } = [];

        public bool TrySetHardware(ReadOnlySpan<byte> payload, out string outcome)
        {
            if (payload.Length != 1)
            {
                outcome = "the chat ARQ sends one byte, the waveform number";
                return false;
            }

            int waveform = payload[0];
            Applied.Add(waveform);
            if (Array.IndexOf(Refuse, waveform) >= 0)
            {
                outcome = $"wn{waveform} refused";
                return false;
            }

            outcome = $"ms110d-wn{waveform}, short interleaver";
            return true;
        }
    }
}
