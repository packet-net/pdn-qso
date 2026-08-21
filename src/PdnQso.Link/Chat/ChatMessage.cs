using Packet.SoundModem.Modems;

namespace PdnQso.Link.Chat;

/// <summary>One line heard from the other end of the QSO.</summary>
/// <param name="Source">Who sent it, <c>CALL</c> or <c>CALL-SSID</c>.</param>
/// <param name="Session">Their conversation id, which our acknowledgement carries back.</param>
/// <param name="Seq">Their sequence number for this line.</param>
/// <param name="Text">The line itself, with control characters stripped.</param>
/// <param name="Waveform">The MS110D waveform they sent it on, or null where they have no
/// ladder. A number lower than the one before it is the far station having stepped down.</param>
/// <param name="Quality">What the decode established: SNR, FEC corrections, CRC state.</param>
/// <param name="ReceivedAt">When it arrived, by the session's clock.</param>
public sealed record ChatMessage(
    string Source,
    byte Session,
    byte Seq,
    string Text,
    int? Waveform,
    FrameQuality Quality,
    DateTimeOffset ReceivedAt);

/// <summary>What became of a line this station sent.</summary>
/// <param name="IsDelivered">True when the far station acknowledged it.</param>
/// <param name="Seq">The sequence number it went out with.</param>
/// <param name="Attempts">How many times it was sent, the first attempt included. Honest
/// either way: a line delivered on the third go reports three.</param>
/// <param name="RoundTrip">How long the attempt that landed took, from the station starting
/// to transmit it (so our own air time is in there) to the acknowledgement arriving;
/// <see cref="TimeSpan.Zero"/> for a line that never landed.</param>
public readonly record struct ChatDelivery(bool IsDelivered, byte Seq, int Attempts, TimeSpan RoundTrip)
{
    /// <summary>A line the far station acknowledged.</summary>
    public static ChatDelivery Delivered(byte seq, int attempts, TimeSpan roundTrip) =>
        new(true, seq, attempts, roundTrip);

    /// <summary>A line nobody acknowledged, after every attempt it was going to get.</summary>
    public static ChatDelivery Failed(byte seq, int attempts) =>
        new(false, seq, attempts, TimeSpan.Zero);

    /// <summary>A one-line rendering for a log or a status bar.</summary>
    public override string ToString() =>
        IsDelivered
            ? $"delivered seq {Seq} in {Attempts} attempt(s), rtt {RoundTrip.TotalSeconds:0.0} s"
            : $"failed seq {Seq} after {Attempts} attempt(s)";
}

/// <summary>One attempt at a line that went unacknowledged.</summary>
/// <param name="Seq">The line's sequence number.</param>
/// <param name="Attempt">Which attempt this was, counting the first as 1.</param>
/// <param name="Waveform">The waveform it went out on, or null where there is no ladder.</param>
public readonly record struct ChatAttempt(byte Seq, int Attempt, int? Waveform);

/// <summary>The line currently in flight; stop-and-wait means there is at most one.</summary>
/// <param name="Seq">Its sequence number.</param>
/// <param name="Text">The line.</param>
/// <param name="Attempt">Which attempt is in the air, counting the first as 1.</param>
/// <param name="Waveform">The waveform it went out on, or null where there is no ladder.</param>
public readonly record struct ChatOutstanding(byte Seq, string Text, int Attempt, int? Waveform);

/// <summary>What the conversation has cost so far.</summary>
/// <param name="Sent">Lines this station has offered to send.</param>
/// <param name="Delivered">Lines the far station acknowledged.</param>
/// <param name="Failed">Lines that ran out of attempts.</param>
/// <param name="Retries">Attempts beyond the first, over all lines.</param>
/// <param name="Received">Lines heard from the far station, duplicates not counted.</param>
/// <param name="Duplicates">Lines heard again because our acknowledgement did not get through.</param>
public readonly record struct ChatStats(
    int Sent, int Delivered, int Failed, int Retries, int Received, int Duplicates);
