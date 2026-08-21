namespace PdnQso.Link;

/// <summary>
/// The first byte of a pdn-qso link frame's information field: what the rest of the frame is.
/// </summary>
/// <remarks>
/// The values are wire constants fixed by docs/design.md section 3 and must never be
/// renumbered - a station running an older build has to be able to say "I do not know that
/// type" rather than misread a new one as an old one. Gaps between the groups are deliberate:
/// 0x0n is the conversation, 0x1n the file transfer, 0x2n the performance measurements, and
/// 0x7F is the greeting that everything else can assume it has already seen.
/// </remarks>
public enum LinkFrameType : byte
{
    /// <summary>A chat line: <c>seq(1)</c> then UTF-8 text.</summary>
    Chat = 0x01,

    /// <summary>Acknowledgement of a <see cref="Chat"/> frame, carrying its sequence byte.</summary>
    ChatAck = 0x02,

    /// <summary>Start of a file transfer: file id, name, size, block size K, CRC-32.</summary>
    FileOffer = 0x10,

    /// <summary>One LT-coded symbol: symbol index then the coded payload.</summary>
    FileSymbol = 0x11,

    /// <summary>The receiver's periodic "decoded n of K" report.</summary>
    FileStatus = 0x12,

    /// <summary>The receiver has decoded the file and its CRC matched.</summary>
    FileDone = 0x13,

    /// <summary>One frame of a numbered performance stream.</summary>
    PerfStream = 0x20,

    /// <summary>A round-trip-time probe.</summary>
    PerfPing = 0x21,

    /// <summary>The answer to a <see cref="PerfPing"/>.</summary>
    PerfPong = 0x22,

    /// <summary>"I am here": sent on start and on demand so the other end can name us.</summary>
    Hello = 0x7F,
}
