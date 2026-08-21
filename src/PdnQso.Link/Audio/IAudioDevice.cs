using PdnQso.Link.Devices;

namespace PdnQso.Link.Audio;

/// <summary>
/// One radio's audio, as a station sees it: samples coming in, samples going out, a PTT line
/// and the power control that goes with it.
/// </summary>
/// <remarks>
/// <para>
/// The three real devices (a Flex over the LAN, a sound card with a CM108 PTT widget, an
/// UberSDR web receiver) arrive in phase A2 behind this interface; phase A ships the
/// in-process one that <see cref="AudioLink"/> builds, so two <see cref="Station"/>s can be
/// tested end to end without a radio.
/// </para>
/// <para>
/// A receive-only device - UberSDR - reports <see cref="CanTransmit"/> false, and
/// <see cref="TransmitAsync"/> on it throws rather than quietly doing nothing: a station that
/// thinks it is being heard and is not is the worst failure this tool has.
/// </para>
/// </remarks>
public interface IAudioDevice : IDisposable
{
    /// <summary>What to call this device in the UI, e.g. <c>alsa:plughw:1,0</c>.</summary>
    string Name { get; }

    /// <summary>The sample rate both directions run at.</summary>
    int SampleRate { get; }

    /// <summary>False for a receive-only device.</summary>
    bool CanTransmit { get; }

    /// <summary>True while the transmitter is keyed - the status bar's PTT lamp.</summary>
    bool Ptt { get; }

    /// <summary>
    /// This device's transmit power, or <see cref="NoPowerControl"/> where there is none.
    /// </summary>
    IPowerControl Power { get; }

    /// <summary>Received audio, in blocks, at <see cref="SampleRate"/>.</summary>
    event Action<float[]>? SamplesReceived;

    /// <summary>Raised when <see cref="Ptt"/> changes, so a lamp can follow it.</summary>
    event Action<bool>? PttChanged;

    /// <summary>Opens the device and starts delivering <see cref="SamplesReceived"/>.</summary>
    void Start();

    /// <summary>
    /// Keys the transmitter, plays <paramref name="samples"/>, and unkeys.
    /// </summary>
    /// <returns>A task that completes when the audio has left the transmitter - which is what
    /// makes it safe for the caller to treat the frame as sent and start an ARQ timer.</returns>
    /// <exception cref="InvalidOperationException"><see cref="CanTransmit"/> is false.</exception>
    Task TransmitAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default);
}
