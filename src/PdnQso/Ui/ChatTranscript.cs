using System.Globalization;
using System.Text;
using PdnQso.Link.Chat;

namespace PdnQso.Ui;

/// <summary>What became of a line this station sent.</summary>
public enum ChatRowState
{
    /// <summary>Not ours: a line the far station sent us.</summary>
    Heard,

    /// <summary>Ours, on air or waiting for the acknowledgement.</summary>
    Sending,

    /// <summary>Ours, acknowledged.</summary>
    Delivered,

    /// <summary>Ours, and the attempts ran out.</summary>
    Failed,

    /// <summary>Not a line at all: something the session said about itself.</summary>
    Note,
}

/// <summary>
/// One line of the Chat transcript: when, who, what, and the delivery tick.
/// </summary>
/// <param name="At">When it was typed or heard.</param>
/// <param name="Callsign">Who sent it; empty for a note.</param>
/// <param name="Text">The line itself.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="Attempts">How many times it was sent; 0 before the outcome is known.</param>
/// <param name="RoundTrip">How long the attempt that landed took.</param>
/// <param name="SnrDb">The SNR of a heard line, where the decode reported one.</param>
/// <param name="Waveform">The MS110D waveform a heard line arrived on, where there is one.</param>
public readonly record struct ChatRow(
    DateTimeOffset At,
    string Callsign,
    string Text,
    ChatRowState State,
    int Attempts,
    TimeSpan RoundTrip,
    double? SnrDb,
    int? Waveform)
{
    /// <summary>How wide the callsign column is.</summary>
    public const int CallsignWidth = 9;

    /// <summary>One line for the transcript pane. ASCII throughout.</summary>
    public string Render()
    {
        var line = new StringBuilder(96);
        line.Append(At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ');

        if (State == ChatRowState.Note)
        {
            return line.Append("-- ").Append(Text).ToString();
        }

        line.Append(Callsign.Length >= CallsignWidth
            ? Callsign[..CallsignWidth]
            : Callsign.PadRight(CallsignWidth));
        line.Append(' ').Append(Text);

        string tick = Tick();
        if (tick.Length > 0)
        {
            line.Append("  [").Append(tick).Append(']');
        }

        return line.ToString();
    }

    /// <summary>
    /// The delivery tick: what this line's state is worth saying, and nothing more.
    /// </summary>
    /// <remarks>
    /// The attempt count is on the tick because it is the cheapest honest measure of how the
    /// path is doing: a conversation whose every line lands first time and one whose every line
    /// lands on the third go look identical without it, and they are not the same link.
    /// </remarks>
    public string Tick() => State switch
    {
        ChatRowState.Sending => "sending",
        ChatRowState.Delivered => string.Create(
            CultureInfo.InvariantCulture,
            $"ok, {Tries(Attempts)}, {RoundTrip.TotalSeconds:0.0} s"),
        ChatRowState.Failed => $"failed after {Tries(Attempts)}",
        ChatRowState.Heard => HeardTick(),
        _ => "",
    };

    private string HeardTick()
    {
        var parts = new List<string>(2);
        if (SnrDb is double snr)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"snr {snr:0.0} dB"));
        }

        if (Waveform is int waveform)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"wf {waveform}"));
        }

        return string.Join(", ", parts);
    }

    private static string Tries(int attempts) =>
        attempts == 1 ? "1 try" : $"{attempts} tries";
}

/// <summary>
/// The Chat pane's model: the transcript, and what the delivery ticks say.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the view for the same reason <see cref="MonitorLine"/> is: the part
/// of Chat that is worth testing is that a line sent shows as sending and then as delivered
/// with the attempts it really took, that a line that failed says so rather than quietly
/// disappearing, and that a duplicate the session suppressed never reaches the screen at all.
/// None of that needs a terminal.
/// </para>
/// <para>
/// An outgoing line gets a ticket from <see cref="AddOutgoing"/> and the outcome is posted
/// against that ticket later, because a chat line is stop-and-wait but the UI is not: the
/// operator can be typing the next one while the last is still being retried.
/// </para>
/// <para>
/// Not thread safe. The view owns one of these and touches it only on the UI thread; the
/// station's events reach it through <c>IApplication.Invoke</c>.
/// </para>
/// </remarks>
public sealed class ChatTranscript
{
    /// <summary>How many lines the transcript keeps before the oldest fall off.</summary>
    public const int DefaultCapacity = 1000;

    private readonly List<ChatRow> _rows = [];
    private readonly Dictionary<int, int> _tickets = [];
    private readonly int _capacity;
    private int _nextTicket;
    private int _dropped;

    /// <summary>Builds a transcript.</summary>
    /// <param name="capacity">How many lines to keep.</param>
    public ChatTranscript(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>The transcript, oldest first.</summary>
    public IReadOnlyList<ChatRow> Rows => _rows;

    /// <summary>Every row rendered, in order.</summary>
    public IEnumerable<string> Lines => _rows.Select(r => r.Render());

    /// <summary>Everything: the transcript, the tickets, the counters.</summary>
    /// <remarks>
    /// Called on <c>Attach</c>. A transcript is a record of one conversation over one station,
    /// and carrying it across a station restart would leave delivery ticks on the screen that
    /// nothing can ever resolve.
    /// </remarks>
    public void Clear()
    {
        _rows.Clear();
        _tickets.Clear();
        _nextTicket = 0;
        _dropped = 0;
    }

    /// <summary>Adds a line this station is sending, and returns the ticket for its outcome.</summary>
    /// <param name="callsign">This station's callsign.</param>
    /// <param name="text">The line.</param>
    /// <param name="at">When it was typed.</param>
    public int AddOutgoing(string callsign, string text, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(text);

        int ticket = _nextTicket++;
        _tickets[ticket] = _rows.Count + _dropped;
        Add(new ChatRow(at, callsign, text, ChatRowState.Sending, 0, TimeSpan.Zero, null, null));
        return ticket;
    }

    /// <summary>Posts what became of a line, against the ticket <see cref="AddOutgoing"/> gave.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <param name="delivery">What the session came back with.</param>
    /// <returns>False when the row has already fallen off the end of the transcript.</returns>
    public bool Complete(int ticket, ChatDelivery delivery)
    {
        if (!_tickets.TryGetValue(ticket, out int absolute))
        {
            return false;
        }

        _tickets.Remove(ticket);
        int index = absolute - _dropped;
        if (index < 0 || index >= _rows.Count)
        {
            return false;
        }

        _rows[index] = _rows[index] with
        {
            State = delivery.IsDelivered ? ChatRowState.Delivered : ChatRowState.Failed,
            Attempts = delivery.Attempts,
            RoundTrip = delivery.RoundTrip,
        };
        return true;
    }

    /// <summary>Adds a line heard from the far station.</summary>
    public void AddIncoming(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Add(new ChatRow(
            message.ReceivedAt,
            message.Source,
            message.Text,
            ChatRowState.Heard,
            Attempts: 0,
            RoundTrip: TimeSpan.Zero,
            SnrDb: message.Quality.SnrDb,
            Waveform: message.Waveform));
    }

    /// <summary>Adds a line about the conversation rather than in it.</summary>
    public void AddNote(string text, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(text);
        Add(new ChatRow(at, "", text, ChatRowState.Note, 0, TimeSpan.Zero, null, null));
    }

    private void Add(ChatRow row)
    {
        _rows.Add(row);
        while (_rows.Count > _capacity)
        {
            _rows.RemoveAt(0);
            _dropped++;
        }
    }
}
