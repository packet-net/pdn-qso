using System.Buffers.Binary;
using System.Text;
using PdnQso.Link.Fountain;

namespace PdnQso.Link.Transfer;

/// <summary>
/// The body of a <see cref="LinkFrameType.FileOffer"/> frame: everything the receiver needs to
/// build a decoder and to know when it has finished.
/// </summary>
/// <remarks>
/// <para>
/// Wire layout, all integers big endian (docs/design.md section 3):
/// </para>
/// <code>
/// 0    1   version, 1
/// 1    4   file id
/// 5    4   file length in bytes
/// 9    4   K, the number of source blocks
/// 13   2   block size in bytes
/// 15   4   CRC-32 of the file
/// 19   4   the fountain seed
/// 23   8   the robust soliton c, IEEE 754 binary64
/// 31   8   the robust soliton delta, IEEE 754 binary64
/// 39   1   the length of the name in bytes
/// 40   n   the name, UTF-8
/// </code>
/// <para>
/// <c>c</c> and <c>delta</c> travel as full doubles rather than as anything smaller because
/// the two ends must build <em>bit-identical</em> degree distributions from them. A value
/// rounded on the wire would give the receiver a distribution a hair different from the
/// sender's, and a repair symbol whose degree the two ends disagree about is not a damaged
/// symbol - it is a silently wrong one.
/// </para>
/// <para>
/// The offer is re-sent through the transfer, both because the receiver may have missed the
/// first one and because it doubles as the sender's request for a status report. Everything in
/// it is therefore idempotent: a second offer for a transfer already running changes nothing.
/// </para>
/// </remarks>
public readonly record struct FileOfferPayload
{
    /// <summary>The version byte this build writes and accepts.</summary>
    public const byte Version = 1;

    /// <summary>Bytes before the name.</summary>
    public const int HeaderLength = 40;

    /// <summary>Builds an offer.</summary>
    /// <param name="fileId">Identifies the file for the life of the transfer.</param>
    /// <param name="name">The file's name as the sender knows it.</param>
    /// <param name="length">The file's length in bytes.</param>
    /// <param name="blockCount">K, the number of source blocks.</param>
    /// <param name="blockSize">How many bytes each symbol carries.</param>
    /// <param name="crc32">The CRC-32 of the whole file.</param>
    /// <param name="parameters">The fountain's shape and seed.</param>
    public FileOfferPayload(
        uint fileId,
        string name,
        long length,
        int blockCount,
        int blockSize,
        uint crc32,
        LtParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        FileId = fileId;
        Name = name;
        Length = length;
        BlockCount = blockCount;
        BlockSize = blockSize;
        Crc32 = crc32;
        Parameters = parameters;
    }

    /// <summary>Identifies the file for the life of the transfer.</summary>
    public uint FileId { get; }

    /// <summary>The file's name as the sender knows it; the receiver makes it safe.</summary>
    public string Name { get; }

    /// <summary>The file's length in bytes, before the fountain's zero padding.</summary>
    public long Length { get; }

    /// <summary>K, the number of source blocks.</summary>
    public int BlockCount { get; }

    /// <summary>How many bytes each symbol carries.</summary>
    public int BlockSize { get; }

    /// <summary>The CRC-32 of the whole file, which closes the transfer.</summary>
    public uint Crc32 { get; }

    /// <summary>The fountain's shape and seed, so both ends draw the same symbols.</summary>
    public LtParameters Parameters { get; }

    /// <summary>The frame body these fields encode to.</summary>
    /// <exception cref="InvalidOperationException">The offer cannot be put on the wire: a
    /// negative or over-large length, a name that does not fit, or a block size out of range.</exception>
    public byte[] Encode()
    {
        if (Length is < 0 or > uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"a file of {Length} bytes cannot be offered; the wire field is 32 bits");
        }

        if (BlockCount < 1 || BlockSize is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"K = {BlockCount} and a block size of {BlockSize} are not a transfer");
        }

        byte[] name = Encoding.UTF8.GetBytes(Name);
        if (name.Length > LinkCapacity.MaxNameBytes)
        {
            throw new InvalidOperationException(
                $"'{Name}' is {name.Length} bytes of UTF-8 and the offer carries at most "
                + $"{LinkCapacity.MaxNameBytes}");
        }

        var payload = new byte[HeaderLength + name.Length];
        payload[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), FileId);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), (uint)Length);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(9), BlockCount);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(13), (ushort)BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(15), Crc32);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(19), Parameters.Seed);
        BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(23), Parameters.C);
        BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(31), Parameters.Delta);
        payload[39] = (byte)name.Length;
        name.CopyTo(payload.AsSpan(HeaderLength));
        return payload;
    }

    /// <summary>Reads an offer out of a frame body.</summary>
    /// <param name="payload">The frame's payload, after the type and session bytes.</param>
    /// <param name="offer">The offer, or default.</param>
    /// <returns><see langword="false"/> for anything that is not a well-formed offer of a
    /// version this build knows: too short, the wrong version, a K and block size that do not
    /// describe the stated length, or fountain parameters that are not usable.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out FileOfferPayload offer)
    {
        offer = default;
        if (payload.Length < HeaderLength)
        {
            return false;
        }

        if (payload[0] != Version)
        {
            return false;
        }

        int nameLength = payload[39];
        if (payload.Length != HeaderLength + nameLength)
        {
            return false;
        }

        uint fileId = BinaryPrimitives.ReadUInt32BigEndian(payload[1..]);
        long length = BinaryPrimitives.ReadUInt32BigEndian(payload[5..]);
        int blockCount = BinaryPrimitives.ReadInt32BigEndian(payload[9..]);
        int blockSize = BinaryPrimitives.ReadUInt16BigEndian(payload[13..]);
        uint crc = BinaryPrimitives.ReadUInt32BigEndian(payload[15..]);
        uint seed = BinaryPrimitives.ReadUInt32BigEndian(payload[19..]);
        double c = BinaryPrimitives.ReadDoubleBigEndian(payload[23..]);
        double delta = BinaryPrimitives.ReadDoubleBigEndian(payload[31..]);

        if (blockCount < 1 || blockSize < 1 || length < 1)
        {
            return false;
        }

        // K has to be the number of blocks the stated length comes to. Checking it rather than
        // trusting it means a damaged offer cannot talk the receiver into allocating a
        // gigabyte of decoder for a two-kilobyte file.
        if (blockCount != (length + blockSize - 1) / blockSize)
        {
            return false;
        }

        var parameters = new LtParameters { C = c, Delta = delta, Seed = seed };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        offer = new FileOfferPayload(
            fileId,
            Encoding.UTF8.GetString(payload.Slice(HeaderLength, nameLength)),
            length,
            blockCount,
            blockSize,
            crc,
            parameters);
        return true;
    }
}
