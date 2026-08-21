using PdnQso.Link.Fountain;

namespace PdnQso.Link.Transfer;

/// <summary>
/// The knobs both ends of a file transfer share: how big a block is, how the fountain is
/// shaped, and how patient each end is with the other.
/// </summary>
/// <remarks>
/// The defaults suit a mode of a few thousand bits per second. On 300 baud HF, turn
/// <see cref="BlockSize"/> down (see <see cref="LinkCapacity"/>) and the intervals up: a
/// full-size block is most of a minute on air there, and a status interval shorter than one
/// frame time is a receiver talking over the sender.
/// </remarks>
public sealed record FileTransferOptions
{
    /// <summary>
    /// How many bytes each fountain block carries. Defaults to the largest a frame can hold,
    /// which is right for a fast mode and wrong for a slow one; see <see cref="LinkCapacity"/>.
    /// </summary>
    public int BlockSize { get; init; } = LinkCapacity.MaxBlockSize;

    /// <summary>The fountain's shape. The seed is replaced per transfer by the sender.</summary>
    public LtParameters Fountain { get; init; } = LtParameters.Default;

    /// <summary>
    /// How often the receiver reports "decoded n of K" unasked, and how often the sender stops
    /// to listen for one. The two are the same interval on purpose: a report the sender is
    /// still transmitting through is a report nobody hears.
    /// </summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many silent status intervals either end tolerates before deciding the other has
    /// gone away. The sender's patience is <see cref="Patience"/>; the receiver uses the same
    /// span waiting for symbols.
    /// </summary>
    public int PatienceIntervals { get; init; } = 6;

    /// <summary>
    /// How long the sender stops transmitting each time it listens: the gap in which the
    /// receiver gets the channel to answer. Nothing can be heard while a half-duplex station
    /// is talking, so this is not politeness, it is the protocol. The gap comes round once per
    /// <see cref="StatusInterval"/> and ends early the moment the receiver says it is done.
    /// </summary>
    public TimeSpan ListenInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the sender re-sends the offer during the repair phase, which doubles as its
    /// request for a fresh status. Less often than it listens, because a receiver that has the
    /// offer does not need it again and the frame is not free.
    /// </summary>
    public TimeSpan OfferInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often either end looks at its inbox and its clock.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long the receiver goes on answering after it has sent Done, in case the Done was
    /// lost and the sender is still pouring symbols at a file that is already on disc.
    /// </summary>
    public TimeSpan DoneLinger { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A hard ceiling on symbols sent, or 0 for none. Patience is the normal stop; this is for
    /// a caller that wants a transfer bounded in air time whatever the other end says.
    /// </summary>
    public int MaxSymbols { get; init; }

    /// <summary>How long either end waits in silence before giving up.</summary>
    public TimeSpan Patience => StatusInterval * PatienceIntervals;

    /// <summary>Throws if these options are not usable.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A block size outside 1 to
    /// <see cref="LinkCapacity.MaxBlockSize"/>, a non-positive interval, fewer than one
    /// patience interval, or fountain parameters that are not usable.</exception>
    public void Validate()
    {
        if (BlockSize is < 1 or > LinkCapacity.MaxBlockSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize), BlockSize,
                $"a block is 1 to {LinkCapacity.MaxBlockSize} bytes; a frame will not hold more");
        }

        if (StatusInterval <= TimeSpan.Zero || ListenInterval <= TimeSpan.Zero
            || OfferInterval <= TimeSpan.Zero || PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StatusInterval), "every interval has to be a positive length of time");
        }

        if (PatienceIntervals < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PatienceIntervals), PatienceIntervals,
                "a station that gives up before the first status has not tried");
        }

        if (DoneLinger < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DoneLinger), DoneLinger, "a linger cannot be negative");
        }

        if (MaxSymbols < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSymbols), MaxSymbols, "a symbol ceiling cannot be negative");
        }

        Fountain.Validate();
    }
}
