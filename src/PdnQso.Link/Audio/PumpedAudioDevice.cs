using M0LTE.Radio.Audio;
using PdnQso.Link.Devices;

namespace PdnQso.Link.Audio;

/// <summary>
/// The one <see cref="IAudioDevice"/> every real radio is built from: a capture thread pulling
/// blocks off an <see cref="IAudioInput"/>, a transmit path that keys an
/// <see cref="IPttControl"/> around an <see cref="IAudioOutput"/>, and whatever power control
/// the device came with.
/// </summary>
/// <remarks>
/// <para>
/// ALSA, Flex, UberSDR and the pipe pair differ only in which three of those they hand over,
/// so they share this rather than each growing their own thread and their own PTT bug. The
/// factory (<see cref="DeviceFactory"/>) is what knows how to open each one.
/// </para>
/// <para>
/// <b>The received block is reused.</b> <see cref="SamplesReceived"/> is raised with the same
/// array every time, on the capture thread, and the handler must have finished with it before
/// it returns - which is exactly what <see cref="Station"/> does, since it hands the samples
/// straight to the modem. A handler that wants to keep the samples has to copy them. This is
/// the per-sample path, and the house rule for those is no steady-state allocation.
/// </para>
/// <para>
/// <b>Transmit runs off the caller's thread.</b> Writing a burst to a sound card takes as long
/// as the burst lasts, so the write, the drain and the two PTT edges happen on the thread pool
/// and the returned task completes when the audio has actually left. The PTT is dropped in a
/// <c>finally</c>: an exception on the way out must never leave a transmitter keyed.
/// </para>
/// </remarks>
public sealed class PumpedAudioDevice : IAudioDevice
{
    private readonly IAudioInput _input;
    private readonly IAudioOutput? _output;
    private readonly IPttControl? _ptt;
    private readonly IAsyncDisposable? _owned;
    private readonly float[] _block;
    private readonly float _inputGain;
    private readonly float _outputGain;
    private readonly Action<Exception>? _faulted;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _keyed = new(1, 1);
    private float[] _transmitScratch = [];
    private Thread? _pump;
    private volatile bool _ptton;
    private bool _disposed;

    /// <summary>Builds a device over the three seams pdn-soundmodem's own devices expose.</summary>
    /// <param name="name">What to call it in the UI - the device string it came from.</param>
    /// <param name="input">Capture, already at the modem's rate (wrap it in a
    /// <see cref="DecimatingAudioInput"/> if the hardware is faster).</param>
    /// <param name="output">Playback at the same rate, or null for a receive-only device.</param>
    /// <param name="ptt">The PTT line; null where the transport keys itself or has no PTT.</param>
    /// <param name="power">The transmit power control, or <see cref="NoPowerControl.Instance"/>.</param>
    /// <param name="blockSamples">Samples per <see cref="SamplesReceived"/> block.</param>
    /// <param name="inputGain">Linear gain applied to captured audio.</param>
    /// <param name="outputGain">Linear gain applied to transmitted audio.</param>
    /// <param name="faulted">Called when the capture thread dies, so the UI can say why the
    /// station has gone deaf instead of simply going quiet.</param>
    /// <param name="owned">Something else this device is responsible for closing - the Flex
    /// runtime, which owns a radio session and disposes asynchronously.</param>
    /// <exception cref="ArgumentException">Capture and playback disagree about the rate.</exception>
    public PumpedAudioDevice(
        string name,
        IAudioInput input,
        IAudioOutput? output,
        IPttControl? ptt,
        IPowerControl power,
        int blockSamples = 1024,
        float inputGain = 1.0f,
        float outputGain = 1.0f,
        Action<Exception>? faulted = null,
        IAsyncDisposable? owned = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSamples);

        if (output is not null && output.SampleRate != input.SampleRate)
        {
            throw new ArgumentException(
                $"'{name}' captures at {input.SampleRate} Hz and plays at {output.SampleRate} Hz "
                + "- one of the two would run at the wrong speed",
                nameof(output));
        }

        Name = name;
        _input = input;
        _output = output;
        _ptt = ptt;
        Power = power;
        _block = new float[blockSamples];
        _inputGain = inputGain;
        _outputGain = outputGain;
        _faulted = faulted;
        _owned = owned;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int SampleRate => _input.SampleRate;

    /// <inheritdoc />
    public bool CanTransmit => _output is not null;

    /// <inheritdoc />
    public bool Ptt => _ptton;

    /// <inheritdoc />
    public IPowerControl Power { get; }

    /// <inheritdoc />
    public event Action<float[]>? SamplesReceived;

    /// <inheritdoc />
    public event Action<bool>? PttChanged;

    /// <summary>How many blocks have been delivered - a liveness counter for the UI.</summary>
    public long BlocksCaptured { get; private set; }

    /// <summary>
    /// How much air has been delivered, in samples at <see cref="SampleRate"/> - the device's
    /// own elapsed time. A real-time test bounds its waits with this, there being no other
    /// clock it is allowed to read: "the frame did not arrive within this much pumped air" is
    /// a statement about the link that stays true or false however busy the machine was.
    /// </summary>
    public long SamplesCaptured => BlocksCaptured * _block.Length;

    /// <summary>
    /// The capture seam this device pumps. Exposed so that a caller who built the device over
    /// a paced input - the pipe pair - can still read that input's own counters; the wrapper
    /// would otherwise be the only thing that could say how the audio got on.
    /// </summary>
    public IAudioInput Input => _input;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pump is not null)
        {
            return;
        }

        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = $"pdn-qso capture {Name}",
        };
        _pump.Start();
    }

    /// <inheritdoc />
    public async Task TransmitAsync(
        ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_output is not IAudioOutput output)
        {
            throw new InvalidOperationException(
                $"'{Name}' is a receive-only device - this station cannot transmit");
        }

        // One keyup at a time even if two callers race here: two bursts interleaved into one
        // transmission is one unreadable frame instead of two readable ones.
        await _keyed.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () =>
                {
                    ReadOnlySpan<float> audio = ApplyTransmitGain(samples.Span);
                    SetPtt(true);
                    try
                    {
                        output.Write(audio);
                        output.Drain();
                    }
                    finally
                    {
                        SetPtt(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _keyed.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();

        // A transmitter left keyed by a crash is the one failure a station must never have, so
        // the line is dropped before anything else is closed.
        try
        {
            if (_ptton)
            {
                _ptt?.Unkey();
                SetPtt(false);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _faulted?.Invoke(e);
        }

        _pump?.Join(TimeSpan.FromSeconds(2));
        (_input as IDisposable)?.Dispose();
        (_output as IDisposable)?.Dispose();
        (_ptt as IDisposable)?.Dispose();
        if (_owned is not null)
        {
            _owned.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _stopping.Dispose();
        _keyed.Dispose();
    }

    private ReadOnlySpan<float> ApplyTransmitGain(ReadOnlySpan<float> samples)
    {
        if (_outputGain == 1.0f)
        {
            return samples;
        }

        if (_transmitScratch.Length < samples.Length)
        {
            _transmitScratch = new float[samples.Length];
        }

        Span<float> scaled = _transmitScratch.AsSpan(0, samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            scaled[i] = samples[i] * _outputGain;
        }

        return scaled;
    }

    private void Pump()
    {
        int filled = 0;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int got = _input.Read(_block.AsSpan(filled));
                if (got <= 0)
                {
                    // Nothing to be had this instant. A short sleep rather than a spin: a
                    // capture device with nothing in it is normal on a FIFO between bursts.
                    Thread.Sleep(2);
                    continue;
                }

                filled += got;
                if (filled < _block.Length)
                {
                    continue;
                }

                filled = 0;
                if (_inputGain != 1.0f)
                {
                    for (int i = 0; i < _block.Length; i++)
                    {
                        _block[i] *= _inputGain;
                    }
                }

                BlocksCaptured++;
                SamplesReceived?.Invoke(_block);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!_stopping.IsCancellationRequested)
            {
                // A station that has gone deaf and says nothing looks exactly like a quiet
                // band, which is the worst way for this to fail.
                _faulted?.Invoke(e);
            }
        }
    }

    private void SetPtt(bool value)
    {
        if (_ptton == value)
        {
            return;
        }

        if (value)
        {
            _ptt?.Key();
        }
        else
        {
            _ptt?.Unkey();
        }

        _ptton = value;
        PttChanged?.Invoke(value);
    }
}
