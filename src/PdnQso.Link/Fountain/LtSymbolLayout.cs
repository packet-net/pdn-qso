namespace PdnQso.Link.Fountain;

/// <summary>
/// The shared geometry of a transfer: which source blocks each symbol index combines.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that makes an LT symbol cost four bytes of header instead of a bitmap.
/// Both ends build a layout from the same <c>(K, c, delta, seed)</c> - the file offer carries
/// all four - so the receiver regenerates a symbol's degree and neighbour set from its index
/// alone. A symbol that arrives out of order, after a thousand others, or from a resumed
/// sender is decoded exactly the same way.
/// </para>
/// <para>
/// Indices below K are the systematic pass and are their own single neighbour, so a clean
/// channel decodes a file with no repair symbols at all and no XOR work. Indices from K
/// upwards draw a degree from the <see cref="RobustSolitonDistribution"/> and then that many
/// distinct blocks.
/// </para>
/// <para>
/// <b>Not thread safe.</b> A layout owns scratch buffers so that generating a neighbour set
/// allocates nothing; give the encoder and the decoder one each rather than sharing.
/// </para>
/// </remarks>
public sealed class LtSymbolLayout
{
    private readonly int[] _pool;
    private readonly int[] _swaps;

    /// <summary>Builds the layout for a transfer.</summary>
    /// <param name="blockCount">K, the number of source blocks; at least 1.</param>
    /// <param name="parameters">The distribution shape and the seed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockCount"/> is less
    /// than 1, or the parameters are not usable.</exception>
    public LtSymbolLayout(int blockCount, LtParameters parameters)
    {
        Distribution = new RobustSolitonDistribution(blockCount, parameters);
        BlockCount = blockCount;
        Parameters = parameters;

        _pool = new int[blockCount];
        _swaps = new int[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            _pool[i] = i;
        }
    }

    /// <summary>K, the number of source blocks.</summary>
    public int BlockCount { get; }

    /// <summary>The distribution shape and the seed both ends agreed on.</summary>
    public LtParameters Parameters { get; }

    /// <summary>The degree distribution repair symbols are drawn from.</summary>
    public RobustSolitonDistribution Distribution { get; }

    /// <summary>The most blocks any symbol can combine, which is all of them.</summary>
    public int MaxDegree => BlockCount;

    /// <summary>
    /// Writes the source blocks symbol <paramref name="index"/> combines into
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="index">The symbol index; below <see cref="BlockCount"/> for a systematic
    /// symbol, at or above it for a repair symbol.</param>
    /// <param name="destination">At least <see cref="MaxDegree"/> long. The first
    /// <em>return value</em> entries are filled with distinct block numbers.</param>
    /// <returns>The symbol's degree: how many entries were written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public int Neighbours(int index, Span<int> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (destination.Length < MaxDegree)
        {
            throw new ArgumentException(
                $"a neighbour buffer must hold {MaxDegree} entries, not {destination.Length}",
                nameof(destination));
        }

        if (index < BlockCount)
        {
            destination[0] = index;
            return 1;
        }

        var random = new LtRandom(Parameters.Seed, index);
        int degree = Distribution.Sample(random.NextDouble());

        // A partial Fisher-Yates over a permutation this layout keeps, then wound back: the
        // first `degree` entries of a shuffled pool are distinct by construction, which the
        // obvious "draw and reject duplicates" loop is not, and it costs O(degree) rather than
        // O(degree^2) when the degree approaches K.
        for (int i = 0; i < degree; i++)
        {
            int j = i + random.Next(BlockCount - i);
            _swaps[i] = j;
            (_pool[i], _pool[j]) = (_pool[j], _pool[i]);
            destination[i] = _pool[i];
        }

        for (int i = degree - 1; i >= 0; i--)
        {
            int j = _swaps[i];
            (_pool[i], _pool[j]) = (_pool[j], _pool[i]);
        }

        return degree;
    }
}
