using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.UberSdr;

namespace PdnQso.Link.Devices;

/// <summary>Which kind of radio a device string names.</summary>
public enum DeviceKind
{
    /// <summary>An ALSA sound card: <c>default</c>, <c>alsa:plughw:1,0</c>, <c>hw:CARD=Device</c>.</summary>
    Alsa,

    /// <summary>A FlexRadio 6000-series over the LAN: <c>flex:&lt;radio&gt;[:slice][@station]</c>.</summary>
    Flex,

    /// <summary>A public UberSDR web receiver, receive only: <c>ubersdr:&lt;instance&gt;</c>.</summary>
    UberSdr,

    /// <summary>Two named pipes, for two instances on one machine: <c>pipe:&lt;in&gt;,&lt;out&gt;[,&lt;rate&gt;]</c>.</summary>
    Pipe,
}

/// <summary>
/// A parsed <c>--device</c> string, in the same four forms pdn-soundmodem's daemon accepts, so
/// a string that works there works here and an operator has one thing to learn.
/// </summary>
/// <remarks>
/// <para>
/// The <c>flex:</c> and <c>ubersdr:</c> forms are handed to the library's own parsers
/// (<see cref="FlexDevice.Parse"/>, <see cref="UberSdrDevice.Parse"/>) rather than re-read
/// here, so the grammar cannot drift between the two programs. The <c>pipe:</c> parser is
/// internal to the daemon and is reimplemented below against its documented spelling.
/// </para>
/// <para>
/// Anything without one of the four prefixes is an ALSA device name, which is how the daemon
/// treats it: <c>default</c>, <c>hw:1,0</c> and <c>plughw:CARD=Device,DEV=0</c> all get passed
/// to ALSA as they stand. A typo in one is therefore caught when the card is opened and not
/// here, which is the honest place for it - this parser has no idea what cards exist.
/// </para>
/// <para>
/// Building the actual device from one of these is phase A2's job; phase A only reads the
/// string, so the wizard and the settings dialog can validate what they are given.
/// </para>
/// </remarks>
/// <param name="Text">The device string as the operator wrote it.</param>
public abstract record DeviceString(string Text)
{
    /// <summary>Which of the four forms this is.</summary>
    public abstract DeviceKind Kind { get; }

    /// <summary>False for a receive-only device.</summary>
    public abstract bool CanTransmit { get; }

    /// <inheritdoc />
    public sealed override string ToString() => Text;

    /// <summary>Parses a device string.</summary>
    /// <exception cref="FormatException">It is not one of the four forms, and the message says
    /// what the form should have looked like.</exception>
    public static DeviceString Parse(string text) =>
        TryParse(text, out DeviceString? device, out string? error)
            ? device
            : throw new FormatException(error);

    /// <summary>
    /// Parses a device string, or explains in one operator-facing line why it will not do.
    /// </summary>
    /// <param name="text">The string to parse.</param>
    /// <param name="device">The parsed device.</param>
    /// <param name="error">Why it was refused - printable ASCII, ready to show.</param>
    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out DeviceString? device,
        [NotNullWhen(false)] out string? error)
    {
        device = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "no device. It is one of: an ALSA card (default, plughw:1,0), "
                + "flex:<radio>[:slice][@station], ubersdr:<instance>, or "
                + "pipe:<in>,<out>[,<rate>].";
            return false;
        }

        string trimmed = text.Trim();

        if (FlexDevice.IsFlex(trimmed))
        {
            FlexDevice.FlexSpec spec = FlexDevice.Parse(trimmed);
            if (string.IsNullOrWhiteSpace(spec.RadioSpec))
            {
                error = $"\"{trimmed}\" names no radio. It is flex:<radio>[:slice][@station] - "
                    + "the radio being discover, an IP address, serial=<n>, or mock.";
                return false;
            }

            device = new FlexDeviceString(trimmed, spec.RadioSpec, spec.SliceLetter, spec.Station);
            return true;
        }

        if (UberSdrDevice.IsUberSdr(trimmed))
        {
            try
            {
                UberSdrEndpoint endpoint = UberSdrDevice.Parse(trimmed);
                device = new UberSdrDeviceString(trimmed, endpoint.Host, endpoint.Port, endpoint.Ssl);
                return true;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException)
            {
                error = e.Message;
                return false;
            }
        }

        if (trimmed.StartsWith(PipePrefix, StringComparison.Ordinal))
        {
            return TryParsePipe(trimmed, out device, out error);
        }

        device = new AlsaDeviceString(trimmed);
        return true;
    }

    private const string PipePrefix = "pipe:";

    private static bool TryParsePipe(
        string text,
        [NotNullWhen(true)] out DeviceString? device,
        [NotNullWhen(false)] out string? error)
    {
        device = null;
        error = null;

        // The daemon's PipeAudio.Parse spelling, which is internal to it: two FIFO paths and an
        // optional sample rate. Station A takes the reverse of station B's.
        string[] parts = text[PipePrefix.Length..].Split(',');
        if (parts.Length is < 2 or > 3
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            error = $"\"{text}\" is not a pipe device. It is pipe:<in>,<out>[,<rate>] - the FIFO "
                + "to read capture audio from, the FIFO to write transmit audio to, and "
                + "optionally the sample rate (48000 by default). Two stations take each "
                + "other's reversed.";
            return false;
        }

        int rate = 48000;
        if (parts.Length == 3 && !int.TryParse(
                parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out rate))
        {
            error = $"\"{parts[2]}\" is not a sample rate";
            return false;
        }

        if (rate <= 0)
        {
            error = $"\"{parts[2]}\" is not a sample rate";
            return false;
        }

        device = new PipeDeviceString(text, parts[0], parts[1], rate);
        return true;
    }
}

/// <summary>An ALSA sound card, named exactly as ALSA names it.</summary>
/// <param name="Text">The string as written, <c>alsa:</c> prefix and all.</param>
public sealed record AlsaDeviceString(string Text) : DeviceString(Text)
{
    private const string Prefix = "alsa:";

    /// <summary>The card name ALSA is given, e.g. <c>default</c> or <c>plughw:1,0</c>.</summary>
    public string Card { get; } =
        Text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? Text[Prefix.Length..] : Text;

    /// <inheritdoc />
    public override DeviceKind Kind => DeviceKind.Alsa;

    /// <summary>True: a sound card transmits, given a PTT line beside it.</summary>
    public override bool CanTransmit => true;
}

/// <summary>A FlexRadio 6000-series slice reached over the LAN.</summary>
/// <param name="Text">The string as written.</param>
/// <param name="Radio">The radio: <c>discover</c>, an IP address, <c>serial=…</c> or <c>mock</c>.</param>
/// <param name="Slice">The slice letter, A by default.</param>
/// <param name="Station">The SmartSDR station to attach to, or <see langword="null"/> for a
/// headless bring-up in which this tool owns the radio.</param>
public sealed record FlexDeviceString(string Text, string Radio, string Slice, string? Station)
    : DeviceString(Text)
{
    /// <inheritdoc />
    public override DeviceKind Kind => DeviceKind.Flex;

    /// <summary>True: the Flex keys itself, and its power is settable.</summary>
    public override bool CanTransmit => true;

    /// <summary>True when no <c>@station</c> was given and we bring the radio up ourselves.</summary>
    public bool Headless => Station is null;
}

/// <summary>A public UberSDR web receiver. Receive only.</summary>
/// <param name="Text">The string as written.</param>
/// <param name="Host">The instance's host name.</param>
/// <param name="Port">Its port; 443 unless the string said otherwise.</param>
/// <param name="Ssl">True for HTTPS, which every public instance runs.</param>
public sealed record UberSdrDeviceString(string Text, string Host, int Port, bool Ssl)
    : DeviceString(Text)
{
    /// <inheritdoc />
    public override DeviceKind Kind => DeviceKind.UberSdr;

    /// <summary>False: it is somebody else's receiver and has no transmitter at all.</summary>
    public override bool CanTransmit => false;
}

/// <summary>Two named pipes, for two instances of this tool on one machine.</summary>
/// <param name="Text">The string as written.</param>
/// <param name="In">The FIFO capture audio is read from.</param>
/// <param name="Out">The FIFO transmit audio is written to.</param>
/// <param name="Rate">The sample rate, 48000 unless the string said otherwise.</param>
public sealed record PipeDeviceString(string Text, string In, string Out, int Rate)
    : DeviceString(Text)
{
    /// <inheritdoc />
    public override DeviceKind Kind => DeviceKind.Pipe;

    /// <summary>True: the other end of the pipe is listening.</summary>
    public override bool CanTransmit => true;
}
