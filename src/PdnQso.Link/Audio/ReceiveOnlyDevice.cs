using PdnQso.Link.Devices;

namespace PdnQso.Link.Audio;

/// <summary>
/// Any device, with the transmitter locked out: everything is heard, logged and shown, and
/// nothing at all goes out.
/// </summary>
/// <remarks>
/// <para>
/// This is what <c>--monitor-only</c> is made of, and it is a wrapper rather than a flag
/// somewhere in the station because the lockout then sits at the one place the audio could
/// leave: <see cref="TransmitAsync"/> throws, <see cref="CanTransmit"/> is false, and every
/// layer above - the station, the ARQ, the ident - refuses on its own without having been told
/// about the mode at all.
/// </para>
/// <para>
/// Wrapping an UberSDR, which has no transmitter anyway, is harmless and is not a special case.
/// </para>
/// </remarks>
/// <param name="inner">The device to listen with.</param>
public sealed class ReceiveOnlyDevice(IAudioDevice inner) : IAudioDevice
{
    private readonly IAudioDevice _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The device underneath, for a caller that needs its own type.</summary>
    public IAudioDevice Inner => _inner;

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public int SampleRate => _inner.SampleRate;

    /// <inheritdoc />
    public bool CanTransmit => false;

    /// <inheritdoc />
    public bool Ptt => false;

    /// <inheritdoc />
    public IPowerControl Power => _inner.Power;

    /// <inheritdoc />
    public event Action<float[]>? SamplesReceived
    {
        add => _inner.SamplesReceived += value;
        remove => _inner.SamplesReceived -= value;
    }

    /// <inheritdoc />
    public event Action<bool>? PttChanged
    {
        add => _inner.PttChanged += value;
        remove => _inner.PttChanged -= value;
    }

    /// <inheritdoc />
    public void Start() => _inner.Start();

    /// <inheritdoc />
    public Task TransmitAsync(
        ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            $"'{Name}' is in monitor-only mode - this station will not transmit. Restart "
            + "without --monitor-only to put it on the air.");

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
