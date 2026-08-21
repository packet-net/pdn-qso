using Packet.SoundModem.Modems;

namespace PdnQso.Link.Chat;

/// <summary>
/// The MS110D waveform lever, with a rule about when to pull it: down a step when the link
/// stops working, back up a step when it has been working for a while.
/// </summary>
/// <remarks>
/// <para>
/// docs/design.md section 3: the ladder is 8 -> 7 -> 6 -> 5 -> 4 -> 2, each step slower and
/// more robust than the last. Nothing is negotiated, because there is nothing to negotiate:
/// an MS110D receiver is autobaud and reads whichever Phase A waveform arrives. The far end
/// learns what we did from the waveform byte in the next chat frame's payload, which is for
/// the operator's benefit rather than the modem's.
/// </para>
/// <para>
/// <b>Only ever MS110D.</b> The lever is <c>IHardwareControllable</c>, whose payload is
/// defined by the modem that implements it - KISS SETHW is hardware-specific by definition.
/// A different modem could implement the same interface with an entirely different byte
/// layout, so a ladder that stepped on "it is controllable" would eventually send a waveform
/// number to something that read it as an audio level. This one therefore also requires the
/// modem to report an <c>ms110d-wn*</c> mode, and is disabled otherwise.
/// </para>
/// <para>
/// The ladder assumes it owns the lever: it remembers where it is rather than re-reading the
/// modem, so something else moving the waveform underneath it (an operator in the settings
/// dialog, mid-QSO) leaves it a step out of date until its next move.
/// </para>
/// </remarks>
public sealed class WaveformLadder
{
    /// <summary>The mode-name prefix a waveform-ladder modem reports.</summary>
    public const string ModePrefix = "ms110d-wn";

    private readonly IHardwareControllable? _control;
    private readonly int[] _steps;

    /// <summary>
    /// Builds a ladder over a modem's hardware lever.
    /// </summary>
    /// <param name="control">The modem's SETHW interface, or null for a modem that has none.</param>
    /// <param name="mode">The modem's mode as it reports it now; the ladder is disabled unless
    /// this is an <c>ms110d-wn*</c> mode.</param>
    /// <param name="steps">The ladder, most capable first; the default when omitted.</param>
    public WaveformLadder(IHardwareControllable? control, string? mode, IReadOnlyList<int>? steps = null)
    {
        _steps = [.. steps ?? DefaultSteps];
        if (_steps.Length == 0)
        {
            throw new ArgumentException("a ladder with no steps cannot step", nameof(steps));
        }

        if (control is not null && TryReadWaveform(mode, out int waveform))
        {
            _control = control;
            Current = waveform;
            Enabled = true;
        }
    }

    /// <summary>
    /// The ladder of docs/design.md section 3, most capable first: 8, 7, 6, 5, 4, 2.
    /// </summary>
    public static IReadOnlyList<int> DefaultSteps { get; } = [8, 7, 6, 5, 4, 2];

    /// <summary>Raised after the waveform has actually moved: the new number and why.</summary>
    public event Action<int, string>? Changed;

    /// <summary>True when this modem has a ladder to climb at all.</summary>
    public bool Enabled { get; }

    /// <summary>The transmit waveform now, or -1 when <see cref="Enabled"/> is false.</summary>
    public int Current { get; private set; } = -1;

    /// <summary><see cref="Current"/> as the wire and the UI want it: null for no ladder.</summary>
    public int? CurrentOrNull => Enabled ? Current : null;

    /// <summary>What the modem said the last time the lever moved, for the log.</summary>
    public string LastOutcome { get; private set; } = "";

    /// <summary>How many waveforms the modem has refused, over the life of this ladder.</summary>
    public int Refusals { get; private set; }

    /// <summary>
    /// Builds the ladder for a station, if its modem has one.
    /// </summary>
    /// <remarks>
    /// Reaches through <see cref="Station.Modem"/> because that is where the lever is;
    /// <see cref="IStation"/> deliberately talks only in link frames. A station of some other
    /// implementation gets a disabled ladder, which is the right answer rather than a failure.
    /// </remarks>
    public static WaveformLadder ForStation(IStation station, IReadOnlyList<int>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        IModem? modem = station is Station concrete ? concrete.Modem : null;
        return new WaveformLadder(modem as IHardwareControllable, modem?.Mode, steps);
    }

    /// <summary>A ladder that does nothing, for a modem that has no lever.</summary>
    public static WaveformLadder Disabled(IReadOnlyList<int>? steps = null) =>
        new(control: null, mode: null, steps);

    /// <summary>
    /// Steps to the next more robust waveform, skipping any the modem refuses.
    /// </summary>
    /// <param name="reason">Why, for <see cref="Changed"/> and the operator's log.</param>
    /// <returns>False when there is no ladder, or when the bottom of it has been reached.</returns>
    public bool TryStepDown(string reason) => TryStep(down: true, reason);

    /// <summary>
    /// Steps back up one, skipping any the modem refuses.
    /// </summary>
    /// <param name="reason">Why, for <see cref="Changed"/> and the operator's log.</param>
    /// <returns>False when there is no ladder, or when it is already at the top.</returns>
    public bool TryStepUp(string reason) => TryStep(down: false, reason);

    /// <summary>Reads the waveform number out of a mode name, e.g. <c>ms110d-wn6</c>.</summary>
    public static bool TryReadWaveform(string? mode, out int waveform)
    {
        waveform = -1;
        return mode is not null
            && mode.StartsWith(ModePrefix, StringComparison.Ordinal)
            && int.TryParse(
                mode.AsSpan(ModePrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out waveform);
    }

    private bool TryStep(bool down, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (!Enabled)
        {
            return false;
        }

        int from = Current;
        while (true)
        {
            int next = down ? LargestBelow(from) : SmallestAbove(from);
            if (next < 0)
            {
                return false;
            }

            // One byte: the waveform number. The interleaver byte is deliberately not sent -
            // the ARQ has no opinion about it, and sending a second byte would overwrite
            // whatever the operator chose in the settings dialog.
            Span<byte> payload = [(byte)next];
            if (_control!.TrySetHardware(payload, out string outcome))
            {
                Current = next;
                LastOutcome = outcome;
                Changed?.Invoke(next, reason);
                return true;
            }

            // Refused: carry on past it rather than give up. A modem that will not do wn7
            // can still do wn6, and a link that needs a step down needs one now.
            Refusals++;
            LastOutcome = outcome;
            from = next;
        }
    }

    /// <summary>
    /// The next step down from <paramref name="waveform"/>: the highest ladder entry below it.
    /// </summary>
    /// <remarks>
    /// Stated as a comparison rather than an index so that a waveform which is not on the
    /// ladder at all still has an answer - wn13 steps down to 8, wn3 to 2 - instead of
    /// stranding a station that started somewhere the ladder does not mention.
    /// </remarks>
    private int LargestBelow(int waveform)
    {
        int best = -1;
        foreach (int step in _steps)
        {
            if (step < waveform && step > best)
            {
                best = step;
            }
        }

        return best;
    }

    /// <summary>The next step up: the lowest ladder entry above <paramref name="waveform"/>.</summary>
    private int SmallestAbove(int waveform)
    {
        int best = int.MaxValue;
        foreach (int step in _steps)
        {
            if (step > waveform && step < best)
            {
                best = step;
            }
        }

        return best == int.MaxValue ? -1 : best;
    }
}
