using System.Runtime.InteropServices;

namespace PdnQso.Link.Devices;

/// <summary>
/// libasound's simple mixer API, through P/Invoke: open the card, load its controls, find the
/// one that has a playback volume, and read and write it.
/// </summary>
/// <remarks>
/// <para>
/// This is the transmit power of a sound-card station (design.md section 4a). Everything about
/// the card is discovered - the control's name, its raw range, whether it has a dB scale at
/// all - because the alternative is a hard-coded "PCM" that works on the developer's card and
/// silently does nothing on the operator's.
/// </para>
/// <para>
/// Nothing here can be exercised without a sound card, which is why the arithmetic and the
/// wording live behind <see cref="IMixerDevice"/> in <see cref="MixerPowerControl"/> and are
/// tested against a fake. What is untested off hardware is this file: the P/Invoke signatures
/// and the open sequence.
/// </para>
/// <para>
/// The sequence is libasound's documented one: <c>snd_mixer_open</c>,
/// <c>snd_mixer_attach</c> to the card, <c>snd_mixer_selem_register</c> to get the simple
/// element class, <c>snd_mixer_load</c> to populate it, then walk the elements.
/// </para>
/// </remarks>
public sealed class AlsaSimpleMixer : IMixerDevice
{
    private const string Library = "libasound.so.2";

    /// <summary>The channel a single-channel read uses: SND_MIXER_SCHN_FRONT_LEFT.</summary>
    private const int FrontLeft = 0;

    private nint _mixer;
    private nint _element;
    private bool _disposed;

    private AlsaSimpleMixer(nint mixer, nint element, string card, string name, long min, long max)
    {
        _mixer = mixer;
        _element = element;
        Card = card;
        ElementName = name;
        Minimum = min;
        Maximum = max;
    }

    /// <summary>
    /// Opens the mixer of the card behind an ALSA device string and picks its playback control.
    /// </summary>
    /// <param name="device">The PCM device string, e.g. <c>plughw:1,0</c> or <c>default</c>;
    /// the card it names is what gets attached (<see cref="MixerCard.ForDevice"/>).</param>
    /// <exception cref="InvalidOperationException">The card has no playback control, or
    /// libasound refused one of the steps. The message says which step and what the card was.</exception>
    /// <exception cref="DllNotFoundException">libasound is not installed.</exception>
    public static AlsaSimpleMixer Open(string device)
    {
        string card = MixerCard.ForDevice(device);
        nint mixer = 0;
        try
        {
            Check(snd_mixer_open(out mixer, 0), "open the mixer", card);
            Check(snd_mixer_attach(mixer, card), $"attach the mixer to {card}", card);
            Check(snd_mixer_selem_register(mixer, 0, 0), "register the simple mixer", card);
            Check(snd_mixer_load(mixer), $"load the controls of {card}", card);

            List<MixerElementInfo> found = ListElements(mixer, out List<nint> handles);
            if (MixerElement.Choose(found) is not MixerElementInfo chosen)
            {
                throw new InvalidOperationException(
                    $"{card} has no playback volume control, so there is no audio drive to set "
                    + "here. Set the power on the radio, or point --device at the card that "
                    + $"feeds the transmitter. Controls seen: {Describe(found)}.");
            }

            nint element = handles[found.IndexOf(chosen)];
            Check(
                snd_mixer_selem_get_playback_volume_range(element, out long min, out long max),
                $"read the range of '{chosen.Name}'", card);

            var opened = new AlsaSimpleMixer(mixer, element, card, chosen.Name, min, max);
            mixer = 0;
            return opened;
        }
        finally
        {
            if (mixer != 0)
            {
                snd_mixer_close(mixer);
            }
        }
    }

    /// <inheritdoc />
    public string Card { get; }

    /// <inheritdoc />
    public string ElementName { get; }

    /// <inheritdoc />
    public long Minimum { get; }

    /// <inheritdoc />
    public long Maximum { get; }

    /// <inheritdoc />
    public long Volume
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The card's own state can change under us - somebody else's alsamixer, a desktop
            // volume key - so the handle is refreshed before every read rather than cached.
            snd_mixer_handle_events(_mixer);
            Check(
                snd_mixer_selem_get_playback_volume(_element, FrontLeft, out long value),
                $"read the '{ElementName}' volume", Card);
            return value;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Check(
                snd_mixer_selem_set_playback_volume_all(_element, value),
                $"set the '{ElementName}' volume", Card);
        }
    }

    /// <inheritdoc />
    public double? Decibels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (snd_mixer_selem_has_playback_volume(_element) == 0)
            {
                return null;
            }

            // Hundredths of a dB, and a negative return means the control has no dB scale -
            // common on USB cards. Null rather than a made-up figure.
            return snd_mixer_selem_get_playback_dB(_element, FrontLeft, out long hundredths) < 0
                ? null
                : hundredths / 100.0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _element = 0;
        if (_mixer != 0)
        {
            snd_mixer_close(_mixer);
            _mixer = 0;
        }
    }

    private static List<MixerElementInfo> ListElements(nint mixer, out List<nint> handles)
    {
        var found = new List<MixerElementInfo>();
        handles = [];
        for (nint element = snd_mixer_first_elem(mixer);
             element != 0;
             element = snd_mixer_elem_next(element))
        {
            string name = Marshal.PtrToStringAnsi(snd_mixer_selem_get_name(element)) ?? "";
            found.Add(new MixerElementInfo(
                name,
                (int)snd_mixer_selem_get_index(element),
                snd_mixer_selem_has_playback_volume(element) != 0));
            handles.Add(element);
        }

        return found;
    }

    private static string Describe(IReadOnlyList<MixerElementInfo> elements) =>
        elements.Count == 0 ? "none" : string.Join(", ", elements.Select(e => e.Name));

    private static void Check(int result, string what, string card)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"could not {what} on {card}: ALSA returned {result} "
                + $"({Marshal.PtrToStringAnsi(snd_strerror(result)) ?? "no message"})");
        }
    }

    // DllImport rather than LibraryImport throughout: the source generator behind the newer
    // attribute emits unsafe code, and this library does not otherwise need unsafe on.
    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_open(out nint mixer, int mode);

    [DllImport(Library, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int snd_mixer_attach(nint mixer, string card);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_selem_register(nint mixer, nint options, nint classp);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_load(nint mixer);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_handle_events(nint mixer);

    [DllImport(Library, SetLastError = true)]
    private static extern nint snd_mixer_first_elem(nint mixer);

    [DllImport(Library, SetLastError = true)]
    private static extern nint snd_mixer_elem_next(nint element);

    [DllImport(Library, SetLastError = true)]
    private static extern nint snd_mixer_selem_get_name(nint element);

    [DllImport(Library, SetLastError = true)]
    private static extern uint snd_mixer_selem_get_index(nint element);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_selem_has_playback_volume(nint element);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_selem_get_playback_volume_range(
        nint element, out long min, out long max);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_selem_get_playback_volume(
        nint element, int channel, out long value);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_selem_set_playback_volume_all(nint element, long value);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_selem_get_playback_dB(
        nint element, int channel, out long value);

    [DllImport(Library, SetLastError = true)]
    private static extern int snd_mixer_close(nint mixer);

    [DllImport(Library)]
    private static extern nint snd_strerror(int error);
}
