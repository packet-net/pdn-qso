using System.Buffers.Binary;

namespace PdnQso.Link.Perf;

/// <summary>
/// The byte layout inside the payload of <see cref="LinkFrameType.PerfStream"/>,
/// <see cref="LinkFrameType.PerfPing"/> and <see cref="LinkFrameType.PerfPong"/> frames.
/// </summary>
/// <remarks>
/// <para>
/// Three frame types carry four distinct meanings, told apart by context rather than by a
/// wire tag, because the type byte is already spent (design.md section 3 fixes it) and a
/// session id is cheap to correlate on instead:
/// </para>
/// <list type="bullet">
/// <item><description>A <see cref="LinkFrameType.PerfStream"/> frame - one numbered frame of a
/// stream run, <see cref="EncodeStreamPayload"/>.</description></item>
/// <item><description>A <see cref="LinkFrameType.PerfPing"/> frame carrying a 6-byte RTT probe,
/// <see cref="EncodePingPayload"/> - answered by echoing the same payload back as
/// <see cref="LinkFrameType.PerfPong"/>.</description></item>
/// <item><description>A <see cref="LinkFrameType.PerfPing"/> frame with an empty payload and the
/// same session id as a just-finished stream run - "wrap that session up and tell me what you
/// heard". A stream receiver is listening for exactly that session, so there is nothing to
/// confuse it with a normal RTT probe, which runs its own freshly-generated session.</description></item>
/// <item><description>The stream receiver's answer to the request above: a
/// <see cref="LinkFrameType.PerfPong"/> on the same session carrying
/// <see cref="EncodeSummary"/> - the counts the sender cannot know for itself.</description></item>
/// </list>
/// <para>All multi-byte fields are big-endian; nothing here is a hot path (a handful of frames
/// a second at most), so <see cref="System.Buffers.Binary.BinaryPrimitives"/> over a plain
/// array is simplicity over cleverness.</para>
/// </remarks>
internal static class PerfWire
{
    /// <summary>Bytes at the front of every <see cref="LinkFrameType.PerfStream"/> payload:
    /// seq(2) + total(2) + send-timestamp-ms(4).</summary>
    public const int StreamHeaderLength = 8;

    /// <summary>Bytes in an RTT probe's payload: seq(2) + send-timestamp-ms(4).</summary>
    public const int PingPayloadLength = 6;

    /// <summary>Bytes in a stream summary's payload.</summary>
    public const int SummaryPayloadLength = 12;

    /// <summary>The sentinel written for "no SNR reading" - never a plausible tenth-of-a-dB.</summary>
    private const short NoSnr = short.MinValue;

    /// <summary>
    /// Builds one <see cref="LinkFrameType.PerfStream"/> payload: the running sequence, the
    /// total frame count (repeated in every frame so a receiver still knows how many were
    /// coming even if the last one is the one that gets lost), the sender's local send time,
    /// and deterministic filler out to <paramref name="payloadSize"/> so the frame is exactly
    /// the size the run was configured for.
    /// </summary>
    public static byte[] EncodeStreamPayload(ushort seq, ushort total, uint sendTimeMs, int payloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadSize, StreamHeaderLength);

        var payload = new byte[payloadSize];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), seq);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), total);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), sendTimeMs);
        for (int i = StreamHeaderLength; i < payload.Length; i++)
        {
            // Not read by the receiver - IL2P+CRC already vouches for every byte that arrives.
            // A non-zero, non-repeating pattern only helps a human looking at a capture.
            payload[i] = unchecked((byte)i);
        }

        return payload;
    }

    /// <summary>Reads a <see cref="EncodeStreamPayload"/> header back out.</summary>
    public static bool TryDecodeStreamPayload(
        ReadOnlySpan<byte> payload, out ushort seq, out ushort total, out uint sendTimeMs)
    {
        seq = 0;
        total = 0;
        sendTimeMs = 0;
        if (payload.Length < StreamHeaderLength)
        {
            return false;
        }

        seq = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
        total = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
        sendTimeMs = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
        return true;
    }

    /// <summary>Builds an RTT probe's 6-byte payload.</summary>
    public static byte[] EncodePingPayload(ushort seq, uint sendTimeMs)
    {
        var payload = new byte[PingPayloadLength];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2, 4), sendTimeMs);
        return payload;
    }

    /// <summary>Reads an RTT probe's payload back out - the same layout answers and probes share.</summary>
    public static bool TryDecodePingPayload(ReadOnlySpan<byte> payload, out ushort seq, out uint sendTimeMs)
    {
        seq = 0;
        sendTimeMs = 0;
        if (payload.Length < PingPayloadLength)
        {
            return false;
        }

        seq = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
        sendTimeMs = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(2, 4));
        return true;
    }

    /// <summary>What a stream receiver tells the sender once a run is done, so the sender's
    /// own report is complete: what was actually heard, not just what was sent.</summary>
    public readonly record struct Summary(
        ushort Heard,
        ushort Lost,
        ushort Duplicates,
        double? MeanSnrDb,
        double? WorstSnrDb,
        double? LastSnrDb);

    /// <summary>Builds the summary payload carried on the closing <see cref="LinkFrameType.PerfPong"/>.</summary>
    public static byte[] EncodeSummary(Summary summary)
    {
        var payload = new byte[SummaryPayloadLength];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), summary.Heard);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), summary.Lost);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), summary.Duplicates);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(6, 2), ToTenths(summary.MeanSnrDb));
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(8, 2), ToTenths(summary.WorstSnrDb));
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(10, 2), ToTenths(summary.LastSnrDb));
        return payload;
    }

    /// <summary>Reads a summary payload back out.</summary>
    public static bool TryDecodeSummary(ReadOnlySpan<byte> payload, out Summary summary)
    {
        summary = default;
        if (payload.Length < SummaryPayloadLength)
        {
            return false;
        }

        ushort heard = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
        ushort lost = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
        ushort duplicates = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        double? mean = FromTenths(BinaryPrimitives.ReadInt16BigEndian(payload.Slice(6, 2)));
        double? worst = FromTenths(BinaryPrimitives.ReadInt16BigEndian(payload.Slice(8, 2)));
        double? last = FromTenths(BinaryPrimitives.ReadInt16BigEndian(payload.Slice(10, 2)));
        summary = new Summary(heard, lost, duplicates, mean, worst, last);
        return true;
    }

    private static short ToTenths(double? db) =>
        db is double value
            ? (short)Math.Clamp(Math.Round(value * 10, MidpointRounding.AwayFromZero), short.MinValue + 1, short.MaxValue)
            : NoSnr;

    private static double? FromTenths(short raw) => raw == NoSnr ? null : raw / 10.0;
}
