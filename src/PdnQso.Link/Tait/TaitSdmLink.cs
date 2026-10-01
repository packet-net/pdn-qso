using M0LTE.Tait.Ccdi;

namespace PdnQso.Link.Tait;

/// <summary>
/// The radio's SDM service, the half of <see cref="TaitCcdiRadio"/> an SDM link uses. The seam
/// a test puts a fake in.
/// </summary>
public interface ISdmRadio : IAsyncDisposable
{
    /// <summary>True while the radio reports carrier; null when it does not know.</summary>
    bool? ChannelBusy { get; }

    /// <summary>Every SDM the radio received, as the raw message bytes.</summary>
    event Action<byte[]>? MessageReceived;

    /// <summary>Hands one binary SDM to the radio to transmit.</summary>
    Task SendAsync(string destination, ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
}

/// <summary>
/// AX.25 frames over the radio's Short Data Messages: each frame is one binary SDM, reshaped
/// by <see cref="SdmFrameCodec"/> around the bytes an SDM cannot carry.
/// </summary>
/// <remarks>
/// <para>
/// The radio stays in Command mode throughout, so unlike Transparent mode the control channel
/// still works: carrier sense comes from its PROGRESS messages, and <see cref="Busy"/> is real.
/// </para>
/// <para>
/// The radio answers a send as soon as it has taken the command, which is before the message
/// is on air. The link then waits out its own estimate of the air time, so a send ends when
/// the frame has gone, as <see cref="ITaitLink.SendAsync"/> promises.
/// </para>
/// <para>
/// SDM delivery receipts are ignored. They depend on auto-acknowledge being programmed into
/// both radios, a wildcard destination gets none, and this tool's own protocols acknowledge
/// what they need to.
/// </para>
/// </remarks>
public sealed class TaitSdmLink : ITaitLink
{
    /// <summary>
    /// Bytes an SDM puts on air beyond the message: the radio's own preamble, address, format
    /// and check fields. An estimate; the radio does not say.
    /// </summary>
    private const int OverheadBytes = 16;

    /// <summary>The carrier lead-in the driver asks for when it is given none.</summary>
    private static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(100);

    private readonly ISdmRadio _radio;
    private readonly TaitSettings _settings;
    private readonly TimeProvider _time;
    private bool _disposed;

    /// <summary>Wraps a radio that is already set up to send and deliver SDMs.</summary>
    /// <param name="radio">The radio; this link owns it and disposes it.</param>
    /// <param name="settings">Destination and over-air rate.</param>
    /// <param name="timeProvider">The clock a send waits out its air time on.</param>
    public TaitSdmLink(ISdmRadio radio, TaitSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SdmDestination.Length != TaitSdmSideChannel.IdentityLength)
        {
            throw new ArgumentException(
                $"an SDM destination is {TaitSdmSideChannel.IdentityLength} characters, "
                + $"and '{settings.SdmDestination}' is not",
                nameof(settings));
        }

        _radio = radio;
        _settings = settings;
        _time = timeProvider ?? TimeProvider.System;
        Name = settings.PortName;
        _radio.MessageReceived += OnMessage;
    }

    /// <summary>
    /// Opens the radio on a serial port and turns on what an SDM link needs: PROGRESS messages,
    /// for carrier sense, and SDM output on reception, so a received message arrives without
    /// being asked for.
    /// </summary>
    public static async Task<TaitSdmLink> OpenAsync(
        TaitSettings settings, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TimeProvider time = timeProvider ?? TimeProvider.System;
        TaitCcdiRadio radio = TaitCcdiRadio.Open(settings.PortName, settings.BaudRate, new TaitCcdiRadioOptions(), time);
        try
        {
            await radio.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
            await radio.SetSdmOutputOnReceptionAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await radio.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new TaitSdmLink(new CcdiSdmRadio(radio), settings, time);
    }

    /// <inheritdoc />
    public string Mode => TaitModes.Sdm;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int MaxFrameBytes => SdmFrameCodec.MaxFrameBytes;

    /// <inheritdoc />
    public bool Busy => _radio.ChannelBusy ?? false;

    /// <inheritdoc />
    public event Action<byte[]>? FrameReceived;

    /// <inheritdoc />
    public async Task SendAsync(ReadOnlyMemory<byte> ax25Frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] message = SdmFrameCodec.Encode(ax25Frame.Span);
        await _radio.SendAsync(_settings.SdmDestination, message, cancellationToken).ConfigureAwait(false);
        await WaitOutAirTimeAsync(AirTimeOf(message.Length), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True while a send is parked on its air-time timer and nothing else, which is the one
    /// stretch in which a clock driving this link may be moved on.
    /// </summary>
    /// <remarks>
    /// For a test running on a clock of its own: a sender that has handed its frame to the
    /// radio is waiting for time to pass, and time cannot pass while the test counts it as
    /// busy. Raised just before the timer is armed and cleared by the timer's own callback,
    /// not by the continuation after it, so the instant the air time is up this reads false
    /// and whatever the sender does next is counted again (design.md 6d).
    /// </remarks>
    public bool ParkedOnAir => Volatile.Read(ref _parked) != 0;

    private int _parked;
    private int _park;

    private async Task WaitOutAirTimeAsync(TimeSpan airTime, CancellationToken cancellationToken)
    {
        int park = Interlocked.Increment(ref _park);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _parked, 1);
        using ITimer timer = _time.CreateTimer(
            _ =>
            {
                // Only this park's own timer may end it.
                if (Volatile.Read(ref _park) == park)
                {
                    Volatile.Write(ref _parked, 0);
                }

                landed.TrySetResult();
            },
            null,
            airTime,
            Timeout.InfiniteTimeSpan);
        using CancellationTokenRegistration cancel = cancellationToken.Register(() =>
        {
            if (Volatile.Read(ref _park) == park)
            {
                Volatile.Write(ref _parked, 0);
            }

            landed.TrySetCanceled(cancellationToken);
        });
        await landed.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sized for a frame that needed no escapes, which is nearly all of them; one that did is
    /// a byte or two longer.
    /// </remarks>
    public TimeSpan EstimateAirTime(int ax25FrameBytes) => AirTimeOf(1 + ax25FrameBytes);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _radio.MessageReceived -= OnMessage;
        await _radio.DisposeAsync().ConfigureAwait(false);
    }

    private TimeSpan AirTimeOf(int messageBytes)
    {
        // Over 32 bytes the radio splits the message into an extended SDM's several bursts,
        // each with its own lead-in: two for 100 or 128 bytes on the bench, so about one per
        // 64 bytes.
        int bursts = Math.Max(1, (messageBytes + 63) / 64);
        double bits = (messageBytes + (OverheadBytes * bursts)) * 8.0;
        return (LeadIn * bursts) + TimeSpan.FromSeconds(bits / _settings.FfskBaud);
    }

    private void OnMessage(byte[] message)
    {
        // Anything that is not one of ours - a plain text SDM from a radio's keypad, say - is
        // not a frame and is not passed up as one.
        if (SdmFrameCodec.TryDecode(message, out byte[] frame) && frame.Length > 0)
        {
            FrameReceived?.Invoke(frame);
        }
    }

    /// <summary><see cref="ISdmRadio"/> over the CCDI driver.</summary>
    private sealed class CcdiSdmRadio : ISdmRadio
    {
        private readonly TaitCcdiRadio _radio;

        public CcdiSdmRadio(TaitCcdiRadio radio)
        {
            _radio = radio;
            _radio.SdmReceived += OnSdm;
        }

        public bool? ChannelBusy => _radio.ChannelBusy;

        public event Action<byte[]>? MessageReceived;

        public Task SendAsync(string destination, ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
            _radio.SendBinarySdmAsync(destination, message, leadInDelay: null, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            _radio.SdmReceived -= OnSdm;
            await _radio.DisposeAsync().ConfigureAwait(false);
        }

        private void OnSdm(object? sender, CcdiSdmMessage sdm)
        {
            // The driver reads the serial line a byte to a character, so each character is
            // one byte of the message.
            string data = sdm.Data;
            var bytes = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                bytes[i] = (byte)data[i];
            }

            MessageReceived?.Invoke(bytes);
        }
    }
}
