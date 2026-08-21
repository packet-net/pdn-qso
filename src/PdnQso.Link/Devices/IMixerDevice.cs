namespace PdnQso.Link.Devices;

/// <summary>
/// One playback volume control on a sound card, as <see cref="MixerPowerControl"/> needs it:
/// a name, a raw range, the value now, and the dB the card claims for that value.
/// </summary>
/// <remarks>
/// The seam exists so the arithmetic and the operator-facing wording can be tested on a
/// machine with no sound card - which includes CI, and includes this development box. The one
/// implementation that talks to hardware is <see cref="AlsaSimpleMixer"/>, and everything
/// interesting about it is on the far side of a P/Invoke that a test cannot call.
/// </remarks>
public interface IMixerDevice : IDisposable
{
    /// <summary>The mixer card this control belongs to, e.g. <c>hw:1</c>.</summary>
    string Card { get; }

    /// <summary>The control's name as the card reports it - discovered, never assumed.</summary>
    string ElementName { get; }

    /// <summary>The lowest raw value the control takes.</summary>
    long Minimum { get; }

    /// <summary>The highest raw value the control takes.</summary>
    long Maximum { get; }

    /// <summary>The raw value now. Setting it sets every channel of the control.</summary>
    long Volume { get; set; }

    /// <summary>
    /// The dB the card reports for the current value, or <see langword="null"/> when the
    /// control has no dB scale. Plenty of USB cards do not, and inventing a number for them
    /// would be worse than showing the percentage alone.
    /// </summary>
    double? Decibels { get; }
}

/// <summary>One playback control found on a card, before one of them is chosen.</summary>
/// <param name="Name">The control's name, e.g. <c>Master</c>, <c>PCM</c>, <c>Speaker</c>.</param>
/// <param name="Index">Its index, for the cards that have two controls of one name.</param>
/// <param name="HasPlaybackVolume">False for a control that only mutes, or that is capture
/// side; those are no use as a power control.</param>
public readonly record struct MixerElementInfo(string Name, int Index, bool HasPlaybackVolume);

/// <summary>Which of a card's controls is the one to drive as transmit power.</summary>
public static class MixerElement
{
    /// <summary>
    /// Picks the playback control to use, or <see langword="null"/> when the card has none.
    /// </summary>
    /// <remarks>
    /// The first control that has a playback volume, in the order ALSA's simple mixer lists
    /// them - which is by the card's own weighting, so the primary output control comes first.
    /// Deliberately not a hunt for a name we like: a card whose control is called
    /// <c>Speaker</c>, or <c>PCM</c>, or something the vendor made up, all work, and a rule
    /// that looks for a spelling silently fails on the fourth card it meets.
    /// </remarks>
    /// <param name="elements">Every control on the card, in the card's order.</param>
    public static MixerElementInfo? Choose(IReadOnlyList<MixerElementInfo> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].HasPlaybackVolume)
            {
                return elements[i];
            }
        }

        return null;
    }
}

/// <summary>Turning an ALSA PCM device name into the mixer card that goes with it.</summary>
public static class MixerCard
{
    /// <summary>
    /// The mixer card name for a PCM device string: <c>plughw:1,0</c> is mixed by
    /// <c>hw:1</c>, <c>hw:CARD=Device,DEV=0</c> by <c>hw:CARD=Device</c>, and <c>default</c>
    /// mixes as <c>default</c>.
    /// </summary>
    /// <remarks>
    /// The PCM name selects a stream (a card and a device on it, possibly through the plug
    /// layer); the mixer name selects a control interface (a card, and nothing else). Passing
    /// a PCM name straight to <c>snd_mixer_attach</c> gets "No such file or directory" on the
    /// forms that carry a device number, which is most of the ones an operator will type.
    /// </remarks>
    /// <param name="device">The ALSA device string, with or without an <c>alsa:</c> prefix.</param>
    public static string ForDevice(string device)
    {
        ArgumentNullException.ThrowIfNull(device);
        string name = device.Trim();
        if (name.StartsWith("alsa:", StringComparison.OrdinalIgnoreCase))
        {
            name = name["alsa:".Length..];
        }

        if (name.Length == 0 || name.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        // plughw: is the plug layer wrapped round hw:; the controls belong to the card either
        // way, and there is no such thing as a plughw mixer.
        if (name.StartsWith("plughw:", StringComparison.OrdinalIgnoreCase))
        {
            name = "hw:" + name["plughw:".Length..];
        }
        else if (!name.StartsWith("hw:", StringComparison.OrdinalIgnoreCase))
        {
            // sysdefault:CARD=x, dmix:CARD=x, or a name from ~/.asoundrc. Take the CARD= if
            // there is one and leave anything else alone rather than guess.
            int card = name.IndexOf("CARD=", StringComparison.OrdinalIgnoreCase);
            if (card < 0)
            {
                return name;
            }

            name = "hw:" + name[card..];
        }

        // Drop the device and subdevice: hw:1,0 -> hw:1, hw:CARD=Device,DEV=0 -> hw:CARD=Device.
        int comma = name.IndexOf(',', StringComparison.Ordinal);
        return comma < 0 ? name : name[..comma];
    }
}
