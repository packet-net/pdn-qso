namespace PdnQso.Link.Fountain;

/// <summary>
/// The deterministic generator a symbol's geometry is drawn from, seeded by the pair
/// (transfer seed, symbol index).
/// </summary>
/// <remarks>
/// <para>
/// SplitMix64 (Steele, Lea and Flood, "Fast splittable pseudorandom number generators",
/// OOPSLA 2014; Vigna's public-domain reference implementation). Chosen over
/// <see cref="System.Random"/> for one reason: the receiver has to reproduce the sender's
/// draws exactly from the index alone, and the framework's generator carries no promise that
/// it will give the same sequence on another runtime version. This one is eleven lines of
/// integer arithmetic and gives the same answer everywhere, for ever.
/// </para>
/// <para>
/// Seeding by (seed, index) rather than by running one stream forward is what makes a symbol
/// self-describing: symbol 40 000 costs the same to generate as symbol 3, and a receiver that
/// missed everything in between still gets the right neighbours. SplitMix64 is designed to be
/// seeded with consecutive states, so neighbouring indices are not correlated.
/// </para>
/// </remarks>
internal struct LtRandom
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    /// <summary>Seeds the generator for one symbol.</summary>
    /// <param name="seed">The transfer's seed, from the file offer.</param>
    /// <param name="index">The symbol index.</param>
    internal LtRandom(uint seed, int index) => _state = ((ulong)seed << 32) | (uint)index;

    /// <summary>The next 64 bits.</summary>
    internal ulong NextULong()
    {
        ulong z = _state += Gamma;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A draw from <c>[0, 1)</c>, with 53 bits of resolution.</summary>
    internal double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>
    /// A draw from <c>[0, bound)</c>. Modulo rather than rejection: the bias is one part in
    /// 2^64/bound and nothing here is a lottery, while rejection would make the number of
    /// draws depend on the arithmetic and so on the platform.
    /// </summary>
    /// <param name="bound">The exclusive upper bound, at least 1.</param>
    internal int Next(int bound) => (int)(NextULong() % (ulong)bound);
}
