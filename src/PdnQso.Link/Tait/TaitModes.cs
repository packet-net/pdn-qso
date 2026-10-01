using PdnQso.Link.Transfer;

namespace PdnQso.Link.Tait;

/// <summary>
/// The two modems a Tait TM8100/TM8200 carries inside it, as mode names this tool can be set
/// to. Neither is a pdn-soundmodem mode: the radio is the modem, so there is no audio, no
/// sample rate and no audio centre.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>tait-ffsk</c>: the radio's FFSK modem in Transparent mode, the serial
/// port as an 8-bit-clean byte pipe. Frames go over it SLIP-framed, through packet.net's
/// <c>TaitTransparentTransport</c>.</description></item>
/// <item><description><c>tait-sdm</c>: Short Data Messages, the radio's own addressed datagram
/// service over the same FFSK modem, through the CCDI driver's binary SDM. Small: one frame is
/// at most <see cref="SdmFrameCodec.MaxFrameBytes"/> bytes.</description></item>
/// </list>
/// </remarks>
public static class TaitModes
{
    /// <summary>The FFSK modem in Transparent mode.</summary>
    public const string Ffsk = "tait-ffsk";

    /// <summary>Short Data Messages.</summary>
    public const string Sdm = "tait-sdm";

    /// <summary>Both, in the order a list shows them.</summary>
    public static IReadOnlyList<string> All { get; } = [Ffsk, Sdm];

    /// <summary>True for a mode that only a Tait radio can run.</summary>
    public static bool IsTait(string? mode) => mode is Ffsk or Sdm;

    /// <summary>The largest AX.25 frame the mode carries, in bytes.</summary>
    /// <exception cref="ArgumentException">Not a Tait mode.</exception>
    public static int MaxFrameBytes(string mode) => mode switch
    {
        // The radio fragments and reassembles over the air itself; the ceiling is the one the
        // rest of the tool already keeps to.
        Ffsk => LinkCapacity.MaxAx25FrameBytes,
        Sdm => SdmFrameCodec.MaxFrameBytes,
        _ => throw new ArgumentException($"'{mode}' is not a Tait mode", nameof(mode)),
    };

    /// <summary>The largest link-protocol payload one frame of the mode carries.</summary>
    public static int MaxPayloadBytes(string mode) =>
        MaxFrameBytes(mode) - LinkFrame.HeaderLength - LinkFrame.InfoHeaderLength;

    /// <summary>One line for a mode list.</summary>
    public static string Describe(string mode) => mode switch
    {
        Ffsk => "Tait radio's own FFSK modem, Transparent mode",
        Sdm => $"Tait Short Data Messages, {SdmFrameCodec.MaxFrameBytes} byte frames",
        _ => mode,
    };
}
