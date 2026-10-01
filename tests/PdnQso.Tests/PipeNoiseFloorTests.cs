using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Tests.Time;

namespace PdnQso.Tests;

/// <summary>
/// What a pipe pair's capture side hands up between bursts: a quiet receiver's noise floor,
/// not digital silence, because afsk1200's carrier detect does not let go into exact zeros and
/// every reply over a pipe pair used to wait about ten seconds for it.
/// </summary>
/// <remarks>
/// Both run on a <see cref="VirtualClock"/>: the capture side's pacing is through its
/// <see cref="TimeProvider"/>, so moving the clock is what makes samples due, and nothing here
/// depends on the machine.
/// </remarks>
public sealed class PipeNoiseFloorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdn-qso-floor-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// A read of a paced input waits for its clock to owe it samples, so it is run on a thread
    /// of its own while the clock is stepped a millisecond at a time until it returns: the
    /// clock moves exactly as fast as the input needs it to and no faster.
    /// </summary>
    private static int ReadPaced(IAudioInput input, VirtualClock clock, float[] buffer)
    {
        Task<int> read = Task.Factory.StartNew(
            () => input.Read(buffer), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        while (!read.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Thread.Yield();
        }

        return read.Result;
    }

    [Fact]
    public void A_Quiet_Pipe_Sounds_Like_A_Quiet_Receiver_And_Not_Like_Digital_Silence()
    {
        var clock = new VirtualClock();
        using var input = new PipeAudioInput(Path.Combine(_dir, "in"), 12000, clock);
        float[] buffer = new float[12000];

        _ = ReadPaced(input, clock, buffer.AsSpan(0, 1).ToArray());
        clock.Advance(TimeSpan.FromSeconds(1));
        int read = input.Read(buffer);

        read.Should().BeGreaterThan(1000);
        double rms = Math.Sqrt(buffer.Take(read).Sum(s => (double)s * s) / read);
        double dbfs = 20 * Math.Log10(rms);
        dbfs.Should().BeApproximately(PipeAudioInput.NoiseFloorDbfs, 1.0);
        input.SamplesFromPipe.Should().Be(0, "nothing was written, so all of it is filled-in floor");
    }

    [Fact]
    public void After_A_Burst_Ends_Afsk1200_Lets_Go_Of_The_Channel_In_Well_Under_The_Ten_Seconds_It_Used_To_Hold()
    {
        // Built as the pipe device builds it: the pipe at 48000, resampled to and from the
        // mode's 12000. That is the arrangement that showed the ten-second hold.
        const string Mode = "afsk1200";
        const int PipeRate = 48_000;
        int rate = ModemCatalog.DspRateFor(Mode);
        var clock = new VirtualClock();
        string path = Path.Combine(_dir, "air");
        using var playback = new PipeAudioOutput(path, PipeRate);
        var output = new UpsamplingAudioOutput(playback, rate);
        using var capture = new PipeAudioInput(path, PipeRate, clock);
        var input = new DecimatingAudioInput(capture, rate);
        IModem modem = ModemCatalog.Create(Mode, rate, _ => { });
        int decoded = 0;
        modem.FrameDecoded += (_, _) => decoded++;

        float[] burst = modem.Modulate(new LinkFrame("M0LTE-7", LinkFrameType.Chat, 1, "hello"u8).Encode(), 300);
        _ = ReadPaced(capture, clock, new float[1]);

        // On a thread of its own, as a station's transmit is: at 48000 the burst is more than
        // the FIFO holds, so the write finishes only as the reads below drain it.
        Task writing = Task.Factory.StartNew(
            () => output.Write(burst), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Read as a sound card hands audio up until the frame decodes, then count how much more
        // has to be read before the channel is clear. Counted from the decode, not from the
        // write, so a writer thread that was slow to be scheduled moves the burst later and
        // not the measurement.
        float[] block = new float[rate / 50];
        ModemBusyGate gate = new(modem);
        int guard = rate * 10;
        for (int carried = 0; decoded == 0 && carried < guard;)
        {
            int read = ReadPaced(input, clock, block);
            modem.Process(block.AsSpan(0, read));
            carried += read;
        }

        decoded.Should().Be(1);
        int afterDecode = 0;
        while (gate.Busy && afterDecode < rate * 2)
        {
            int read = ReadPaced(input, clock, block);
            modem.Process(block.AsSpan(0, read));
            afterDecode += read;
        }

        // Typically a few hundred milliseconds, varying with the noise; into digital silence
        // it was about ten seconds. The bar is between the two with room either side.
        double seconds = (double)afterDecode / rate;
        seconds.Should().BeLessThan(
            1.5,
            "a quiet receiver after the frame is a clear channel, and the far end would "
            + "otherwise wait for one that is already there");
        writing.IsCompleted.Should().BeTrue("every sample written has been read");
    }
}
