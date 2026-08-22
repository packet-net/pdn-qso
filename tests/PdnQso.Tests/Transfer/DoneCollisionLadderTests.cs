using System.Globalization;
using PdnQso.Link.Audio;
using PdnQso.Link.Transfer;
using PdnQso.Tests.Rig;

namespace PdnQso.Tests.Transfer;

/// <summary>
/// A measurement rather than a claim: what it costs a transfer that the receiver answers the
/// instant it has something to say, over a medium where two stations on air at once lose both
/// frames (issue #8), and what each of the three candidate cures costs in return.
/// </summary>
/// <remarks>
/// <para>
/// It is <b>explicit</b>, so an ordinary run and CI skip it: it is a Monte Carlo over a modem
/// simulation, it takes minutes rather than seconds, and it produces numbers instead of a
/// verdict. Run it with:
/// </para>
/// <code>
/// tests/PdnQso.Tests/bin/Release/net10.0/PdnQso.Tests -explicit only -showLiveOutput \
///     -class PdnQso.Tests.Transfer.DoneCollisionLadderTests
/// </code>
/// <para>
/// Each trial is a whole transfer over its own channel, at its own noise seed, on the clock the
/// rig charges air time to, so "the transfer took nine seconds" means nine seconds of air.
/// Two media, because the cure and the fault have to be priced separately:
/// </para>
/// <list type="bullet">
/// <item><description><b>queueing</b> is the old rig, where a station that wants the channel
/// while the other has it simply waits. Nothing collides there, so this column is the price of
/// the cure on a link that never had the disease.</description></item>
/// <item><description><b>colliding</b> is <see cref="HalfDuplexChannel"/> built to collide: a
/// station keyed up over another loses both frames. This column is the disease.</description></item>
/// </list>
/// </remarks>
/// <para>
/// <b>What it said.</b> Thirty trials a rung, a 384 byte file in six blocks, a status interval
/// of three seconds and a listening gap of two, on <c>afsk1200-il2p</c> where a frame is about
/// 1.2 s of air. "air" is how long the sender's whole transfer took; "symbols" is how many it
/// put on air to do it, where six is the file itself and everything beyond that is repair;
/// "closed" is how many of the thirty ended with the sender knowing the file had arrived rather
/// than giving up on a station that had it. Every trial decoded, in every row.
/// </para>
/// <para>
/// The "wait for quiet" rows repeat to the tenth of a second from run to run, because a receiver
/// that answers into the gap does the same thing every time. The "answer at once" rows do not,
/// and that is a finding rather than a nuisance: a receiver answering into the sender's
/// transmission is racing it, and a race has a spread. Repeats of the table above have put its
/// clean colliding row anywhere between 29 and 37 seconds of air, and its 4 dB row between 80
/// and 109. Nothing in the comparison turns on which end of that a given run lands at.
/// </para>
/// <code>
/// colliding medium: a station keyed up over another loses both frames
///   snr   candidate           closed   air s (med/mean/max)  symbols  wasted s
///   clean answer at once          30    29.1   34.5   45.6      14.3       9.4
///   clean wait for quiet          30    12.7   12.7   12.7       6.0       2.2
///   clean beat 1.5 s              30    19.5   22.1   46.8      10.5       3.1
///   clean beat 3.0 s              30    14.3   14.5   15.5       6.2       4.0
///   clean sender asks last        30    33.1   36.7   51.6      13.6       8.3
///   clean quiet + asks last       30    11.5   11.5   11.5       6.0       1.1
///   6     answer at once          24    63.7   65.8  127.1      22.5      35.2
///   6     wait for quiet          30    15.1   16.0   43.6       6.4       2.8
///   6     beat 1.5 s              30    22.7   25.9   49.9      10.3       3.2
///   6     beat 3.0 s              30    16.5   18.0   59.3       6.6       4.3
///   6     sender asks last        30    38.0   44.5   69.7      14.2      11.0
///   6     quiet + asks last       30    13.7   14.8   46.3       6.4       1.4
///   5     answer at once          27    64.1   71.4  126.2      24.5      39.5
///   5     wait for quiet          30    15.1   16.8   29.3       6.8       3.1
///   5     beat 1.5 s              30    28.1   29.0   49.7      11.5       4.7
///   5     beat 3.0 s              30    16.5   19.0   44.2       7.1       5.2
///   5     sender asks last        30    38.0   48.2   88.3      15.5      13.2
///   5     quiet + asks last       30    13.7   16.0   30.0       6.7       1.8
///   4     answer at once          18   108.7   92.7  156.9      32.5      55.0
///   4     wait for quiet          30    29.3   28.3   64.4      11.7       4.8
///   4     beat 1.5 s              30    35.6   38.8   71.1      15.0       5.4
///   4     beat 3.0 s              30    28.8   33.3   85.6      12.7       6.1
///   4     sender asks last        30    53.1   54.9  103.0      17.6      13.7
///   4     quiet + asks last       30    30.0   29.6   72.4      11.5       3.9
///
/// queueing medium: nothing ever collides, so this is the cure's own price
///   clean answer at once          30    15.2   15.2   15.2       6.0       3.1
///   clean wait for quiet          30    15.0   15.2   15.8       6.0       2.5
///   6     answer at once          30    17.9   19.2   43.7       6.4       3.9
///   6     wait for quiet          30    17.5   18.9   50.5       6.4       3.3
///   5     answer at once          30    17.9   19.5   29.3       6.6       3.7
///   5     wait for quiet          30    17.5   19.4   31.5       6.7       3.1
///   4     answer at once          30    30.7   30.3   66.3      11.1       4.5
///   4     wait for quiet          30    31.5   30.8   65.2      11.3       4.6
/// </code>
/// <para>
/// <b>Waiting for quiet wins on both counts and is the one that shipped.</b> Where stations
/// collide it takes a transfer from twenty-nine seconds of air to thirteen on a clean link and
/// from a hundred and nine to twenty-nine at 4 dB, cuts the symbols on air from fourteen to the
/// file's own six, and closes every one of a hundred and twenty transfers where answering at
/// once lost the news of twenty-one of them - the file on disc, the sender out of patience.
/// Where nothing collides it costs nothing: fifteen point two seconds against fifteen point
/// two, and the same six symbols. It is free because the sender could not have heard the answer
/// any sooner in any case - it was transmitting - so holding the answer back until the sender
/// stops does not delay the sender by a single frame.
/// </para>
/// <para>
/// <b>The other two candidates from the issue were measured and rejected.</b> Both are gone from
/// the code, so their rows above cannot be reproduced from this file; to get them back, put
/// <c>AnswerBeat</c> on <see cref="FileTransferOptions"/> and an early return on it at the top
/// of <c>FileReceiver.AnswerHold.Ready</c>, and <c>ListenBeforeAsking</c> with a
/// <c>ListenAsync</c> ahead of the end-of-pass offer in <c>FileSender.RunAsync</c>.
/// </para>
/// <list type="bullet">
/// <item><description><b>A blind beat</b> is a guess at a frame time, and it costs its own
/// length on every answer whether or not anything was colliding. At 1.5 s against frames of 1.2
/// it removes about half of the fault and no more (19.5 s median on a clean colliding link
/// against 12.7); at 3.0 s it finally comes close on a good link (14.3 s) and is still behind
/// on a bad one (33.3 s mean at 4 dB against 28.3, with a worst case of 86 s against 64). An
/// earlier pass measured 0.5 s, which is shorter than a frame and does nothing at all. It is a
/// knob that has to be retuned for every mode, and at no setting does it beat listening.
/// </description></item>
/// <item><description><b>The sender leaving its gap before it asks</b> rather than after fixes
/// only the one collision the issue named. The receiver's status answers go on colliding with
/// the sender's symbols throughout, so it is barely better than doing nothing: 33.1 s median on
/// a clean colliding link against 29.1 for answering at once. Put on top of waiting for quiet it
/// is worth about a second and a half at 5 and 6 dB and gives it back at 4 (29.6 s mean against
/// 28.3), because a receiver with nothing to say leaves the sender paying for a gap nobody uses.
/// That is a wash for the price of a second gap in the protocol, so it did not ship either; it
/// is the one of the three worth revisiting if the sender is ever reshaped for another reason.
/// </description></item>
/// </list>
/// <param name="output">Where the ladder is printed.</param>
public class DoneCollisionLadderTests(ITestOutputHelper output) : IDisposable
{
    private const int BlockSize = 64;
    private const int Blocks = 6;
    private const int Trials = 30;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pdn-qso-collision-" + Guid.NewGuid().ToString("N"));

    /// <summary>The two answer timings this file can still run.</summary>
    private static readonly (string Name, Func<FileTransferOptions, FileTransferOptions> Apply)[] Candidates =
    [
        ("answer at once", o => o with { QuietBeforeAnswering = TimeSpan.Zero }),
        ("wait for quiet", o => o with { QuietBeforeAnswering = TimeSpan.FromMilliseconds(250) }),
    ];

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact(Explicit = true)]
    public async Task The_Ladder()
    {
        foreach (bool colliding in new[] { false, true })
        {
            output.WriteLine(colliding
                ? "colliding medium: a station keyed up over another loses both frames"
                : "queueing medium: nothing ever collides, so this is the cure's own price");
            output.WriteLine(
                "  snr   candidate          trials  decoded  closed   air s (med/mean/max)  "
                + "symbols  wasted s");
            foreach (double? snrDb in new double?[] { null, 6.0, 5.0, 4.0 })
            {
                foreach ((string name, Func<FileTransferOptions, FileTransferOptions> apply) in Candidates)
                {
                    var results = new List<Trial>();
                    for (int trial = 0; trial < Trials; trial++)
                    {
                        results.Add(await RunTrialAsync(snrDb, colliding, apply, trial));
                    }

                    Report(snrDb, name, results);
                }
            }

            output.WriteLine(string.Empty);
        }
    }

    private void Report(double? snrDb, string candidate, List<Trial> results)
    {
        List<Trial> decoded = results.FindAll(t => t.Decoded);
        List<double> air = decoded.ConvertAll(t => t.Air.TotalSeconds);
        air.Sort();
        List<double> wasted = decoded.ConvertAll(t => t.Wasted.TotalSeconds);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {(snrDb is null ? "clean" : snrDb.Value.ToString("0", CultureInfo.InvariantCulture)),-5} "
            + $"{candidate,-18} {results.Count,6} {decoded.Count,8} "
            + $"{results.FindAll(t => t.SenderKnew).Count,7}  "
            + $"{(air.Count == 0 ? 0 : air[air.Count / 2]),8:0.0} "
            + $"{(air.Count == 0 ? 0 : air.Average()),6:0.0} "
            + $"{(air.Count == 0 ? 0 : air[^1]),6:0.0}  "
            + $"{(decoded.Count == 0 ? 0 : decoded.ConvertAll(t => (double)t.Symbols).Average()),7:0.0}  "
            + $"{(wasted.Count == 0 ? 0 : wasted.Average()),8:0.0}"));
    }

    /// <summary>One whole transfer, and what it cost.</summary>
    /// <param name="Decoded">The receiver got the file.</param>
    /// <param name="SenderKnew">The sender learned that it had, rather than giving up.</param>
    /// <param name="Air">How long the sender's whole transfer took, in air.</param>
    /// <param name="Symbols">How many symbols it put on air to do it.</param>
    /// <param name="Wasted">How long it went on transmitting after the receiver had the
    /// file.</param>
    private sealed record Trial(
        bool Decoded, bool SenderKnew, TimeSpan Air, int Symbols, TimeSpan Wasted);

    private async Task<Trial> RunTrialAsync(
        double? snrDb,
        bool colliding,
        Func<FileTransferOptions, FileTransferOptions> apply,
        int trial)
    {
        AudioChannel channel = snrDb is double snr
            ? new AudioChannel { SnrDb = snr, TailSamples = 8000, Seed = 7919 * (trial + 1) }
            : AudioChannel.Clean;
        await using TransferRig rig = TransferRig.Build(channel, colliding);
        string directory = Path.Combine(
            _directory,
            string.Create(CultureInfo.InvariantCulture, $"{colliding}-{snrDb ?? 99}-{trial}"));

        FileTransferOptions options = apply(Shipped());
        var sender = new FileSender(rig.A, options, idSeed: trial + 1, timeProvider: rig.Clock);
        var receiver = new FileReceiver(rig.B, directory, options, timeProvider: rig.Clock);
        rig.WorkInHand(() => sender.Busy, () => receiver.Busy);

        TimeSpan? decodedAt = null;
        receiver.Completed += _ => decodedAt ??= rig.Clock.Elapsed;

        using var stop = new CancellationTokenSource();
        TimeSpan budget = TimeSpan.FromMinutes(30);
        Task<FileTransferResult> receiving = receiver.ReceiveAsync(stop.Token);
        FileTransferResult sent = await rig.RunAsync(
            sender.SendAsync("payload.bin", Content(BlockSize * Blocks, trial + 1), stop.Token),
            receiver, budget: budget, sending: sender).ConfigureAwait(false);
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
            decoded = (await receiving.ConfigureAwait(false)).Success;
        }
        catch (OperationCanceledException)
        {
        }

        return new Trial(
            decoded,
            sent.Success,
            sent.Elapsed,
            sent.Symbols,
            decodedAt is TimeSpan had && decoded ? senderStopped - had : TimeSpan.Zero);
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
        PollInterval = TimeSpan.FromMilliseconds(100),
        PatienceIntervals = 30,
    };

    private static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }
}
