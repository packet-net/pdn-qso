namespace PdnQso.Link.Transfer;

/// <summary>
/// How many bytes of ours fit in one frame, and therefore how big a fountain block can be.
/// </summary>
/// <remarks>
/// <para>
/// The binding limit is IL2P's: its header describes a payload length in ten bits, so the
/// largest frame any IL2P mode can carry is 1023 bytes (M0LTE.Il2p's
/// <c>Il2pCodec.MaxPayloadBytes</c>, and the modems refuse anything larger with an
/// <see cref="ArgumentException"/> at modulation time). Out of that come the AX.25 UI frame's
/// two addresses, control and PID (<see cref="LinkFrame.HeaderLength"/>), then the link
/// protocol's type and session bytes (<see cref="LinkFrame.InfoHeaderLength"/>), then the
/// symbol's own index.
/// </para>
/// <para>
/// A mode outside the IL2P family (classic HDLC AFSK, say) will carry more, and the IL2P modes
/// will in practice carry a few bytes more still, because IL2P's Type 1 encapsulation folds
/// the AX.25 addresses into its own header rather than counting them as payload. Neither is
/// worth the arithmetic: the number here is the one that is safe on every mode this tool
/// offers, and it is a ceiling, not a recommendation.
/// </para>
/// <para>
/// <b>A full-size block is not a good idea on a slow mode.</b> 1001 bytes is about
/// twenty-seven seconds of air at 300 bit/s, which is a long time to lose to one burst of
/// noise and a long time to hold a shared channel. On the HF modes, set
/// <see cref="FileTransferOptions.BlockSize"/> down to something the mode sends in a few
/// seconds; on 9600 baud FM, the ceiling is fine.
/// </para>
/// </remarks>
public static class LinkCapacity
{
    /// <summary>The largest frame IL2P can describe, in bytes.</summary>
    public const int MaxAx25FrameBytes = 1023;

    /// <summary>The largest link-protocol payload that fits in one frame.</summary>
    public const int MaxPayloadBytes =
        MaxAx25FrameBytes - LinkFrame.HeaderLength - LinkFrame.InfoHeaderLength;

    /// <summary>The largest fountain block that fits in one <c>FILE-SYMBOL</c> frame.</summary>
    public const int MaxBlockSize = MaxPayloadBytes - FileSymbolPayload.HeaderLength;

    /// <summary>The longest file name a <c>FILE-OFFER</c> can carry, in bytes of UTF-8.</summary>
    public static int MaxNameBytes =>
        Math.Min((int)byte.MaxValue, MaxPayloadBytes - FileOfferPayload.HeaderLength);
}
