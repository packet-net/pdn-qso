using Packet.SoundModem.Modems;
using PdnQso.Link.Devices;

namespace PdnQso.Link.Perf;

/// <summary>
/// Turns a link into numbers: design.md section 3's stream and ping-pong procedures.
/// </summary>
/// <remarks>
/// <para>
/// A stream run has a sender and a receiver, driven by <see cref="RunStreamSenderAsync"/> and
/// <see cref="RunStreamReceiverAsync"/> on the two stations. The sender transmits
/// <see cref="PerfStreamOptions.FrameCount"/> numbered frames, then sends one more
/// <see cref="LinkFrameType.PerfPing"/> on the same session asking the receiver to wrap up; the
/// receiver replies with a <see cref="LinkFrameType.PerfPong"/> carrying what it actually heard,
/// so the sender's own report is not just "how many I sent" but "how many arrived" too.
/// </para>
/// <para>
/// A ping-pong run has a pinger and a responder: <see cref="RunPingAsync"/> sends
/// <see cref="LinkFrameType.PerfPing"/> probes one at a time and times each answer;
/// <see cref="RunPongResponderAsync"/> answers every probe it hears until cancelled. The
/// responder queues its answers rather than sending them from inside the frame-received handler
/// - <see cref="Audio.AudioLink"/>'s own remarks explain why: the far station's handler runs
/// while the near one is still inside its send, and replying straight back would re-enter the
/// link on the same call stack.
/// </para>
/// <para>
/// Pure and UI free: everything here is <see cref="IStation"/> and <see cref="TimeProvider"/>,
/// no console and no display formatting beyond <see cref="PerfReport.ToText"/> itself.
/// </para>
/// </remarks>
/// <param name="timeProvider">Wall clock for every duration this class measures;
/// <see cref="TimeProvider.System"/> when omitted.</param>
public sealed class PerfRun(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>A running report fired after each frame sent or answered, for a UI to show a
    /// measurement filling in as it happens rather than only at the end.</summary>
    public event Action<PerfReport>? Progress;

    /// <summary>The final report of a run, fired once, just before the run's task completes
    /// with the same value.</summary>
    public event Action<PerfReport>? Completed;

    /// <summary>
    /// The sending half of a stream run: transmits <see cref="PerfStreamOptions.FrameCount"/>
    /// numbered frames, then asks the far end to report what it heard.
    /// </summary>
    /// <param name="station">The sending station. <see cref="IStation.SendAsync"/> already
    /// waits for a clear channel, so nothing here duplicates that wait.</param>
    /// <param name="modem">The same station's modem, used only to measure one frame's air time
    /// by modulating a probe and dividing its length by <paramref name="dspRateHz"/> - the
    /// modem's own number, not an estimate.</param>
    /// <param name="dspRateHz">The DSP rate <paramref name="modem"/> runs at.
    /// <see cref="IModem"/> does not expose its own rate, so the caller supplies it - normally
    /// the device's <c>SampleRate</c> the station was built over.</param>
    /// <param name="options">How many frames, how big, how the timing runs.</param>
    /// <param name="cancellationToken">Cancels the run; the far end's summary is then never
    /// awaited and the returned task is cancelled rather than completed.</param>
    /// <returns>A report complete with what the far end acknowledged.</returns>
    /// <exception cref="TimeoutException">No summary arrived after
    /// <see cref="PerfStreamOptions.SummaryRetries"/> attempts.</exception>
    public async Task<PerfReport> RunStreamSenderAsync(
        IStation station,
        IModem modem,
        int dspRateHz,
        PerfStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(modem);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FrameCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PayloadSize, PerfWire.StreamHeaderLength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dspRateHz, 0);

        DateTimeOffset start = _time.GetUtcNow();
        PowerReading power = await station.Power.ReadAsync(cancellationToken).ConfigureAwait(false);
        byte session = options.Session ?? RandomSession();
        double airTimeSeconds = MeasureAirTimeSeconds(
            modem, options.PayloadSize, options.TxDelayMilliseconds, dspRateHz);

        for (int i = 0; i < options.FrameCount; i++)
        {
            uint sendMs = (uint)Math.Max(0, (_time.GetUtcNow() - start).TotalMilliseconds);
            byte[] payload = PerfWire.EncodeStreamPayload(
                (ushort)i, (ushort)options.FrameCount, sendMs, options.PayloadSize);
            await station.SendAsync(
                station.Frame(LinkFrameType.PerfStream, session, payload), cancellationToken)
                .ConfigureAwait(false);

            Progress?.Invoke(new PerfReport(
                "stream", modem.Mode, options.CentreHz, station.DeviceName, power,
                FramesSent: i + 1, FramesHeard: 0, FramesDelivered: 0, FramesLost: 0, Duplicates: 0,
                FrameErrorRate: 0, GoodputBytesPerSecond: 0, Elapsed: _time.GetUtcNow() - start,
                MeanSnrDb: null, WorstSnrDb: null, LastSnrDb: null, MeanRttMs: null, WorstRttMs: null,
                Timestamp: _time.GetUtcNow()));

            if (options.Gap > TimeSpan.Zero && i < options.FrameCount - 1)
            {
                await Task.Delay(options.Gap, _time, cancellationToken).ConfigureAwait(false);
            }
        }

        PerfWire.Summary? summary = null;
        int attempts = Math.Max(1, options.SummaryRetries);
        for (int attempt = 0; attempt < attempts && summary is null; attempt++)
        {
            using var wait = new FrameWait(
                station,
                (frame, _) => frame.Type == LinkFrameType.PerfPong && frame.Session == session);
            await station.SendAsync(
                station.Frame(LinkFrameType.PerfPing, session), cancellationToken).ConfigureAwait(false);
            (LinkFrame Frame, FrameQuality Quality)? answer =
                await wait.AnswerAsync(options.SummaryTimeout, _time, cancellationToken)
                    .ConfigureAwait(false);
            if (answer is { } received && PerfWire.TryDecodeSummary(received.Frame.Payload.Span, out PerfWire.Summary decoded))
            {
                summary = decoded;
            }
        }

        if (summary is not { } heard)
        {
            throw new TimeoutException(
                $"no perf summary from the far end after {attempts} attempt(s) - "
                + "this run's numbers are incomplete");
        }

        double goodput = options.PayloadSize * heard.Heard / (airTimeSeconds * options.FrameCount);
        var report = new PerfReport(
            "stream", modem.Mode, options.CentreHz, station.DeviceName, power,
            FramesSent: options.FrameCount,
            FramesHeard: heard.Heard,
            FramesDelivered: heard.Heard,
            FramesLost: heard.Lost,
            Duplicates: heard.Duplicates,
            FrameErrorRate: (double)heard.Lost / options.FrameCount,
            GoodputBytesPerSecond: goodput,
            Elapsed: _time.GetUtcNow() - start,
            MeanSnrDb: heard.MeanSnrDb,
            WorstSnrDb: heard.WorstSnrDb,
            LastSnrDb: heard.LastSnrDb,
            MeanRttMs: null,
            WorstRttMs: null,
            Timestamp: _time.GetUtcNow());

        Completed?.Invoke(report);
        return report;
    }

    /// <summary>
    /// The receiving half of a stream run: counts what arrives on the first session it sees
    /// stream frames for, then answers the sender's wrap-up request with a summary.
    /// </summary>
    /// <param name="station">The receiving station.</param>
    /// <param name="cancellationToken">Cancels the wait for the wrap-up request.</param>
    /// <returns>This end's own view of the run - the same counts the summary it sent carries.</returns>
    public async Task<PerfReport> RunStreamReceiverAsync(
        IStation station, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        DateTimeOffset start = _time.GetUtcNow();
        PowerReading power = await station.Power.ReadAsync(cancellationToken).ConfigureAwait(false);

        byte? session = null;
        bool[]? seen = null;
        int total = 0;
        int heard = 0;
        int duplicates = 0;
        double snrSum = 0;
        int snrCount = 0;
        double? worstSnr = null;
        double? lastSnr = null;
        var wrapUp = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrame(LinkFrame frame, FrameQuality quality)
        {
            if (frame.Type == LinkFrameType.PerfStream)
            {
                session ??= frame.Session;
                if (frame.Session != session)
                {
                    return;
                }

                if (!PerfWire.TryDecodeStreamPayload(frame.Payload.Span, out ushort seq, out ushort frameTotal, out _))
                {
                    return;
                }

                if (seen is null)
                {
                    total = frameTotal;
                    seen = new bool[Math.Max((int)total, 1)];
                }

                if (seq < seen.Length)
                {
                    if (seen[seq])
                    {
                        duplicates++;
                    }
                    else
                    {
                        seen[seq] = true;
                        heard++;
                    }
                }

                if (quality.SnrDb is double snr)
                {
                    snrSum += snr;
                    snrCount++;
                    worstSnr = worstSnr is null ? snr : Math.Min(worstSnr.Value, snr);
                    lastSnr = snr;
                }

                double? mean = snrCount > 0 ? snrSum / snrCount : null;
                int lostSoFar = Math.Max(0, total - heard);
                Progress?.Invoke(new PerfReport(
                    "stream", station.Mode, null, station.DeviceName, power,
                    FramesSent: 0, FramesHeard: heard, FramesDelivered: heard, FramesLost: lostSoFar,
                    Duplicates: duplicates, FrameErrorRate: total == 0 ? 0 : (double)lostSoFar / total,
                    GoodputBytesPerSecond: 0, Elapsed: _time.GetUtcNow() - start,
                    MeanSnrDb: mean, WorstSnrDb: worstSnr, LastSnrDb: lastSnr,
                    MeanRttMs: null, WorstRttMs: null, Timestamp: _time.GetUtcNow()));
            }
            else if (frame.Type == LinkFrameType.PerfPing && session is not null && frame.Session == session)
            {
                wrapUp.TrySetResult(frame.Session);
            }
        }

        station.FrameReceived += OnFrame;
        try
        {
            byte wrapSession = await wrapUp.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            int lost = Math.Max(0, total - heard);
            double? meanSnr = snrCount > 0 ? snrSum / snrCount : null;

            var report = new PerfReport(
                "stream", station.Mode, null, station.DeviceName, power,
                FramesSent: 0,
                FramesHeard: heard,
                FramesDelivered: heard,
                FramesLost: lost,
                Duplicates: duplicates,
                FrameErrorRate: total == 0 ? 0 : (double)lost / total,
                GoodputBytesPerSecond: 0,
                Elapsed: _time.GetUtcNow() - start,
                MeanSnrDb: meanSnr,
                WorstSnrDb: worstSnr,
                LastSnrDb: lastSnr,
                MeanRttMs: null,
                WorstRttMs: null,
                Timestamp: _time.GetUtcNow());

            byte[] summaryPayload = PerfWire.EncodeSummary(
                new PerfWire.Summary((ushort)heard, (ushort)lost, (ushort)duplicates, meanSnr, worstSnr, lastSnr));
            await station.SendAsync(
                station.Frame(LinkFrameType.PerfPong, wrapSession, summaryPayload), cancellationToken)
                .ConfigureAwait(false);

            Completed?.Invoke(report);
            return report;
        }
        finally
        {
            station.FrameReceived -= OnFrame;
        }
    }

    /// <summary>
    /// The pinging half of a ping-pong run: sends <see cref="PerfPingOptions.PingCount"/> probes
    /// one at a time, timing each answer.
    /// </summary>
    /// <param name="station">The pinging station.</param>
    /// <param name="options">How many probes, how patient to be, how the timing runs.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A report of round-trip time and loss; never throws for a lost probe, that is
    /// what <see cref="PerfReport.FramesLost"/> is for.</returns>
    public async Task<PerfReport> RunPingAsync(
        IStation station, PerfPingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PingCount, 1);

        DateTimeOffset start = _time.GetUtcNow();
        PowerReading power = await station.Power.ReadAsync(cancellationToken).ConfigureAwait(false);
        byte session = options.Session ?? RandomSession();

        int heard = 0;
        int lost = 0;
        double rttSum = 0;
        double? worstRtt = null;

        for (int i = 0; i < options.PingCount; i++)
        {
            ushort seq = (ushort)i;
            DateTimeOffset sentAt = _time.GetUtcNow();
            byte[] pingPayload = PerfWire.EncodePingPayload(seq, 0);

            // Listen first, so a pong that comes back inside our own transmit is not missed,
            // but start the clock on the timeout only once the probe is actually on air. Armed
            // before the send, the timeout is running through this station's own TXDELAY and
            // modulation, and can in principle expire against a frame that has not left yet.
            using var wait = new FrameWait(station, (frame, quality) => IsPongFor(frame, session, seq));

            await station.SendAsync(
                station.Frame(LinkFrameType.PerfPing, session, pingPayload), cancellationToken)
                .ConfigureAwait(false);
            (LinkFrame Frame, FrameQuality Quality)? answer =
                await wait.AnswerAsync(options.PingTimeout, _time, cancellationToken)
                    .ConfigureAwait(false);

            if (answer is not null)
            {
                double rtt = (_time.GetUtcNow() - sentAt).TotalMilliseconds;
                heard++;
                rttSum += rtt;
                worstRtt = worstRtt is null ? rtt : Math.Max(worstRtt.Value, rtt);
            }
            else
            {
                lost++;
            }

            Progress?.Invoke(new PerfReport(
                "ping-pong", station.Mode, options.CentreHz, station.DeviceName, power,
                FramesSent: i + 1, FramesHeard: heard, FramesDelivered: heard, FramesLost: lost,
                Duplicates: 0, FrameErrorRate: (double)lost / (i + 1), GoodputBytesPerSecond: 0,
                Elapsed: _time.GetUtcNow() - start,
                MeanSnrDb: null, WorstSnrDb: null, LastSnrDb: null,
                MeanRttMs: heard > 0 ? rttSum / heard : null, WorstRttMs: worstRtt,
                Timestamp: _time.GetUtcNow()));

            if (options.Gap > TimeSpan.Zero && i < options.PingCount - 1)
            {
                await Task.Delay(options.Gap, _time, cancellationToken).ConfigureAwait(false);
            }
        }

        var report = new PerfReport(
            "ping-pong", station.Mode, options.CentreHz, station.DeviceName, power,
            FramesSent: options.PingCount,
            FramesHeard: heard,
            FramesDelivered: heard,
            FramesLost: lost,
            Duplicates: 0,
            FrameErrorRate: (double)lost / options.PingCount,
            GoodputBytesPerSecond: 0,
            Elapsed: _time.GetUtcNow() - start,
            MeanSnrDb: null,
            WorstSnrDb: null,
            LastSnrDb: null,
            MeanRttMs: heard > 0 ? rttSum / heard : null,
            WorstRttMs: worstRtt,
            Timestamp: _time.GetUtcNow());

        Completed?.Invoke(report);
        return report;
    }

    /// <summary>
    /// The answering half of a ping-pong run: echoes every <see cref="LinkFrameType.PerfPing"/>
    /// probe heard back as a <see cref="LinkFrameType.PerfPong"/>, until cancelled. Runs
    /// indefinitely - the caller starts it as a background task alongside a far end's
    /// <see cref="RunPingAsync"/> and cancels it when that finishes.
    /// </summary>
    /// <param name="station">The responding station.</param>
    /// <param name="cancellationToken">Stops the responder.</param>
    /// <remarks>
    /// Replies are queued rather than sent from inside the frame-received handler: the handler
    /// runs on whatever thread the audio arrived on, which for the hermetic
    /// <see cref="Audio.AudioLink"/> is the pinger's own transmitting call - sending straight
    /// back from there would re-enter that call's stack. The queue's reader is a separate async
    /// loop, so the reply goes out once the ping's own send has actually finished.
    /// </remarks>
    public async Task RunPongResponderAsync(IStation station, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        var pending = System.Threading.Channels.Channel.CreateUnbounded<LinkFrame>();

        void OnFrame(LinkFrame frame, FrameQuality quality)
        {
            if (frame.Type == LinkFrameType.PerfPing && frame.Payload.Length >= PerfWire.PingPayloadLength)
            {
                pending.Writer.TryWrite(frame);
            }
        }

        station.FrameReceived += OnFrame;
        try
        {
            await foreach (LinkFrame ping in pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await station.SendAsync(
                    station.Frame(LinkFrameType.PerfPong, ping.Session, ping.Payload.Span), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped on request - the normal way this loop ends, since it otherwise runs for ever.
        }
        finally
        {
            station.FrameReceived -= OnFrame;
        }
    }

    /// <summary>
    /// The air time of one stream frame of <paramref name="payloadSize"/> bytes: modulates a
    /// probe frame once and divides its length by <paramref name="dspRateHz"/>, so the answer is
    /// the modem's own number for exactly this payload size and TXDELAY.
    /// </summary>
    private static double MeasureAirTimeSeconds(
        IModem modem, int payloadSize, int txDelayMilliseconds, int dspRateHz)
    {
        byte[] probe = new LinkFrame(
            "N0CALL", LinkFrameType.PerfStream, 0,
            PerfWire.EncodeStreamPayload(0, 1, 0, payloadSize)).Encode();
        float[] audio = modem.Modulate(probe, txDelayMilliseconds);
        return (double)audio.Length / dspRateHz;
    }

    private static byte RandomSession() => (byte)Random.Shared.Next(0, 256);

    /// <summary>Whether a heard frame is the answer to ping <paramref name="seq"/> on
    /// <paramref name="session"/> - a <see cref="LinkFrameType.PerfPong"/> whose echoed payload
    /// carries the same sequence number back.</summary>
    private static bool IsPongFor(LinkFrame frame, byte session, ushort seq) =>
        frame.Type == LinkFrameType.PerfPong
        && frame.Session == session
        && PerfWire.TryDecodePingPayload(frame.Payload.Span, out ushort echoedSeq, out _)
        && echoedSeq == seq;

    /// <summary>
    /// Listens for the next frame a predicate accepts, from the moment it is made, and is then
    /// asked for the answer with a timeout.
    /// </summary>
    /// <remarks>
    /// Two steps rather than one so that the subscription can be in place before a probe is
    /// transmitted while the timeout only starts once it has been. One call that did both had
    /// the timeout covering this station's own transmit, which is neither what a round trip
    /// means nor safe: it can expire against a frame that has not gone out yet.
    /// </remarks>
    private sealed class FrameWait : IDisposable
    {
        private readonly IStation _station;
        private readonly TaskCompletionSource<(LinkFrame, FrameQuality)> _found =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Func<LinkFrame, FrameQuality, bool> _predicate;
        private bool _disposed;

        public FrameWait(IStation station, Func<LinkFrame, FrameQuality, bool> predicate)
        {
            _station = station;
            _predicate = predicate;
            _station.FrameReceived += OnFrame;
        }

        /// <summary>The frame, or null if none arrived inside <paramref name="timeout"/>.</summary>
        public async Task<(LinkFrame Frame, FrameQuality Quality)?> AnswerAsync(
            TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
        {
            if (_found.Task.IsCompleted)
            {
                return await _found.Task.ConfigureAwait(false);
            }

            // The timeout's own timer is cancelled as soon as the answer arrives, rather than
            // left running with nothing waiting on it.
            using var expired = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                Task winner = await Task
                    .WhenAny(_found.Task, Task.Delay(timeout, time, expired.Token))
                    .ConfigureAwait(false);
                return winner == _found.Task ? await _found.Task.ConfigureAwait(false) : null;
            }
            finally
            {
                await expired.CancelAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _station.FrameReceived -= OnFrame;
        }

        private void OnFrame(LinkFrame frame, FrameQuality quality)
        {
            if (_predicate(frame, quality))
            {
                _found.TrySetResult((frame, quality));
            }
        }
    }
}
