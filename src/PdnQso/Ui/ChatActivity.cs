using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using PdnQso.Link;
using PdnQso.Link.Chat;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>
/// Keyboard to keyboard: the transcript, an input line, and a delivery tick against every line
/// this station sent.
/// </summary>
/// <remarks>
/// <para>
/// The whole activity over <see cref="ChatSession"/>, which is where the ARQ lives. What is
/// here is the screen and nothing else: a <see cref="ChatTranscript"/> that is pure and tested,
/// a list to show it in, and a field that turns Enter into
/// <see cref="ChatSession.SendAsync(string, CancellationToken)"/>.
/// </para>
/// <para>
/// <b>Delivery ticks are the point.</b> A chat window that shows a line the moment it is typed
/// and never says whether it was heard is worse than no window, because it looks like it
/// worked. Every outgoing line here goes up as <c>sending</c> and is rewritten in place as
/// <c>ok</c> with the attempts and the round trip, or as <c>failed</c> with the attempts it
/// cost. The session tells the truth about both; this shows it.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="ChatSession"/> raises its events on the capture thread and its
/// send completes on a pool thread, so everything that touches a view here goes through
/// <see cref="IApplication.Invoke"/>. <see cref="Attach"/> drops the previous session before
/// building one over the new station, because a station that has gone is not going to
/// acknowledge anything.
/// </para>
/// </remarks>
public sealed class ChatActivity : IActivityView
{
    private readonly IApplication _app;
    private readonly Func<ChatOptions> _options;
    private readonly Action<string> _log;
    private readonly ChatTranscript _transcript = new();
    private readonly ObservableCollection<string> _lines = [];
    private readonly Label _header = new();
    private readonly ListView _list = new();
    private readonly TextField _input = new();

    private ChatSession? _session;
    private IStation? _station;
    private int _maxTextBytes;

    /// <summary>Builds the Chat activity. No station until <see cref="Attach"/>.</summary>
    /// <param name="app">The application; every view touch is marshalled through it.</param>
    /// <param name="options">The ARQ settings, read afresh at each attach so a change in the
    /// settings dialog takes effect when the station restarts.</param>
    /// <param name="log">The window's log pane.</param>
    public ChatActivity(IApplication app, Func<ChatOptions> options, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _app = app;
        _options = options;
        _log = log;
        _maxTextBytes = new ChatOptions().MaxTextBytes;

        View = BuildLayout();
    }

    /// <inheritdoc />
    public string Title => "Chat";

    /// <inheritdoc />
    public View View { get; }

    /// <summary>The transcript this pane is showing.</summary>
    public ChatTranscript Transcript => _transcript;

    /// <inheritdoc />
    public void Shown() => _ = _input.SetFocus();

    /// <inheritdoc />
    public void Attach(IStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        Drop();
        _station = station;
        _transcript.Clear();

        ChatOptions options = _options();
        _maxTextBytes = options.MaxTextBytes;
        var session = new ChatSession(station, options);
        session.MessageReceived += OnMessage;
        session.CorrespondentSeen += OnCorrespondent;
        session.WaveformChanged += OnWaveform;
        session.TransmitFailed += OnTransmitFailed;
        _session = session;
        session.Start();

        Redraw();
        RefreshHeader();
    }

    /// <summary>Sends one line, exactly as pressing Enter in the input field does.</summary>
    /// <remarks>
    /// The whole of what Enter does, so that the rule about an empty line, the byte limit and
    /// the delivery tick all live in one place rather than in an event handler.
    /// </remarks>
    /// <param name="text">The line.</param>
    /// <returns>False when there was nothing to send.</returns>
    public bool Send(string text)
    {
        if (_session is not ChatSession session || _station is not IStation station)
        {
            return false;
        }

        string line = ChatPayload.Sanitise(text ?? "");
        if (line.Length == 0)
        {
            // An empty line is a keystroke, not a transmission. Nothing goes on air and
            // nothing is said about it: an operator who hit Enter twice knows what they did.
            return false;
        }

        int bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes > _maxTextBytes)
        {
            Note($"too long: {bytes} bytes, the limit is {_maxTextBytes}");
            return false;
        }

        if (!station.CanTransmit)
        {
            Note("this station is receive only - nothing was sent");
            return false;
        }

        int ticket = _transcript.AddOutgoing(station.Callsign, line, DateTimeOffset.Now);
        Redraw();

        _ = Task.Run(async () =>
        {
            try
            {
                ChatDelivery delivery = await session.SendAsync(line).ConfigureAwait(false);
                _app.Invoke(() =>
                {
                    _transcript.Complete(ticket, delivery);
                    Redraw();
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _app.Invoke(() =>
                {
                    _transcript.Complete(ticket, ChatDelivery.Failed(0, 0));
                    _transcript.AddNote($"send failed: {e.Message}", DateTimeOffset.Now);
                    Redraw();
                });
            }
        });

        return true;
    }

    private View BuildLayout()
    {
        var frame = new View
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),

            // A container that cannot take focus cannot pass it to its children either,
            // so without this the input below is unreachable from the keyboard.
            CanFocus = true,
        };

        _header.X = 0;
        _header.Y = 0;
        _header.Width = Dim.Fill();
        _header.Height = 1;
        _header.Text = HeaderText(null, null, null, _maxTextBytes);

        _list.X = 0;
        _list.Y = 1;
        _list.Width = Dim.Fill();
        _list.Height = Dim.Fill(2);
        _list.SetSource(_lines);

        var prompt = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = 2,
            Height = 1,
            Text = "> ",
        };

        _input.X = 2;
        _input.Y = Pos.AnchorEnd(1);
        _input.Width = Dim.Fill();
        _input.Height = 1;
        _input.Accepting += (_, e) =>
        {
            e.Handled = true;
            string text = _input.Text;
            if (Send(text))
            {
                _input.Text = "";
            }
        };

        frame.Add(_header, _list, prompt, _input);
        return frame;
    }

    /// <summary>The header line: who we are working, what waveform, and the line limit.</summary>
    public static string HeaderText(
        string? callsign, string? correspondent, int? waveform, int maxTextBytes)
    {
        var line = new StringBuilder(80);
        line.Append(string.IsNullOrWhiteSpace(callsign) ? "no station" : callsign);
        line.Append("  with ").Append(
            string.IsNullOrWhiteSpace(correspondent) ? "(nobody yet)" : correspondent);
        if (waveform is int number)
        {
            line.Append(string.Create(CultureInfo.InvariantCulture, $"  waveform {number}"));
        }

        line.Append(string.Create(CultureInfo.InvariantCulture, $"  max {maxTextBytes} bytes/line"));
        return line.ToString();
    }

    private void OnMessage(ChatMessage message) => _app.Invoke(() =>
    {
        _transcript.AddIncoming(message);
        Redraw();
        RefreshHeader();
    });

    private void OnCorrespondent(string callsign) => _app.Invoke(() =>
    {
        _transcript.AddNote($"{callsign} is here", DateTimeOffset.Now);
        Redraw();
        RefreshHeader();
    });

    private void OnWaveform(int waveform, string why) => _app.Invoke(() =>
    {
        string note = string.Create(CultureInfo.InvariantCulture, $"waveform now {waveform}: {why}");
        _transcript.AddNote(note, DateTimeOffset.Now);
        _log($"chat: {note}");
        Redraw();
        RefreshHeader();
    });

    private void OnTransmitFailed(Exception error) => _app.Invoke(() =>
    {
        _transcript.AddNote($"could not transmit: {error.Message}", DateTimeOffset.Now);
        Redraw();
    });

    private void Note(string text)
    {
        _transcript.AddNote(text, DateTimeOffset.Now);
        Redraw();
    }

    private void RefreshHeader() =>
        _header.Text = HeaderText(
            _station?.Callsign,
            _session?.Correspondent ?? Heard(),
            _session?.CurrentWaveform,
            _maxTextBytes);

    /// <summary>
    /// Who has spoken to us, when no correspondent was configured. The session answers whoever
    /// calls, so the header names the last station heard rather than claiming nobody is there.
    /// </summary>
    private string? Heard()
    {
        for (int i = _transcript.Rows.Count - 1; i >= 0; i--)
        {
            if (_transcript.Rows[i].State == ChatRowState.Heard)
            {
                return _transcript.Rows[i].Callsign;
            }
        }

        return null;
    }

    private void Redraw()
    {
        _lines.Clear();
        foreach (string line in _transcript.Lines)
        {
            _lines.Add(line);
        }

        if (_lines.Count > 0)
        {
            _list.SelectedItem = _lines.Count - 1;
            _list.EnsureSelectedItemVisible();
        }
    }

    private void Drop()
    {
        if (_session is not ChatSession session)
        {
            return;
        }

        _session = null;
        session.MessageReceived -= OnMessage;
        session.CorrespondentSeen -= OnCorrespondent;
        session.WaveformChanged -= OnWaveform;
        session.TransmitFailed -= OnTransmitFailed;

        // Not awaited: disposal cancels the pump and waits for it, and the UI thread is not
        // the place to wait for a transmit that may still be keyed. Anything it throws on the
        // way down belongs to a station that has already gone.
        _ = Task.Run(async () =>
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _app.Invoke(() => _log($"chat: closing the old session - {e.Message}"));
            }
        });
    }
}
