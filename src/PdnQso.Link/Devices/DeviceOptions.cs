namespace PdnQso.Link.Devices;

/// <summary>Which kind of PTT line a sound-card station keys with.</summary>
public enum PttKind
{
    /// <summary>None: a rig keyed by VOX, or by hand, or a transport that keys itself.</summary>
    None,

    /// <summary>A CM108/CM119 HID GPIO - the widget on the interface boards this network uses.</summary>
    Cm108,

    /// <summary>RTS or DTR on a serial port.</summary>
    Serial,
}

/// <summary>
/// Everything a device needs that its device string does not say: which PTT line, what rate to
/// run the card at, where the radio is tuned, and how hard to drive it.
/// </summary>
/// <remarks>
/// One record for all four device kinds rather than four, because the settings dialog is one
/// dialog and an operator switching from a sound card to a Flex should not lose what they
/// typed. Fields that do not apply to the device in hand are simply not read.
/// </remarks>
public sealed record DeviceOptions
{
    /// <summary>Which PTT line to key (ALSA only; the other transports key themselves).</summary>
    public PttKind Ptt { get; init; } = PttKind.None;

    /// <summary>The PTT device: a hidraw node for CM108, a tty for serial.</summary>
    public string? PttDevice { get; init; }

    /// <summary>The CM108 GPIO pin. 3 is what the interface boards wire.</summary>
    public int Cm108Gpio { get; init; } = 3;

    /// <summary>Which serial line to assert: <c>rts</c> (the default) or <c>dtr</c>.</summary>
    public string SerialLine { get; init; } = "rts";

    /// <summary>
    /// What rate to open the sound card at. The modes run at 12 or 48 kHz and plenty of cards
    /// will not open 12 kHz at all, so the card runs at its own rate and the adapter resamples
    /// - which is what pdn-soundmodem's daemon does too.
    /// </summary>
    public int CaptureRateHz { get; init; } = 48_000;

    /// <summary>ALSA buffer target. Larger rides out a busy machine at the cost of latency.</summary>
    public int CaptureLatencyMicroseconds { get; init; } = 120_000;

    /// <summary>Samples per received block. One block is one call into the modem.</summary>
    public int BlockSamples { get; init; } = 1024;

    /// <summary>Linear gain applied to captured audio before the modem sees it.</summary>
    public float InputGain { get; init; } = 1.0f;

    /// <summary>Linear gain applied to transmit audio on its way to the radio.</summary>
    public float OutputGain { get; init; } = 1.0f;

    /// <summary>
    /// Drive the card's playback mixer as the transmit power control. Off leaves the level
    /// alone, for a station whose drive is set somewhere else and must not be moved.
    /// </summary>
    public bool UseMixerPower { get; init; } = true;

    /// <summary>
    /// Where the modem's audio centre should land in RF, in Hz. The dial is this minus the
    /// audio centre on USB, which is what actually gets sent to the radio or the receiver.
    /// Null leaves a Flex on whatever it was tuned to; an UberSDR has to have one, since a
    /// receiver cannot guess what you came to listen to.
    /// </summary>
    public double? RfFrequencyHz { get; init; }

    /// <summary>The modem's audio centre, for working out the dial from
    /// <see cref="RfFrequencyHz"/>.</summary>
    public double AudioCentreHz { get; init; } = 1500;

    /// <summary>Lower sideband instead of upper. USB is the data-mode norm.</summary>
    public bool LowerSideband { get; init; }

    /// <summary>The Flex antenna to use on a headless bring-up.</summary>
    public string FlexAntenna { get; init; } = "ANT1";

    /// <summary>The Flex slice demod mode on a headless bring-up.</summary>
    public string FlexSliceMode { get; init; } = "DIGU";

    /// <summary>The DAX channel to claim. SmartSDR grabs 1, so a shared box wants another.</summary>
    public string FlexDaxChannel { get; init; } = "1";

    /// <summary>What to call this client on the radio, so a second one is not also "Flex".</summary>
    public string FlexStationName { get; init; } = "pdn-qso";

    /// <summary>Transmit power in watts to set at bring-up; null leaves the radio's own.</summary>
    public double? FlexTxPowerWatts { get; init; }

    /// <summary>Key through the arbitrated PTT, for a radio shared with another transmitter.</summary>
    public bool FlexArbitration { get; init; }

    /// <summary>The UberSDR IQ mode: <c>iq48</c> everywhere, <c>iq96</c> where allowed.</summary>
    public string UberSdrMode { get; init; } = "iq48";

    /// <summary>Password for a protected UberSDR instance; null for a public one.</summary>
    public string? UberSdrPassword { get; init; }

    /// <summary>Linear gain on the UberSDR's demodulated audio, to bring a quiet instance up.</summary>
    public float UberSdrGain { get; init; } = 1.0f;

    /// <summary>Where to send a line about something going wrong after start-up.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Called when the capture thread dies, so the UI can say why it went deaf.</summary>
    public Action<Exception>? Faulted { get; init; }
}

/// <summary>Working out a dial frequency from where the signal is meant to land.</summary>
public static class DialFrequency
{
    /// <summary>
    /// The dial to tune so that a modem's audio centre lands on <paramref name="rfHz"/>.
    /// </summary>
    /// <remarks>
    /// On upper sideband the audio spectrum sits above the (suppressed) carrier, so the dial
    /// is the wanted RF minus the audio centre; on lower sideband it is mirrored and the dial
    /// is above. This is the one piece of arithmetic that decides whether two stations are on
    /// the same frequency at all, which is why it is a named function with a test rather than
    /// a minus sign somewhere in a device factory.
    /// </remarks>
    /// <param name="rfHz">Where the modem's audio centre should land.</param>
    /// <param name="audioCentreHz">The modem's audio centre.</param>
    /// <param name="lowerSideband">True for LSB.</param>
    public static double For(double rfHz, double audioCentreHz, bool lowerSideband = false) =>
        lowerSideband ? rfHz + audioCentreHz : rfHz - audioCentreHz;
}
