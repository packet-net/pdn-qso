using System.Globalization;

namespace PdnQso.Config;

/// <summary>One sound card the kernel knows about.</summary>
/// <param name="Index">The card number, which can change between boots.</param>
/// <param name="Id">The card's id, e.g. <c>Device</c> - stable across boots, which is why the
/// suggested device string is built from it.</param>
/// <param name="Driver">The driver, e.g. <c>USB-Audio</c>.</param>
/// <param name="Name">The card's own name, e.g. <c>USB PnP Sound Device</c>.</param>
public readonly record struct AlsaCard(int Index, string Id, string Driver, string Name)
{
    /// <summary>The device string to offer for this card.</summary>
    /// <remarks>
    /// By id rather than by number: card numbering depends on what the kernel probed first, so
    /// a config written as <c>plughw:1,0</c> can wake up pointing at the motherboard's audio
    /// after somebody plugs in a webcam. And through the plug layer, because that is what makes
    /// the card take a rate and a format it does not natively have.
    /// </remarks>
    public string DeviceString => $"plughw:CARD={Id},DEV=0";

    /// <summary>One line for a list an operator picks from.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Index}: {Name} [{Id}] ({Driver})");
}

/// <summary>What sound cards this machine has, for the first-run wizard's list.</summary>
/// <remarks>
/// Read from <c>/proc/asound/cards</c> rather than shelled out to <c>aplay -l</c>: it is the
/// same information, it is there on every machine with ALSA loaded, and it does not depend on
/// alsa-utils being installed. A machine with no sound at all simply has no such file, and an
/// empty list is the right answer rather than an error.
/// </remarks>
public static class AlsaCards
{
    /// <summary>Where the kernel lists them.</summary>
    public const string ProcPath = "/proc/asound/cards";

    /// <summary>Every card this machine has; empty when it has none, or has no ALSA.</summary>
    public static IReadOnlyList<AlsaCard> List()
    {
        try
        {
            return File.Exists(ProcPath) ? Parse(File.ReadAllText(ProcPath)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the two-line-per-card format <c>/proc/asound/cards</c> uses.
    /// </summary>
    /// <remarks>
    /// The shape is <c>" 1 [Device         ]: USB-Audio - USB PnP Sound Device"</c> followed by
    /// an indented continuation line naming the bus, which is not used. Anything that does not
    /// match is skipped rather than guessed at.
    /// </remarks>
    /// <param name="text">The contents of the file.</param>
    public static IReadOnlyList<AlsaCard> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cards = new List<AlsaCard>();

        foreach (string line in text.Split('\n'))
        {
            int bracket = line.IndexOf('[', StringComparison.Ordinal);
            int close = line.IndexOf(']', StringComparison.Ordinal);
            int colon = close < 0 ? -1 : line.IndexOf(':', close);
            if (bracket <= 0 || close <= bracket || colon < 0)
            {
                continue;
            }

            if (!int.TryParse(
                    line.AsSpan(0, bracket).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int index))
            {
                continue;
            }

            string id = line[(bracket + 1)..close].Trim();
            string rest = line[(colon + 1)..].Trim();
            int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
            string driver = dash < 0 ? rest : rest[..dash];
            string name = dash < 0 ? rest : rest[(dash + 3)..];

            cards.Add(new AlsaCard(index, id, driver.Trim(), name.Trim()));
        }

        return cards;
    }
}
