namespace PdnQso.Link.Transfer;

/// <summary>
/// What a transfer came to: the counts a station puts in its log, and the reason if it did not
/// finish.
/// </summary>
/// <remarks>
/// A transfer that fails returns one of these rather than throwing. There is nothing
/// exceptional about a correspondent who stops answering on a fading band, and the counts
/// matter as much when it fails as when it works. Cancellation is the exception to that: a
/// cancelled transfer throws <see cref="OperationCanceledException"/>, because the caller
/// already knows.
/// </remarks>
public sealed record FileTransferResult
{
    /// <summary>True when the file arrived, its CRC matched, and it was written.</summary>
    public required bool Success { get; init; }

    /// <summary>Which end this came from.</summary>
    public required FileTransferRole Role { get; init; }

    /// <summary>The file id.</summary>
    public required uint FileId { get; init; }

    /// <summary>The file's name, as offered.</summary>
    public required string Name { get; init; }

    /// <summary>The file's length in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>K, the number of source blocks.</summary>
    public required int BlockCount { get; init; }

    /// <summary>How many bytes each symbol carried.</summary>
    public required int BlockSize { get; init; }

    /// <summary>Symbols sent, at the sender; symbols taken in, at the receiver.</summary>
    public required int Symbols { get; init; }

    /// <summary>How long it took.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Symbols beyond K: what the fountain's repair cost, in frames. Zero on a channel that
    /// lost nothing, because the systematic pass is the file itself.
    /// </summary>
    public int RepairSymbols => Math.Max(0, Symbols - BlockCount);

    /// <summary>Where the receiver wrote the file; null at the sender or on a failure.</summary>
    public string? Path { get; init; }

    /// <summary>Why it did not finish; null when it did.</summary>
    public string? FailureReason { get; init; }

    /// <summary>A one-line rendering for a log or a status bar.</summary>
    public override string ToString() =>
        Success
            ? $"{Name}: {Length} bytes in {Symbols} symbols ({RepairSymbols} repair) "
              + $"in {Elapsed.TotalSeconds:0.0} s"
            : $"{Name}: failed after {Symbols} symbols - {FailureReason}";
}
