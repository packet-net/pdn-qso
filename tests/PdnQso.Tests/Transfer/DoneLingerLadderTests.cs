using System.Globalization;
using PdnQso.Link;
using PdnQso.Link.Audio;
using PdnQso.Link.Transfer;
using PdnQso.Tests.Rig;

namespace PdnQso.Tests.Transfer;

/// <summary>
/// A measurement rather than a claim: how often a transfer is lost with the file already on
/// disc, because every copy of the receiver's Done was eaten and it stopped answering while the
/// sender was still pouring (issue #11).
/// </summary>
/// <remarks>
/// <para>
/// It is <b>explicit</b>, so an ordinary run and CI skip it: it is a Monte Carlo over a modem
/// simulation, it takes minutes rather than seconds, and it produces numbers instead of a
/// verdict. Run it with:
/// </para>
/// <code>
/// tests/PdnQso.Tests/bin/Release/net10.0/PdnQso.Tests -explicit only -showLiveOutput \
///     -class PdnQso.Tests.Transfer.DoneLingerLadderTests
/// </code>
/// <para>
/// Each trial is a whole transfer over its own channel, at its own noise seed, on the clock the
/// rig charges air time to, so a ladder of loss rates costs seconds of real life and every
/// interval in it means what it would mean on air. "Lost" is the failure in the issue: the
/// receiver wrote the file and the sender never learned that it had.
/// </para>
/// <para>
/// <b>What it said, either side of the fix.</b> Before is the linger as a fixed span at the
/// shipped ratio to the status interval (twenty seconds against fifteen, so four against three
/// here); after is the linger as silence for a whole patience. Fifty trials a rung, on the same
/// noise seeds:
/// </para>
/// <code>
///                 before                         after
/// snr  fer   decoded  lost  wasted med/max   decoded  lost  wasted med/max
///   6  0.02    50/50     0     1.4 /  5.4      50/50     0     1.4 /  5.4
///   5  0.07    50/50     1     2.5 / 83.4      50/50     0     1.4 / 13.0
///   4  0.26    50/50     5     4.9 / 89.8      49/50     0     4.9 / 35.5
///   3  0.79    38/50    14     5.1 / 90.3      36/50     2     7.4 / 91.1
/// </code>
/// <para>
/// "Wasted" is how long the sender went on transmitting after the receiver had the whole file;
/// a maximum near ninety seconds is a sender spending its entire patience on a station that had
/// finished. The two left at 3 dB are the channel rather than the window: four frames in five
/// are lost there, so the sender's own patience can run out before any of the receiver's
/// answers gets through while the receiver is still answering. The before column cannot be
/// reproduced from this file any more, because the fixed span is gone: revert
/// <c>FileReceiver.LingerAsync</c> and put <c>DoneLinger</c> back into <c>Shipped</c> to get
/// it.
/// </para>
/// <para>
/// There is no 2 dB rung because there is nothing to measure there:
/// <see cref="The_Link_These_Numbers_Came_From"/> puts the frame error rate at 1.00, so no
/// transfer completes at all and the question of what the receiver does afterwards never
/// arises.
/// </para>
/// </remarks>
/// <param name="output">Where the ladder is printed.</param>
public class DoneLingerLadderTests(ITestOutputHelper output) : IDisposable
{
    private const int BlockSize = 64;
    private const int Trials = 50;

    /// <summary>
    /// Patient enough that a link this bad can still finish, which is the case under
    /// measurement: a sender that gives up before the receiver has the file at all is a
    /// different failure and not this one.
    /// </summary>
    private const int Patience = 30;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pdn-qso-ladder-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// What the channels in the ladder actually cost a frame, so the numbers above have a link
    /// attached to them rather than a decibel figure. It is a property of the modem underneath
    /// and not a claim of this repo's; it is printed for scale.
    /// </summary>
    [Fact(Explicit = true)]
    public void The_Link_These_Numbers_Came_From()
    {
        output.WriteLine("snr  frames  lost  frame error rate");
        foreach (double snrDb in new[] { 8.0, 6.0, 5.0, 4.0, 3.0, 2.0 })
        {
            const int Frames = 200;
            using AudioLink link = AudioLink.Create(
                TransferRig.Mode, new AudioChannel { SnrDb = snrDb, TailSamples = 8000 });
            byte[] body = new byte[FileSymbolPayload.HeaderLength + BlockSize];
            new Random(4).NextBytes(body);
            byte[] frame = new LinkFrame(
                "M0LTE-7", LinkFrameType.FileSymbol, 0x11, body).Encode();
            int lost = 0;
            for (int i = 0; i < Frames; i++)
            {
                if (link.RunBurst(frame, LinkEnd.A, txDelayMilliseconds: 100).Count == 0)
                {
                    lost++;
                }
            }

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{snrDb,3:0}  {Frames,6}  {lost,4}  {(double)lost / Frames,16:0.00}"));
        }
    }

    [Fact(Explicit = true)]
    public async Task The_Ladder()
    {
        output.WriteLine(
            "snr  trials  decoded  lost  stalled  wasted s (med/mean/max)  gap s (mean/max)");
        foreach (double snrDb in new[] { 6.0, 5.0, 4.0, 3.0 })
        {
            var results = new List<Trial>();
            for (int trial = 0; trial < Trials; trial++)
            {
                results.Add(await RunTrialAsync(snrDb, trial));
            }

            Report(snrDb, results);
        }
    }

    private void Report(double snrDb, List<Trial> results)
    {
        // A stalled trial is not a measurement of anything: see Trial.Stalled.
        List<Trial> decoded = results.FindAll(t => t.Decoded && !t.Stalled);
        List<Trial> lost = decoded.FindAll(t => !t.SenderKnew);
        List<double> wasted = decoded.ConvertAll(t => t.Wasted.TotalSeconds);
        wasted.Sort();
        List<double> gaps = [];
        foreach (Trial t in decoded)
        {
            gaps.AddRange(t.Gaps);
        }

        gaps.Sort();
        gaps.Reverse();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{snrDb,3:0}  {results.Count,6}  {decoded.Count,7}  {lost.Count,4}  "
            + $"{results.FindAll(t => t.Stalled).Count,7}  "
            + $"{(wasted.Count == 0 ? 0 : wasted[wasted.Count / 2]),8:0.0} "
            + $"{(wasted.Count == 0 ? 0 : wasted.Average()),6:0.0} "
            + $"{(wasted.Count == 0 ? 0 : wasted[^1]),6:0.0}  "
            + $"{(gaps.Count == 0 ? 0 : gaps.Average()),6:0.0} {(gaps.Count == 0 ? 0 : gaps[0]),6:0.0}"));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"     widest gaps between the sender's frames after the decode: {Widest(gaps)}"));
    }

    /// <summary>The eight widest of a descending list, for reading a tail off.</summary>
    private static string Widest(List<double> sorted) => string.Join(
        ", ",
        sorted.GetRange(0, Math.Min(8, sorted.Count))
            .ConvertAll(g => g.ToString("0.0", CultureInfo.InvariantCulture)));

    /// <summary>One whole transfer, and what became of it.</summary>
    /// <param name="Decoded">The receiver got the file.</param>
    /// <param name="SenderKnew">The sender learned that it had.</param>
    /// <param name="Wasted">How long the sender went on transmitting after the receiver had
    /// the file.</param>
    /// <param name="Gaps">How long the receiver went without hearing the sender, while the
    /// sender was in fact still transmitting: what a linger has to cover to stay alive.</param>
    /// <param name="Stalled">
    /// The sender put nothing on air for several status intervals together. No path through
    /// the protocol does that - a sender pours, or listens for one interval, or has stopped -
    /// so it is the rig rather than the program: the clock's driver can race a minute of
    /// modelled time past a thread that has not been let back in, and the sender wakes up
    /// already out of patience. Counted and set aside rather than averaged in, because a trial
    /// where the simulation ran away from the program is not evidence about the program.
    /// </param>
    private sealed record Trial(
        bool Decoded, bool SenderKnew, TimeSpan Wasted, IReadOnlyList<double> Gaps, bool Stalled);

    private async Task<Trial> RunTrialAsync(double snrDb, int trial)
    {
        var channel = new AudioChannel { SnrDb = snrDb, TailSamples = 8000, Seed = 7919 * (trial + 1) };
        await using TransferRig rig = TransferRig.Build(channel);
        string directory = Path.Combine(
            _directory, string.Create(CultureInfo.InvariantCulture, $"snr{snrDb:0}-{trial}"));

        FileTransferOptions options = Shipped();
        var sender = new FileSender(rig.A, options, idSeed: trial + 1, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, directory, options, timeProvider: rig.Clock);

        TimeSpan? decodedAt = null;
        receiver.Progress += p =>
        {
            if (decodedAt is null && p.Decoded == p.BlockCount)
            {
                decodedAt = rig.Clock.Elapsed;
            }
        };

        var heard = new List<TimeSpan>();
        rig.B.FrameReceived += (frame, _) =>
        {
            if (frame.Type is LinkFrameType.FileSymbol or LinkFrameType.FileOffer)
            {
                heard.Add(rig.Clock.Elapsed);
            }
        };

        // What the sender actually put on air, whether or not it survived the channel. It is
        // how a stalled trial is told from a lossy one: the channel cannot make a sender go
        // quiet, only inaudible.
        var transmitted = new List<TimeSpan>();
        rig.A.FrameTransmitted += (_, _) => transmitted.Add(rig.Clock.Elapsed);

        using var stop = new CancellationTokenSource();
        TimeSpan budget = TimeSpan.FromMinutes(30);
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(stop.Token);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("payload.bin", Content(BlockSize * 6, trial + 1), stop.Token),
            receiver, budget: budget).ConfigureAwait(false);
        TimeSpan senderStopped = rig.Clock.Elapsed;

        // The receiver may still be lingering, and on a bad enough link it may never have heard
        // the offer at all, so this cannot be a wait: it is settled if it settles, and stopped
        // if it does not.
        if (!await rig.SettleAsync(() => receiving.IsCompleted, receiver, budget).ConfigureAwait(false))
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await rig.SettleAsync(() => receiving.IsCompleted, receiver, budget).ConfigureAwait(false);
        }

        bool decoded = false;
        try
        {
            FileTransferResult received = await receiving.ConfigureAwait(false);
            decoded = received.Success;
        }
        catch (OperationCanceledException)
        {
        }

        TimeSpan wasted = decodedAt is TimeSpan had && decoded
            ? senderStopped - had
            : TimeSpan.Zero;
        List<double> quiet = Gaps(transmitted, decodedAt);
        quiet.Add(decodedAt is TimeSpan from && transmitted.Count > 0 && transmitted[^1] > from
            ? (senderStopped - transmitted[^1]).TotalSeconds
            : 0);
        bool stalled = quiet.Count > 0
            && quiet.Max() > (options.ListenInterval + (3 * options.StatusInterval)).TotalSeconds;
        return new Trial(decoded, sent.Success, wasted, Gaps(heard, decodedAt), stalled);
    }

    /// <summary>
    /// The gaps between a list of moments, counted from when the receiver had the whole file.
    /// Over the frames it heard, a linger shorter than the longest of these ends while the
    /// sender is still there, which is the whole of issue #11; over the frames the sender put
    /// on air, a long one means the rig stalled.
    /// </summary>
    private static List<double> Gaps(List<TimeSpan> heard, TimeSpan? decodedAt)
    {
        List<double> gaps = [];
        if (decodedAt is not TimeSpan from)
        {
            return gaps;
        }

        TimeSpan previous = from;
        foreach (TimeSpan at in heard)
        {
            if (at <= from)
            {
                continue;
            }

            gaps.Add((at - previous).TotalSeconds);
            previous = at;
        }

        return gaps;
    }

    /// <summary>
    /// The shipped defaults' shape, an order of magnitude quicker, exactly as the transfer
    /// tests take them: a block is about a second of air at 1200 baud, so an interval of a
    /// couple of hundred milliseconds would be a test of nothing.
    /// </summary>
    private static FileTransferOptions Shipped() => new()
    {
        BlockSize = BlockSize,
        StatusInterval = TimeSpan.FromSeconds(3),
        ListenInterval = TimeSpan.FromSeconds(2),
        OfferInterval = TimeSpan.FromSeconds(12),
        PollInterval = TimeSpan.FromMilliseconds(500),
        PatienceIntervals = Patience,
    };

    private static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }
}
