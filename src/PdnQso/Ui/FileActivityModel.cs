using System.Globalization;
using System.Text;
using PdnQso.Link.Transfer;

namespace PdnQso.Ui;

/// <summary>
/// The File pane's model: where each direction has got to, and the running record of offers,
/// results and failures.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like <see cref="ChatTranscript"/> and for the same reason. What is worth testing about
/// a file transfer pane is that a sender's bar moves with the receiver's reported count and not
/// with its own optimism, that an offer heard is shown whether or not it was taken, and that a
/// transfer which failed leaves a line saying so. A terminal is not needed for any of it.
/// </para>
/// <para>
/// The two directions are independent: this station can be pouring symbols at the far end while
/// the far end is pouring different ones back, and the pane has a bar for each. A progress
/// report for the direction that is not running is simply absent, which is what
/// <see cref="Sending"/> and <see cref="Receiving"/> being null means.
/// </para>
/// <para>
/// Not thread safe: the view owns one and touches it on the UI thread only.
/// </para>
/// </remarks>
public sealed class FileActivityModel
{
    /// <summary>How many event lines are kept before the oldest fall off.</summary>
    public const int DefaultCapacity = 200;

    private readonly List<string> _lines = [];
    private readonly int _capacity;

    /// <summary>Builds a model.</summary>
    /// <param name="capacity">How many event lines to keep.</param>
    public FileActivityModel(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>The last progress report from this station's own transfer out, or null.</summary>
    public FileProgress? Sending { get; private set; }

    /// <summary>The last progress report from the transfer coming in, or null.</summary>
    public FileProgress? Receiving { get; private set; }

    /// <summary>Offers heard, results and failures, oldest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Where the outgoing bar should be, 0 to 1.</summary>
    public double SendFraction => Sending?.Fraction ?? 0;

    /// <summary>Where the incoming bar should be, 0 to 1.</summary>
    public double ReceiveFraction => Receiving?.Fraction ?? 0;

    /// <summary>The one-line description of the transfer out.</summary>
    public string SendLine => Describe("send", Sending);

    /// <summary>The one-line description of the transfer in.</summary>
    public string ReceiveLine => Describe("recv", Receiving);

    /// <summary>Drops everything: called when the station is replaced.</summary>
    public void Clear()
    {
        Sending = null;
        Receiving = null;
        _lines.Clear();
    }

    /// <summary>Takes a progress report from either end.</summary>
    public void Note(FileProgress progress)
    {
        if (progress.Role == FileTransferRole.Sender)
        {
            Sending = progress;
        }
        else
        {
            Receiving = progress;
        }
    }

    /// <summary>Records an offer this station heard, and whether it was taken.</summary>
    public void NoteOffer(FileOfferPayload offer, bool accepted, DateTimeOffset at) =>
        Add(at, string.Create(
            CultureInfo.InvariantCulture,
            $"offer {offer.Name}, {offer.Length} bytes in {offer.BlockCount} blocks of "
            + $"{offer.BlockSize} - {(accepted ? "accepting" : "not accepting")}"));

    /// <summary>Records what a transfer came to, from <see cref="FileTransferResult.ToString"/>.</summary>
    public void NoteResult(FileTransferResult result, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);
        string what = result.Role == FileTransferRole.Sender ? "sent" : "received";
        Add(at, $"{what} {result}");
        if (result.Role == FileTransferRole.Sender)
        {
            Sending = null;
        }
        else
        {
            Receiving = null;
        }
    }

    /// <summary>Records a transfer that gave up.</summary>
    public void NoteFailure(FileTransferRole role, string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Add(at, $"{(role == FileTransferRole.Sender ? "send" : "receive")} failed: {reason}");
        if (role == FileTransferRole.Sender)
        {
            Sending = null;
        }
        else
        {
            Receiving = null;
        }
    }

    /// <summary>Records anything else worth a line.</summary>
    public void NoteLine(string text, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(text);
        Add(at, text);
    }

    /// <summary>Renders one direction's progress line.</summary>
    /// <param name="what"><c>send</c> or <c>recv</c>.</param>
    /// <param name="progress">The last report, or null when nothing is running.</param>
    public static string Describe(string what, FileProgress? progress)
    {
        if (progress is not FileProgress p)
        {
            return $"{what}: idle";
        }

        var line = new StringBuilder(96);
        line.Append(what).Append(' ').Append(p.Name).Append("  ");
        line.Append(string.Create(CultureInfo.InvariantCulture, $"sym {p.Symbols}"));
        line.Append(string.Create(
            CultureInfo.InvariantCulture, $"  decoded {p.Decoded}/{p.BlockCount}"));
        line.Append(string.Create(CultureInfo.InvariantCulture, $"  {p.Fraction:0%}"));
        line.Append(string.Create(CultureInfo.InvariantCulture, $"  {p.BytesPerSecond:0.0} B/s"));
        return line.ToString();
    }

    private void Add(DateTimeOffset at, string text)
    {
        _lines.Add($"{at.ToLocalTime():HH:mm:ss} {text}");
        while (_lines.Count > _capacity)
        {
            _lines.RemoveAt(0);
        }
    }
}
