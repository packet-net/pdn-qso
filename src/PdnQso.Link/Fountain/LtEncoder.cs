namespace PdnQso.Link.Fountain;

/// <summary>
/// The transmitting half of the fountain: turns a block of data into an endless numbered
/// stream of symbols, of which any <c>K</c> plus a little are enough to get it back.
/// </summary>
/// <remarks>
/// <para>
/// Provenance: M. Luby, "LT Codes", FOCS 2002, with the robust soliton distribution in
/// D. J. C. MacKay's notation (<i>Information Theory, Inference, and Learning Algorithms</i>,
/// chapter 50). Systematic first pass, XOR combining, nothing patented and nothing borrowed
/// from RaptorQ.
/// </para>
/// <para>
/// Symbol <c>i</c> for <c>i &lt; K</c> is source block <c>i</c> unchanged, so a transfer over a
/// channel that loses nothing costs exactly K symbols and no arithmetic. Symbol <c>i &gt;= K</c>
/// is the XOR of a set of blocks whose size and membership come from
/// <see cref="LtSymbolLayout"/>, which is to say from the index and the seed - so nothing but
/// the index has to go on air with it.
/// </para>
/// <para>
/// The data is padded up to a whole number of blocks at construction. The padding is zeros and
/// the receiver never sees it: the true length travels in the file offer, and the file layer
/// truncates. Nothing else allocates - <see cref="Symbol(int, Span{byte})"/> writes into the
/// caller's buffer and the neighbour scratch is owned once.
/// </para>
/// <para><b>Not thread safe</b>, for the scratch buffer's sake.</para>
/// </remarks>
public sealed class LtEncoder
{
    private readonly byte[] _padded;
    private readonly int[] _neighbours;
    private readonly LtSymbolLayout _layout;

    /// <summary>Builds an encoder over some data.</summary>
    /// <param name="data">The bytes to send. May be any length, including zero-padded up to
    /// the block size.</param>
    /// <param name="blockSize">How many bytes each symbol carries; at least 1.</param>
    /// <param name="parameters">The distribution shape and the seed;
    /// <see cref="LtParameters.Default"/> when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is less than
    /// 1, or <paramref name="data"/> is empty.</exception>
    public LtEncoder(ReadOnlyMemory<byte> data, int blockSize, LtParameters? parameters = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        if (data.Length == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data), "there is no such thing as a fountain with nothing to pour");
        }

        Parameters = parameters ?? LtParameters.Default;
        BlockSize = blockSize;
        DataLength = data.Length;
        BlockCount = (data.Length + blockSize - 1) / blockSize;

        _padded = new byte[(long)BlockCount * blockSize];
        data.Span.CopyTo(_padded);
        _layout = new LtSymbolLayout(BlockCount, Parameters);
        _neighbours = new int[_layout.MaxDegree];
    }

    /// <summary>K, the number of source blocks the data came to.</summary>
    public int BlockCount { get; }

    /// <summary>How many bytes each symbol carries.</summary>
    public int BlockSize { get; }

    /// <summary>The true length of the data, before zero padding.</summary>
    public int DataLength { get; }

    /// <summary>The distribution shape and the seed; these travel in the file offer.</summary>
    public LtParameters Parameters { get; }

    /// <summary>The geometry both ends share.</summary>
    public LtSymbolLayout Layout => _layout;

    /// <summary>Writes one symbol.</summary>
    /// <param name="index">The symbol index, from 0 upwards. Below <see cref="BlockCount"/> is
    /// the systematic pass; at or above it is a repair symbol.</param>
    /// <param name="destination">Exactly <see cref="BlockSize"/> bytes to write into.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not
    /// <see cref="BlockSize"/> bytes long.</exception>
    public void Symbol(int index, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (destination.Length != BlockSize)
        {
            throw new ArgumentException(
                $"a symbol is {BlockSize} bytes, not {destination.Length}", nameof(destination));
        }

        int degree = _layout.Neighbours(index, _neighbours);
        _padded.AsSpan(_neighbours[0] * BlockSize, BlockSize).CopyTo(destination);
        for (int i = 1; i < degree; i++)
        {
            BlockXor.Xor(destination, _padded.AsSpan(_neighbours[i] * BlockSize, BlockSize));
        }
    }

    /// <summary>
    /// One symbol, in a fresh array. Convenient for a test; the sender uses
    /// <see cref="Symbol(int, Span{byte})"/> and its own buffer.
    /// </summary>
    /// <param name="index">The symbol index, from 0 upwards.</param>
    public byte[] Symbol(int index)
    {
        var symbol = new byte[BlockSize];
        Symbol(index, symbol);
        return symbol;
    }
}
