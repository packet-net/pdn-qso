namespace PdnQso.Link.Fountain;

/// <summary>
/// The receiving half of the fountain: a peeling decoder that takes symbols in any order, with
/// any gaps, and finishes when it has enough of them.
/// </summary>
/// <remarks>
/// <para>
/// Provenance: M. Luby, "LT Codes", FOCS 2002, section 2 (the decoder), in D. J. C. MacKay's
/// exposition (<i>Information Theory, Inference, and Learning Algorithms</i>, chapter 50).
/// </para>
/// <para>
/// The algorithm is the whole of the idea. A symbol arrives; every neighbour of it that is
/// already decoded is XORed out. If that leaves one unknown neighbour, the symbol <em>is</em>
/// that block, so it is decoded, and every stored symbol that touched it can now be reduced -
/// which may release another block, and so on: the ripple. If it leaves two or more, the
/// symbol is kept until some later symbol reduces it. If it leaves none, the symbol was
/// redundant and is thrown away.
/// </para>
/// <para>
/// Two representation choices keep that cheap and allocation-free in the steady state. A
/// stored symbol does not keep its neighbour list: it keeps a count of unresolved neighbours
/// and the XOR of their indices, so when the count falls to one the XOR <em>is</em> the index
/// of the block it resolves. And every unresolved (symbol, block) pair is one entry in a
/// linked list hanging off the block, so decoding a block visits exactly the symbols that
/// touch it rather than all of them. Both the symbol slots and the list entries are recycled
/// through free lists, so a long transfer allocates only while its working set is growing.
/// </para>
/// <para>
/// <b>What it will not do.</b> A symbol whose index is out of range, or whose length is wrong,
/// is refused rather than decoded into something. That is a guard, not error correction: a
/// symbol whose <em>contents</em> are corrupt but whose index is plausible will decode into a
/// wrong file, and the only thing that catches that is the whole-file CRC-32 in the file
/// offer. On air the modem's own CRC removes a corrupt frame long before it reaches here, so
/// the realistic failure is a symbol that is simply absent, which costs nothing but another
/// symbol.
/// </para>
/// <para><b>Not thread safe.</b></para>
/// </remarks>
public sealed class LtDecoder
{
    private const int InitialEdgeCapacity = 64;
    private const int InitialSlotCapacity = 16;

    private readonly LtSymbolLayout _layout;
    private readonly byte[] _blocks;
    private readonly bool[] _known;
    private readonly int[] _neighbours;
    private readonly int[] _unresolved;
    private readonly byte[] _working;
    private readonly int[] _ripple;
    private readonly int[] _blockHead;

    private byte[][] _slotValue = new byte[InitialSlotCapacity][];
    private int[] _slotRemaining = new int[InitialSlotCapacity];
    private int[] _slotXor = new int[InitialSlotCapacity];
    private int[] _slotGeneration = new int[InitialSlotCapacity];
    private bool[] _slotActive = new bool[InitialSlotCapacity];
    private int[] _slotFree = new int[InitialSlotCapacity];
    private int _slotCount;
    private int _slotFreeCount;

    private int[] _edgeSlot = new int[InitialEdgeCapacity];
    private int[] _edgeGeneration = new int[InitialEdgeCapacity];
    private int[] _edgeNext = new int[InitialEdgeCapacity];
    private int _edgeCount;
    private int _edgeFreeHead = -1;

    private int _rippleCount;

    /// <summary>Builds a decoder for a transfer whose shape is already known.</summary>
    /// <param name="blockCount">K, the number of source blocks, from the file offer.</param>
    /// <param name="blockSize">How many bytes each symbol carries, from the file offer.</param>
    /// <param name="parameters">The distribution shape and the seed, from the file offer;
    /// <see cref="LtParameters.Default"/> when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockCount"/> or
    /// <paramref name="blockSize"/> is less than 1, or the parameters are not usable.</exception>
    public LtDecoder(int blockCount, int blockSize, LtParameters? parameters = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);

        Parameters = parameters ?? LtParameters.Default;
        BlockCount = blockCount;
        BlockSize = blockSize;
        _layout = new LtSymbolLayout(blockCount, Parameters);

        _blocks = new byte[(long)blockCount * blockSize];
        _known = new bool[blockCount];
        _neighbours = new int[_layout.MaxDegree];
        _unresolved = new int[_layout.MaxDegree];
        _working = new byte[blockSize];
        _ripple = new int[blockCount];
        _blockHead = new int[blockCount];
        Array.Fill(_blockHead, -1);
    }

    /// <summary>K, the number of source blocks.</summary>
    public int BlockCount { get; }

    /// <summary>How many bytes each symbol carries.</summary>
    public int BlockSize { get; }

    /// <summary>The distribution shape and the seed the sender chose.</summary>
    public LtParameters Parameters { get; }

    /// <summary>The geometry both ends share.</summary>
    public LtSymbolLayout Layout => _layout;

    /// <summary>How many of the K source blocks are known.</summary>
    public int Decoded { get; private set; }

    /// <summary>How many symbols have been offered, accepted or not.</summary>
    public int Received { get; private set; }

    /// <summary>How many symbols were refused: a bad index or the wrong length.</summary>
    public int Refused { get; private set; }

    /// <summary>How many symbols are held, waiting for something to reduce them.</summary>
    public int Pending => _slotCount - _slotFreeCount;

    /// <summary>True once every source block is known.</summary>
    public bool IsComplete => Decoded == BlockCount;

    /// <summary>
    /// The largest symbol index this decoder will look at. A sanity bound, not a protocol
    /// limit: a transfer needing sixty-four times the file in repair symbols has already
    /// failed, and a wild index out of a damaged frame must not be allowed to allocate against
    /// it. See the class remarks on what this does and does not protect.
    /// </summary>
    public int MaxSymbolIndex => (BlockCount * 64) + 65536;

    /// <summary>
    /// The decoded data: K times <see cref="BlockSize"/> bytes, including whatever zero
    /// padding the last block carried. The file layer truncates to the length in the offer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The decode is not finished.</exception>
    public byte[] Data
    {
        get
        {
            if (!IsComplete)
            {
                throw new InvalidOperationException(
                    $"the decode is not finished: {Decoded} of {BlockCount} blocks");
            }

            return (byte[])_blocks.Clone();
        }
    }

    /// <summary>Copies the decoded data out without allocating.</summary>
    /// <param name="destination">At least K times <see cref="BlockSize"/> bytes.</param>
    /// <exception cref="InvalidOperationException">The decode is not finished.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException(
                $"the decode is not finished: {Decoded} of {BlockCount} blocks");
        }

        if (destination.Length < _blocks.Length)
        {
            throw new ArgumentException(
                $"the decoded data is {_blocks.Length} bytes and the buffer holds "
                + $"{destination.Length}", nameof(destination));
        }

        _blocks.CopyTo(destination);
    }

    /// <summary>
    /// Offers one symbol to the decoder.
    /// </summary>
    /// <param name="index">The symbol index, exactly as it arrived on air.</param>
    /// <param name="symbol">The symbol's <see cref="BlockSize"/> bytes.</param>
    /// <returns>
    /// <see langword="true"/> when the symbol taught the decoder something: it either decoded
    /// a block or was kept because it might later. <see langword="false"/> when it was refused
    /// (out of range, wrong length, or offered after the decode finished) or was redundant
    /// (every block it touches was already known).
    /// </returns>
    public bool Add(int index, ReadOnlySpan<byte> symbol)
    {
        Received++;
        if (index < 0 || index > MaxSymbolIndex || symbol.Length != BlockSize)
        {
            Refused++;
            return false;
        }

        if (IsComplete)
        {
            return false;
        }

        int degree = _layout.Neighbours(index, _neighbours);

        // Reduce against what is already known before deciding what to do with it. The working
        // buffer is owned, so a symbol that turns out to be redundant costs one copy and no
        // allocation at all.
        Span<byte> value = _working;
        symbol.CopyTo(value);
        int remaining = 0;
        int remainingXor = 0;
        for (int i = 0; i < degree; i++)
        {
            int block = _neighbours[i];
            if (_known[block])
            {
                BlockXor.Xor(value, _blocks.AsSpan(block * BlockSize, BlockSize));
            }
            else
            {
                _unresolved[remaining++] = block;
                remainingXor ^= block;
            }
        }

        if (remaining == 0)
        {
            return false;
        }

        if (remaining == 1)
        {
            Resolve(remainingXor, value);
            DrainRipple();
            return true;
        }

        int slot = TakeSlot();
        value.CopyTo(_slotValue[slot]);
        _slotRemaining[slot] = remaining;
        _slotXor[slot] = remainingXor;
        _slotActive[slot] = true;
        for (int i = 0; i < remaining; i++)
        {
            AttachEdge(_unresolved[i], slot);
        }

        return true;
    }

    /// <summary>Throws everything away and starts the transfer again.</summary>
    public void Reset()
    {
        Array.Clear(_blocks);
        Array.Clear(_known);
        Array.Fill(_blockHead, -1);
        Array.Clear(_slotActive, 0, _slotCount);
        Decoded = 0;
        Received = 0;
        Refused = 0;
        _rippleCount = 0;
        _edgeCount = 0;
        _edgeFreeHead = -1;
        _slotFreeCount = 0;
        for (int i = 0; i < _slotCount; i++)
        {
            _slotFree[_slotFreeCount++] = i;
            _slotGeneration[i]++;
        }
    }

    /// <summary>Records a block's contents and puts it on the ripple.</summary>
    private void Resolve(int block, ReadOnlySpan<byte> value)
    {
        value.CopyTo(_blocks.AsSpan(block * BlockSize, BlockSize));
        _known[block] = true;
        Decoded++;
        _ripple[_rippleCount++] = block;
    }

    /// <summary>
    /// Works through the newly decoded blocks, reducing every stored symbol that touched one
    /// and following whatever that releases.
    /// </summary>
    private void DrainRipple()
    {
        while (_rippleCount > 0)
        {
            int block = _ripple[--_rippleCount];
            ReadOnlySpan<byte> known = _blocks.AsSpan(block * BlockSize, BlockSize);

            int edge = _blockHead[block];
            _blockHead[block] = -1;
            while (edge >= 0)
            {
                int next = _edgeNext[edge];
                int slot = _edgeSlot[edge];

                // A slot that has been released and reused since this entry was made is not
                // the symbol this entry belonged to; the generation stamp is what tells them
                // apart, and skipping is exactly right - the original symbol is gone.
                if (_slotActive[slot] && _slotGeneration[slot] == _edgeGeneration[edge])
                {
                    BlockXor.Xor(_slotValue[slot], known);
                    _slotXor[slot] ^= block;
                    if (--_slotRemaining[slot] <= 1)
                    {
                        int target = _slotXor[slot];
                        if (_slotRemaining[slot] == 1 && !_known[target])
                        {
                            Resolve(target, _slotValue[slot]);
                        }

                        ReleaseSlot(slot);
                    }
                }

                ReleaseEdge(edge);
                edge = next;
            }
        }
    }

    /// <summary>Takes a symbol slot from the free list, growing the arrays if it is empty.</summary>
    private int TakeSlot()
    {
        if (_slotFreeCount > 0)
        {
            return _slotFree[--_slotFreeCount];
        }

        if (_slotCount == _slotValue.Length)
        {
            int capacity = _slotValue.Length * 2;
            Array.Resize(ref _slotValue, capacity);
            Array.Resize(ref _slotRemaining, capacity);
            Array.Resize(ref _slotXor, capacity);
            Array.Resize(ref _slotGeneration, capacity);
            Array.Resize(ref _slotActive, capacity);
            Array.Resize(ref _slotFree, capacity);
        }

        int slot = _slotCount++;
        _slotValue[slot] = new byte[BlockSize];
        return slot;
    }

    /// <summary>Puts a slot back, stamping a new generation so its stale entries are ignored.</summary>
    private void ReleaseSlot(int slot)
    {
        _slotActive[slot] = false;
        _slotGeneration[slot]++;
        _slotFree[_slotFreeCount++] = slot;
    }

    /// <summary>Records that <paramref name="slot"/> still needs <paramref name="block"/>.</summary>
    private void AttachEdge(int block, int slot)
    {
        int edge;
        if (_edgeFreeHead >= 0)
        {
            edge = _edgeFreeHead;
            _edgeFreeHead = _edgeNext[edge];
        }
        else
        {
            if (_edgeCount == _edgeSlot.Length)
            {
                int capacity = _edgeSlot.Length * 2;
                Array.Resize(ref _edgeSlot, capacity);
                Array.Resize(ref _edgeGeneration, capacity);
                Array.Resize(ref _edgeNext, capacity);
            }

            edge = _edgeCount++;
        }

        _edgeSlot[edge] = slot;
        _edgeGeneration[edge] = _slotGeneration[slot];
        _edgeNext[edge] = _blockHead[block];
        _blockHead[block] = edge;
    }

    /// <summary>Puts a list entry back on the free list.</summary>
    private void ReleaseEdge(int edge)
    {
        _edgeNext[edge] = _edgeFreeHead;
        _edgeFreeHead = edge;
    }
}
