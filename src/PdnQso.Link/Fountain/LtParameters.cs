namespace PdnQso.Link.Fountain;

/// <summary>
/// The two shape parameters of the robust soliton distribution, plus the seed both ends draw
/// their symbol geometry from.
/// </summary>
/// <remarks>
/// <para>
/// Provenance: M. Luby, "LT Codes", FOCS 2002, and D. J. C. MacKay, <i>Information Theory,
/// Inference, and Learning Algorithms</i>, chapter 50 ("Sparse Graph Codes" / digital fountain
/// codes), whose notation for the robust soliton distribution - <c>rho</c>, <c>tau</c>,
/// <c>c</c>, <c>delta</c>, <c>S</c>, <c>beta</c> - this code follows. No RaptorQ, by policy:
/// see CLAUDE.md, Licence rules.
/// </para>
/// <para>
/// <see cref="C"/> and <see cref="Delta"/> are settings in the UI because their best values
/// depend on the channel, not on the file. <see cref="Delta"/> is the allowed probability that
/// a decode stalls after <c>K + O(sqrt(K) ln^2(K/delta))</c> symbols, and <see cref="C"/> is a
/// free constant of order 1 that scales the expected ripple. Smaller <see cref="C"/> means a
/// smaller degree-1 spike, which is cheaper on air but more likely to stall; the defaults here
/// (<c>c = 0.1</c>, <c>delta = 0.5</c>) are the ones the literature uses for the worked
/// examples and behave well from K = 1 upwards.
/// </para>
/// <para>
/// <see cref="Seed"/> travels in the file offer. Both ends build the same
/// <see cref="LtSymbolLayout"/> from it, which is what lets a symbol carry nothing but its
/// index: the receiver regenerates the degree and the neighbour set rather than being told
/// them.
/// </para>
/// </remarks>
public sealed record LtParameters
{
    /// <summary>The defaults: <c>c = 0.1</c>, <c>delta = 0.5</c>, a fixed seed.</summary>
    public static LtParameters Default { get; } = new();

    /// <summary>The robust soliton constant <c>c</c>, a positive number of order 1.</summary>
    public double C { get; init; } = 0.1;

    /// <summary>
    /// The robust soliton <c>delta</c>: the allowed probability of a decode failure, strictly
    /// between 0 and 1.
    /// </summary>
    public double Delta { get; init; } = 0.5;

    /// <summary>
    /// The seed the symbol geometry is drawn from, sent in the file offer so both ends agree.
    /// The default spells "QSO".
    /// </summary>
    public uint Seed { get; init; } = 0x51534F00;

    /// <summary>Throws if these parameters are not usable.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="C"/> is not positive and finite, or <see cref="Delta"/> is not strictly
    /// between 0 and 1.
    /// </exception>
    public void Validate()
    {
        if (!double.IsFinite(C) || C <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(C), C, "the robust soliton c must be a positive, finite number");
        }

        if (!double.IsFinite(Delta) || Delta <= 0 || Delta >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Delta), Delta, "the robust soliton delta must be strictly between 0 and 1");
        }
    }
}
