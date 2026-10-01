using System.Globalization;
using Packet.SoundModem.Modems;
using PdnQso.Config;
using PdnQso.Link;
using PdnQso.Link.Chat;
using PdnQso.Link.Perf;
using PdnQso.Link.Tait;
using PdnQso.Ui;

namespace PdnQso.Headless;

/// <summary>
/// One headless run over a station that is already up: answer the far end, listen, or make one
/// measurement, saying what happens in plain lines and ending with an exit code.
/// </summary>
/// <remarks>
/// <para>
/// The same protocol pieces the screen drives - <see cref="ChatSession"/> and
/// <see cref="PerfRun"/> - with a text writer where the panes would be. That is the point of
/// it: a script over ssh, or a lab with two radios on one machine, exercises exactly what an
/// operator would, and gets an answer it can test with <c>$?</c>.
/// </para>
/// <para>
/// Exit codes: 0 for success (a responder or listener that ran until stopped, every ping
/// answered, the chat line acknowledged, no stream frame lost), 1 for loss or failure.
/// </para>
/// </remarks>
public sealed class HeadlessSession
{
    private readonly IStation _station;
    private readonly QsoConfig _config;
    private readonly TextWriter _out;
    private readonly TimeProvider _time;
    private readonly object _write = new();

    /// <summary>Builds a run over a started station.</summary>
    /// <param name="station">The station, already started.</param>
    /// <param name="config">The config it was built from, for the chat and perf knobs.</param>
    /// <param name="output">Where the lines go.</param>
    /// <param name="timeProvider">The clock every protocol timeout runs on.</param>
    public HeadlessSession(IStation station, QsoConfig config, TextWriter output, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(output);
        _station = station;
        _config = config;
        _out = output;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The chat session a respond or chat run is using, for a test to watch.</summary>
    public ChatSession? Chat { get; private set; }

    /// <summary>
    /// True once a respond run is answering everything it answers: chat started, and both the
    /// ping responder and a stream receiver listening. Until then a far end that starts at
    /// once can send into a gap, which is what a test, or a script, waits on this for.
    /// </summary>
    public bool Ready =>
        _chatStarted && _pong?.Listening == true && _streams?.Listening == true;

    private volatile bool _chatStarted;
    private PerfRun? _pong;
    private PerfRun? _streams;

    /// <summary>Runs the command.</summary>
    /// <param name="command">What to do.</param>
    /// <param name="cancellationToken">Stops a responder or listener, which then exits 0, or
    /// abandons a measurement, which then exits 1.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> RunAsync(HeadlessCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        _station.RawFrameReceived += OnHeard;
        _station.FrameTransmitted += OnSent;
        try
        {
            return command.Action switch
            {
                HeadlessAction.Listen => await ListenAsync(command, cancellationToken).ConfigureAwait(false),
                HeadlessAction.Respond => await RespondAsync(command, cancellationToken).ConfigureAwait(false),
                HeadlessAction.Ping => await PingAsync(command, cancellationToken).ConfigureAwait(false),
                HeadlessAction.Chat => await ChatAsync(command, cancellationToken).ConfigureAwait(false),
                HeadlessAction.Stream => await StreamAsync(command, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Say("stopped before it finished");
            return 1;
        }
        finally
        {
            _station.RawFrameReceived -= OnHeard;
            _station.FrameTransmitted -= OnSent;
        }
    }

    private async Task<int> ListenAsync(HeadlessCommand command, CancellationToken cancellationToken)
    {
        Say($"listening as {_station.Callsign} on {_station.DeviceName}, {_station.Mode}");
        await HoldAsync(command.RunFor, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RespondAsync(HeadlessCommand command, CancellationToken cancellationToken)
    {
        Say($"responding as {_station.Callsign} on {_station.DeviceName}, {_station.Mode}");
        await using var chat = new ChatSession(_station, _config.ToChatOptions(), _time);
        Chat = chat;
        chat.MessageReceived += message => Say($"chat from {message.Source}: {message.Text}");
        chat.Start();
        _chatStarted = true;

        // Two runs, not one shared, so that each says for itself whether it is listening.
        _pong = new PerfRun(_time);
        _streams = new PerfRun(_time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pong = _pong.RunPongResponderAsync(_station, stop.Token);
        Task streams = Task.Run(() => AnswerStreamsAsync(_streams, stop.Token), CancellationToken.None);

        // Said once it is true, so a script can start the far end on seeing it.
        while (!Ready && !pong.IsCompleted && !streams.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            await Task.Yield();
        }

        Say("ready");

        try
        {
            await HoldAsync(command.RunFor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Quietly(pong).ConfigureAwait(false);
            await Quietly(streams).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>One stream receiver after another, each ending when its sender wraps up.</summary>
    private async Task AnswerStreamsAsync(PerfRun perf, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PerfReport report = await perf.RunStreamReceiverAsync(_station, cancellationToken).ConfigureAwait(false);
                Say("stream received:\n" + report.ToText());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Say($"stream responder: {e.Message}");
            }
        }
    }

    private async Task<int> PingAsync(HeadlessCommand command, CancellationToken cancellationToken)
    {
        var run = new PerfRun(_time);
        PerfReport report = await run.RunPingAsync(
            _station,
            new PerfPingOptions { PingCount = command.Count, CentreHz = _config.ResolvedAudioCentreHz },
            cancellationToken).ConfigureAwait(false);
        Say(report.ToText());
        return report.FramesLost == 0 && report.FramesHeard == command.Count ? 0 : 1;
    }

    private async Task<int> ChatAsync(HeadlessCommand command, CancellationToken cancellationToken)
    {
        await using var chat = new ChatSession(_station, _config.ToChatOptions(), _time);
        Chat = chat;
        chat.Start();
        ChatDelivery delivery = await chat.SendAsync(command.Text ?? "", cancellationToken).ConfigureAwait(false);
        Say(delivery.IsDelivered
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"chat: delivered after {delivery.Attempts} attempt(s), round trip {delivery.RoundTrip.TotalMilliseconds:0} ms")
            : $"chat: not delivered after {delivery.Attempts} attempt(s)");
        return delivery.IsDelivered ? 0 : 1;
    }

    private async Task<int> StreamAsync(HeadlessCommand command, CancellationToken cancellationToken)
    {
        int payload = command.PayloadBytes ?? DefaultStreamPayload();
        var options = new PerfStreamOptions
        {
            FrameCount = command.Count,
            PayloadSize = payload,
            TxDelayMilliseconds = _config.TxDelayMs,
            CentreHz = _config.ResolvedAudioCentreHz,
        };

        var run = new PerfRun(_time);
        PerfReport report = _station switch
        {
            TaitStation tait => await run.RunStreamSenderAsync(
                _station,
                tait.EstimateAirTime(LinkFrame.HeaderLength + LinkFrame.InfoHeaderLength + payload),
                options,
                cancellationToken).ConfigureAwait(false),
            { Modem: IModem modem } => await run.RunStreamSenderAsync(
                _station, modem, ModemCatalog.DspRateFor(_config.Mode), options, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException($"{_station.Mode} has no way to time a frame"),
        };

        Say(report.ToText());
        return report.FramesLost == 0 && report.FramesHeard == command.Count ? 0 : 1;
    }

    /// <summary>128 bytes, or as much as one frame of the mode carries if that is less.</summary>
    private int DefaultStreamPayload() =>
        TaitModes.IsTait(_config.Mode) ? Math.Min(128, TaitModes.MaxPayloadBytes(_config.Mode)) : 128;

    private async Task HoldAsync(TimeSpan? runFor, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(runFor ?? Timeout.InfiniteTimeSpan, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping a responder or listener is how it is meant to end.
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnHeard(byte[] frame, FrameQuality quality) =>
        Say(MonitorLine.Format(_time.GetUtcNow(), frame, quality));

    private void OnSent(LinkFrame? link, byte[] frame) =>
        Say(MonitorLine.Format(
            _time.GetUtcNow(), frame, new FrameQuality(_station.Mode, frame.Length, null, null), outgoing: true));

    private void Say(string line)
    {
        lock (_write)
        {
            _out.WriteLine(line);
            _out.Flush();
        }
    }
}
