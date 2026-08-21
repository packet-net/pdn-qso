using System.Runtime.InteropServices;
using M0LTE.Radio.Audio;

namespace PdnQso.Link.Audio;

/// <summary>
/// Two named pipes standing in for a radio: raw 32-bit float samples out of one and into the
/// other, so two copies of pdn-qso on one machine are on the same air with no hardware between
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Reimplemented from the shape of pdn-soundmodem's daemon <c>PipeAudio.cs</c> (which is
/// internal to that program, so there is nothing to reference), and it takes the same device
/// string so a FIFO pair works with either program at either end:
/// <c>pipe:&lt;in&gt;,&lt;out&gt;[,&lt;rate&gt;]</c>, with station A's reversed against
/// station B's.
/// </para>
/// <para>
/// <b>It is not a channel.</b> There is no noise, no filtering and no propagation here at all;
/// samples arrive exactly as they were written. That makes it the right tool for "can these
/// two actually hear each other" and the wrong one for any performance question, which belongs
/// to <see cref="AudioChannel"/> and, for the modems themselves, to pdn-soundmodem's own
/// Watterson masks.
/// </para>
/// </remarks>
public static class PipeAudio
{
    /// <summary>Creates <paramref name="path"/> as a FIFO if there is not one there already.</summary>
    /// <exception cref="IOException">The FIFO could not be created.</exception>
    public static void EnsureFifo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 0o666: whoever else is meant to be on the far end of this is another user's process
        // as often as not, and the containing directory is what limits who can reach it.
        if (Mkfifo(path, 0b110_110_110) != 0 && !File.Exists(path))
        {
            throw new IOException(
                $"could not create the FIFO {path} - check the directory exists and is writable");
        }
    }

    /// <summary>
    /// Opens a FIFO read-write, which is the one mode that neither blocks waiting for the other
    /// end nor reports end of file when it goes away.
    /// </summary>
    /// <remarks>
    /// Read-only would block in the constructor until somebody opened the far end, so two
    /// stations started in either order would deadlock on each other; and once a writer had
    /// come and gone, a read-only handle would report EOF for ever after. Neither is what a
    /// sound card does.
    /// </remarks>
    internal static FileStream OpenFifo(string path)
    {
        EnsureFifo(path);
        return new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0, useAsync: false);
    }

    // DllImport rather than LibraryImport: the source generator behind the newer attribute
    // emits unsafe code, and switching the whole library to unsafe to reach two syscalls is a
    // poor trade.
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Mkfifo(string path, uint mode);
}

/// <summary>
/// The capture half of a pipe pair: whatever the other station wrote, paced to wall clock,
/// with silence in the gaps.
/// </summary>
/// <remarks>
/// The silence is the point. A FIFO delivers nothing at all between transmissions, and a
/// capture device that simply blocked there would freeze the receive loop, the DCD with it,
/// and make a quiet band indistinguishable from a dead input. A sound card hands up a
/// continuous stream that happens to be silent when nothing is on, so this does too.
/// </remarks>
public sealed class PipeAudioInput : IAudioInput, IDisposable
{
    private readonly FileStream _fifo;
    private readonly TimeProvider _time;
    private readonly byte[] _bytes;
    private long _started;
    private bool _running;
    private long _delivered;

    /// <summary>Opens the capture FIFO, creating it if needed.</summary>
    /// <param name="path">The FIFO to read from.</param>
    /// <param name="sampleRate">The rate the far end is writing at.</param>
    /// <param name="timeProvider">The clock the pacing runs on.</param>
    public PipeAudioInput(string path, int sampleRate, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _fifo = PipeAudio.OpenFifo(path);
        _time = timeProvider ?? TimeProvider.System;
        SampleRate = sampleRate;
        Path = path;

        // A tenth of a second of headroom: enough that a burst is read in a few passes rather
        // than hundreds, and small enough that the read never sits on a large idle buffer.
        _bytes = new byte[Math.Max(4096, sampleRate / 10) * sizeof(float)];
    }

    /// <summary>The FIFO this reads from.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>How many samples arrived as real audio rather than as filled-in silence.</summary>
    public long SamplesFromPipe { get; private set; }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        if (!_running)
        {
            _running = true;
            _started = _time.GetTimestamp();
        }

        // Paced like a capture device: never hand back more than wall clock has had time to
        // produce, so a burst written in one go is heard over the time it really occupies.
        long due = Owed();
        while (due <= 0)
        {
            Thread.Sleep(2);
            due = Owed();
        }

        int want = (int)Math.Min(Math.Min(due, destination.Length), _bytes.Length / sizeof(float));
        int taken = Math.Min(want, AvailableSamples());
        if (taken > 0)
        {
            int wanted = taken * sizeof(float);
            int got = 0;
            while (got < wanted)
            {
                int read = _fifo.Read(_bytes, got, wanted - got);
                if (read <= 0)
                {
                    break;
                }

                got += read;
            }

            taken = got / sizeof(float);
            for (int n = 0; n < taken; n++)
            {
                destination[n] = BitConverter.ToSingle(_bytes, n * sizeof(float));
            }

            SamplesFromPipe += taken;
        }

        destination[taken..want].Clear();
        _delivered += want;
        return want;
    }

    /// <inheritdoc />
    public void Dispose() => _fifo.Dispose();

    private long Owed() =>
        (long)(_time.GetElapsedTime(_started).TotalSeconds * SampleRate) - _delivered;

    /// <summary>Whole samples sitting in the FIFO right now, without blocking for more.</summary>
    private int AvailableSamples()
    {
        try
        {
            int bytes = 0;
            if (Ioctl(_fifo.SafeFileHandle.DangerousGetHandle(), FionRead, ref bytes) < 0)
            {
                return 0;
            }

            return bytes <= 0 ? 0 : bytes / sizeof(float);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return 0;
        }
    }

    private const nuint FionRead = 0x541B;

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(nint fd, nuint request, ref int argument);
}

/// <summary>The transmit half of a pipe pair: raw float samples into the FIFO.</summary>
public sealed class PipeAudioOutput : IAudioOutput, IDisposable
{
    private readonly FileStream _fifo;
    private readonly byte[] _bytes = new byte[8192 * sizeof(float)];

    /// <summary>Opens the transmit FIFO, creating it if needed.</summary>
    /// <param name="path">The FIFO to write to.</param>
    /// <param name="sampleRate">The rate this writes at.</param>
    public PipeAudioOutput(string path, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _fifo = PipeAudio.OpenFifo(path);
        SampleRate = sampleRate;
        Path = path;
    }

    /// <summary>The FIFO this writes to.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> samples)
    {
        for (int at = 0; at < samples.Length;)
        {
            int chunk = Math.Min(samples.Length - at, _bytes.Length / sizeof(float));
            for (int n = 0; n < chunk; n++)
            {
                BitConverter.TryWriteBytes(_bytes.AsSpan(n * sizeof(float)), samples[at + n]);
            }

            _fifo.Write(_bytes, 0, chunk * sizeof(float));
            at += chunk;
        }

        _fifo.Flush();
    }

    /// <inheritdoc />
    public void Drain() => _fifo.Flush();

    /// <inheritdoc />
    public void Dispose() => _fifo.Dispose();
}
