namespace PdnQso.Link.Transfer;

/// <summary>Which end of a transfer a progress report or a result came from.</summary>
public enum FileTransferRole
{
    /// <summary>The station pouring symbols.</summary>
    Sender,

    /// <summary>The station decoding them.</summary>
    Receiver,
}

/// <summary>
/// One progress report, raised per symbol so a UI can move a bar without polling anything.
/// </summary>
/// <param name="FileId">The file this is about.</param>
/// <param name="Name">The file's name.</param>
/// <param name="Role">Which end this came from.</param>
/// <param name="Symbols">Symbols sent, at the sender; symbols taken in, at the receiver.</param>
/// <param name="Decoded">Blocks decoded: the receiver's own count, or the last count the
/// receiver reported, which is 0 at the sender until the first status arrives.</param>
/// <param name="BlockCount">K, the number of source blocks.</param>
/// <param name="BlockSize">How many bytes each symbol carries.</param>
/// <param name="Elapsed">How long the transfer has been running.</param>
public readonly record struct FileProgress(
    uint FileId,
    string Name,
    FileTransferRole Role,
    int Symbols,
    int Decoded,
    int BlockCount,
    int BlockSize,
    TimeSpan Elapsed)
{
    /// <summary>How much of the file is decoded, 0 to 1. Zero at the sender until a status.</summary>
    public double Fraction => BlockCount <= 0 ? 0 : Math.Min(1.0, (double)Decoded / BlockCount);

    /// <summary>
    /// The rate: payload bytes put on air per second at the sender, payload bytes recovered
    /// per second at the receiver. The two differ by the repair overhead, which is the point
    /// of showing both.
    /// </summary>
    public double BytesPerSecond
    {
        get
        {
            double seconds = Elapsed.TotalSeconds;
            if (seconds <= 0)
            {
                return 0;
            }

            int blocks = Role == FileTransferRole.Sender ? Symbols : Decoded;
            return (double)blocks * BlockSize / seconds;
        }
    }
}
