using M0LTE.Dsp;
using M0LTE.Flex;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.UberSdr;
using PdnQso.Link.Audio;

namespace PdnQso.Link.Devices;

/// <summary>
/// Turns a parsed <see cref="DeviceString"/> into the <see cref="IAudioDevice"/> a
/// <see cref="Station"/> runs on: the four transports pdn-soundmodem supports, opened the way
/// its daemon opens them, presented at the modem's own sample rate.
/// </summary>
/// <remarks>
/// <para>
/// The rate matters more than it looks. A <see cref="Station"/> refuses a device whose rate is
/// not its modem's, because a modem running at the wrong speed decodes nothing and says
/// nothing about why. Sound cards will not generally open 12 kHz, and a Flex's DAX stream and
/// an UberSDR's IQ have rates of their own, so every path here resamples by a whole number
/// until what comes out is the mode's rate.
/// </para>
/// <para>
/// Only the pipe path can be exercised without hardware. The other three compile and are
/// wired, and what is actually on the far side of them is untested here.
/// </para>
/// </remarks>
public static class DeviceFactory
{
    /// <summary>
    /// Opens the device a string names, at <paramref name="sampleRate"/>.
    /// </summary>
    /// <param name="device">The parsed device string.</param>
    /// <param name="sampleRate">The mode's DSP rate, from <c>ModemCatalog.DspRateFor</c>.</param>
    /// <param name="options">PTT, tuning, gains; the defaults where omitted.</param>
    /// <param name="cancellationToken">Cancels a connect that is taking too long.</param>
    /// <exception cref="ArgumentException">The device cannot run at this rate by a whole
    /// number.</exception>
    /// <exception cref="InvalidOperationException">The device is there but refused - a Flex
    /// whose transmitter is not on DAX, an UberSDR that will not give us the IQ mode.</exception>
    /// <exception cref="IOException">The hardware is not.</exception>
    public static async Task<IAudioDevice> CreateAsync(
        DeviceString device,
        int sampleRate,
        DeviceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        options ??= new DeviceOptions();

        return device switch
        {
            AlsaDeviceString alsa => CreateAlsa(alsa, sampleRate, options),
            PipeDeviceString pipe => CreatePipe(pipe, sampleRate, options),
            FlexDeviceString flex =>
                await CreateFlexAsync(flex, sampleRate, options, cancellationToken).ConfigureAwait(false),
            UberSdrDeviceString uber =>
                await CreateUberSdrAsync(uber, sampleRate, options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"'{device.Text}' is not a device this tool opens", nameof(device)),
        };
    }

    /// <summary>
    /// <see cref="CreateAsync"/> for a caller that is not on the UI thread and has nothing
    /// useful to do while a radio connects.
    /// </summary>
    public static IAudioDevice Create(
        DeviceString device,
        int sampleRate,
        DeviceOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(device, sampleRate, options, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// The PTT line <paramref name="options"/> names, or null for none.
    /// </summary>
    /// <exception cref="ArgumentException">A PTT kind was chosen with no device to key.</exception>
    public static IPttControl? CreatePtt(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Ptt == PttKind.None)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.PttDevice))
        {
            throw new ArgumentException(
                $"a {options.Ptt} PTT needs a device: the hidraw node for a CM108 widget "
                + "(/dev/hidraw0) or the serial port for an RTS/DTR line (/dev/ttyUSB0)",
                nameof(options));
        }

        return options.Ptt switch
        {
            PttKind.Cm108 => new Cm108Ptt(options.PttDevice, options.Cm108Gpio),
            PttKind.Serial => new SerialPtt(
                options.PttDevice,
                useRts: !options.SerialLine.Equals("dtr", StringComparison.OrdinalIgnoreCase),
                useDtr: options.SerialLine.Equals("dtr", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    private static IAudioDevice CreateAlsa(
        AlsaDeviceString alsa, int sampleRate, DeviceOptions options)
    {
        int cardRate = options.CaptureRateHz;
        if (cardRate % sampleRate != 0)
        {
            throw new ArgumentException(
                $"a card at {cardRate} Hz cannot feed a {sampleRate} Hz mode - the ratio has to "
                + "be a whole number. 48000 works for every mode this tool has.",
                nameof(options));
        }

        IPttControl? ptt = null;
        AlsaAudioInput? capture = null;
        AlsaAudioOutput? playback = null;
        MixerPowerControl? power = null;
        try
        {
            ptt = CreatePtt(options);

            // Modulate at the mode's rate and play through the image-rejecting upsampler, as
            // the daemon does: cards commonly refuse to open 12 kHz playback directly.
            playback = new AlsaAudioOutput(alsa.Card, cardRate);
            IAudioOutput output = cardRate == sampleRate
                ? playback
                : new UpsamplingAudioOutput(playback, sampleRate);

            capture = new AlsaAudioInput(alsa.Card, cardRate, options.CaptureLatencyMicroseconds);
            IAudioInput input = cardRate == sampleRate
                ? capture
                : new DecimatingAudioInput(capture, sampleRate, options.BlockSamples);

            power = OpenMixer(alsa.Card, options);
            return new PumpedAudioDevice(
                alsa.Text, input, output, ptt, (IPowerControl?)power ?? NoPowerControl.Instance,
                options.BlockSamples, options.InputGain, options.OutputGain, options.Faulted);
        }
        catch
        {
            power?.Dispose();
            capture?.Dispose();
            playback?.Dispose();
            (ptt as IDisposable)?.Dispose();
            throw;
        }
    }

    private static MixerPowerControl? OpenMixer(string card, DeviceOptions options)
    {
        if (!options.UseMixerPower)
        {
            return null;
        }

        try
        {
            return new MixerPowerControl(AlsaSimpleMixer.Open(card));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException
                                    or DllNotFoundException or EntryPointNotFoundException)
        {
            // A card with no playback control still transmits perfectly well; the operator sets
            // the drive somewhere else. Say so once and carry on.
            options.Log?.Invoke($"power: no mixer control on {card} - {e.Message}");
            return null;
        }
    }

    private static IAudioDevice CreatePipe(
        PipeDeviceString pipe, int sampleRate, DeviceOptions options)
    {
        if (pipe.Rate % sampleRate != 0)
        {
            throw new ArgumentException(
                $"a pipe at {pipe.Rate} Hz cannot feed a {sampleRate} Hz mode - the ratio has to "
                + "be a whole number. Write the rate into the device string: "
                + $"pipe:{pipe.In},{pipe.Out},{sampleRate}.",
                nameof(pipe));
        }

        PipeAudioInput? capture = null;
        PipeAudioOutput? playback = null;
        try
        {
            playback = new PipeAudioOutput(pipe.Out, pipe.Rate);
            IAudioOutput output = pipe.Rate == sampleRate
                ? playback
                : new UpsamplingAudioOutput(playback, sampleRate);

            capture = new PipeAudioInput(pipe.In, pipe.Rate);
            IAudioInput input = pipe.Rate == sampleRate
                ? capture
                : new DecimatingAudioInput(capture, sampleRate, options.BlockSamples);

            // No PTT and no power: there is no transmitter here at all, and pretending
            // otherwise would put a lamp on the screen that means nothing.
            return new PumpedAudioDevice(
                pipe.Text, input, output, ptt: null, NoPowerControl.Instance,
                options.BlockSamples, options.InputGain, options.OutputGain, options.Faulted);
        }
        catch
        {
            capture?.Dispose();
            playback?.Dispose();
            throw;
        }
    }

    private static async Task<IAudioDevice> CreateFlexAsync(
        FlexDeviceString flex, int sampleRate, DeviceOptions options, CancellationToken cancellation)
    {
        var tuning = new FlexTuning
        {
            Antenna = options.FlexAntenna,
            Mode = options.FlexSliceMode,
            DaxChannel = options.FlexDaxChannel,
            StationName = options.FlexStationName,
            TxPowerWatts = options.FlexTxPowerWatts,
            Arbitration = options.FlexArbitration,
        };

        if (options.RfFrequencyHz is double rf)
        {
            // The radio takes MHz to six places, and the dial is where the audio centre has to
            // sit for the signal to land on the frequency the operator asked for.
            double dial = DialFrequency.For(rf, options.AudioCentreHz, options.LowerSideband);
            tuning = tuning with
            {
                Frequency = (dial / 1_000_000.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        FlexRuntime runtime = await FlexDevice
            .OpenAsync(flex.Text, sampleRate, packetBuffer: 3, tuning, cancellation)
            .ConfigureAwait(false);

        try
        {
            if (runtime.Station.TuneWarning is string tuneWarning)
            {
                options.Log?.Invoke($"flex: {tuneWarning}");
            }

            FlexMeters? meters = null;
            try
            {
                meters = await FlexMeters.SubscribeAsync(runtime.Station.Client, cancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is FlexProtocolException or IOException)
            {
                // A station that cannot read its meters transmits perfectly well; it just
                // cannot show what came out.
                options.Log?.Invoke($"flex: no transmit metering - {e.Message}");
            }

            FlexPowerControl power = await FlexPowerControl
                .OpenAsync(
                    runtime.Station.Client, meters, runtime.Station.MaxPowerLevel,
                    timeProvider: null, cancellation)
                .ConfigureAwait(false);

            IAudioInput input = runtime.Input.SampleRate == sampleRate
                ? runtime.Input
                : new DecimatingAudioInput(runtime.Input, sampleRate, options.BlockSamples);

            return new PumpedAudioDevice(
                flex.Text, input, runtime.Output, runtime.Ptt, power,
                options.BlockSamples, options.InputGain, options.OutputGain, options.Faulted,
                owned: runtime);
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IAudioDevice> CreateUberSdrAsync(
        UberSdrDeviceString uber, int sampleRate, DeviceOptions options, CancellationToken cancellation)
    {
        if (options.RfFrequencyHz is not double rf)
        {
            throw new ArgumentException(
                $"the UberSDR instance at {uber.Host} has to be told where to listen - set the "
                + "RF frequency in the settings. A receiver cannot guess what you came to hear.",
                nameof(options));
        }

        var tuning = new UberSdrTuning
        {
            // Tuned to the dial itself, so the suppressed carrier lands at DC in the IQ and the
            // demodulator's own oscillator has nothing left to do.
            FrequencyHz = (int)Math.Round(
                DialFrequency.For(rf, options.AudioCentreHz, options.LowerSideband)),
            Sideband = options.LowerSideband ? Sideband.Lower : Sideband.Upper,
            OutputRate = sampleRate,
            Mode = options.UberSdrMode,
            Password = options.UberSdrPassword,
            Gain = options.UberSdrGain,
        };

        UberSdrAudioInput input = await UberSdrAudioInput
            .OpenAsync(
                new UberSdrEndpoint(uber.Host, uber.Port, uber.Ssl), tuning, options.Log, cancellation)
            .ConfigureAwait(false);

        if (options.Log is Action<string> log)
        {
            input.Lost += reason => log($"ubersdr: {reason}");
        }

        return new PumpedAudioDevice(
            uber.Text, input, output: null, ptt: null, NoPowerControl.Instance,
            options.BlockSamples, options.InputGain, options.OutputGain, options.Faulted);
    }
}
