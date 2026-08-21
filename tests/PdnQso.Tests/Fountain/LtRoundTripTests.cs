using System.Globalization;
using PdnQso.Link.Fountain;

namespace PdnQso.Tests.Fountain;

/// <summary>
/// The claim the file transfer rests on: any K symbols plus a little, in any order, with any
/// gaps, get the file back.
/// </summary>
/// <param name="output">Where the measured overhead table is printed.</param>
public class LtRoundTripTests(ITestOutputHelper output)
{
    private const int BlockSize = 64;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public void The_Systematic_Pass_Alone_Completes_On_A_Channel_That_Loses_Nothing(int blockCount)
    {
        byte[] data = Data(blockCount, seed: 7);
        var encoder = new LtEncoder(data, BlockSize, new LtParameters());
        var decoder = new LtDecoder(encoder.BlockCount, BlockSize, encoder.Parameters);
        byte[] symbol = new byte[BlockSize];

        for (int index = 0; index < encoder.BlockCount; index++)
        {
            encoder.Symbol(index, symbol);
            decoder.Add(index, symbol).Should().BeTrue();
        }

        decoder.IsComplete.Should().BeTrue("the systematic pass is the file itself");
        decoder.Received.Should().Be(encoder.BlockCount, "and it costs exactly K symbols");
        decoder.Data.AsSpan(0, data.Length).ToArray().Should().Equal(data);
    }

    [Theory]
    [InlineData(1, 0.3)]
    [InlineData(2, 0.3)]
    [InlineData(10, 0.2)]
    [InlineData(10, 0.5)]
    [InlineData(100, 0.2)]
    [InlineData(100, 0.5)]
    [InlineData(1000, 0.2)]
    [InlineData(1000, 0.5)]
    public void A_File_Round_Trips_Through_A_Channel_That_Loses_Symbols(int blockCount, double loss)
    {
        for (int trial = 0; trial < 8; trial++)
        {
            byte[] data = Data(blockCount, seed: 100 + trial);
            var parameters = new LtParameters { Seed = (uint)(0x51530000 + trial) };
            var encoder = new LtEncoder(data, BlockSize, parameters);
            var decoder = new LtDecoder(encoder.BlockCount, BlockSize, parameters);

            Transfer(encoder, decoder, loss, new Random(500 + trial))
                .Should().BeTrue($"K = {blockCount}, loss = {loss}, trial {trial}");
            decoder.Data.AsSpan(0, data.Length).ToArray().Should().Equal(data);
        }
    }

    [Fact]
    public void Symbols_Arriving_Out_Of_Order_Decode_The_Same_File()
    {
        byte[] data = Data(60, seed: 11);
        var encoder = new LtEncoder(data, BlockSize, new LtParameters());
        var decoder = new LtDecoder(encoder.BlockCount, BlockSize, encoder.Parameters);

        // Every index from 0 to 299, shuffled: the receiver has no idea what order the sender
        // used and must not need one.
        var indices = new List<int>();
        for (int i = 0; i < 300; i++)
        {
            indices.Add(i);
        }

        var random = new Random(4242);
        for (int i = indices.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        byte[] symbol = new byte[BlockSize];
        foreach (int index in indices)
        {
            encoder.Symbol(index, symbol);
            decoder.Add(index, symbol);
            if (decoder.IsComplete)
            {
                break;
            }
        }

        decoder.IsComplete.Should().BeTrue();
        decoder.Data.AsSpan(0, data.Length).ToArray().Should().Equal(data);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public void The_Measured_Overhead_Is_Within_The_Range_These_Parameters_Give(int blockCount)
    {
        const int Trials = 24;
        output.WriteLine(
            $"K = {blockCount}, c = 0.1, delta = 0.5, {Trials} trials per row, "
            + "overhead = symbols the receiver took in beyond K");
        output.WriteLine("  loss   mean    worst   as % of K");

        foreach (double loss in new[] { 0.0, 0.05, 0.2, 0.5 })
        {
            long total = 0;
            int worst = 0;
            for (int trial = 0; trial < Trials; trial++)
            {
                byte[] data = Data(blockCount, seed: 900 + trial);
                var parameters = new LtParameters { Seed = (uint)(0x4F564800 + trial) };
                var encoder = new LtEncoder(data, BlockSize, parameters);
                var decoder = new LtDecoder(encoder.BlockCount, BlockSize, parameters);

                Transfer(encoder, decoder, loss, new Random(3000 + trial)).Should().BeTrue();
                int overhead = decoder.Received - decoder.Refused - encoder.BlockCount;
                total += overhead;
                worst = Math.Max(worst, overhead);
            }

            double mean = (double)total / Trials;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {loss,4:0.00}  {mean,6:0.0}  {worst,6}   {100 * mean / blockCount,6:0.0}%"));

            if (loss == 0)
            {
                mean.Should().Be(0, "a systematic pass over a clean channel needs no repair");
                continue;
            }

            // The band this construction actually lives in. A systematic fountain pays a
            // coupon-collector tail: once only a few blocks are missing out of K, most repair
            // symbols combine blocks the receiver already has and teach it nothing, so the
            // overhead sits around a third of K rather than the few per cent a non-systematic
            // LT code shows. That is the price of the systematic pass, which in exchange makes
            // a clean transfer cost exactly K. It is measured here so that a change to the
            // distribution, the sampler or the peeling decoder cannot quietly make it worse.
            (100 * mean / blockCount).Should().BeInRange(
                1, 75, "the repair overhead should stay in the band this code has measured");
        }
    }

    [Fact]
    public void An_Index_Beyond_The_Ceiling_Is_Refused_And_Changes_Nothing()
    {
        byte[] data = Data(20, seed: 3);
        var encoder = new LtEncoder(data, BlockSize, new LtParameters());
        var decoder = new LtDecoder(encoder.BlockCount, BlockSize, encoder.Parameters);
        byte[] rubbish = new byte[BlockSize];
        Array.Fill(rubbish, (byte)0xA5);

        decoder.Add(-1, rubbish).Should().BeFalse();
        decoder.Add(int.MinValue, rubbish).Should().BeFalse();
        decoder.Add(int.MaxValue, rubbish).Should().BeFalse();
        decoder.Add(decoder.MaxSymbolIndex + 1, rubbish).Should().BeFalse();
        decoder.Refused.Should().Be(4);
        decoder.Decoded.Should().Be(0);
        decoder.Pending.Should().Be(0, "a refused symbol is not stored either");

        byte[] symbol = new byte[BlockSize];
        for (int index = 0; index < encoder.BlockCount; index++)
        {
            encoder.Symbol(index, symbol);
            decoder.Add(index, symbol);
        }

        decoder.IsComplete.Should().BeTrue();
        decoder.Data.AsSpan(0, data.Length).ToArray().Should().Equal(data);
    }

    [Fact]
    public void A_Symbol_Of_The_Wrong_Length_Is_Refused()
    {
        var decoder = new LtDecoder(10, BlockSize, new LtParameters());

        decoder.Add(0, new byte[BlockSize - 1]).Should().BeFalse();
        decoder.Add(0, new byte[BlockSize + 1]).Should().BeFalse();
        decoder.Add(0, []).Should().BeFalse();
        decoder.Refused.Should().Be(3);
        decoder.Decoded.Should().Be(0);
    }

    [Fact]
    public void A_Repeated_Symbol_Is_Redundant_Rather_Than_Harmful()
    {
        byte[] data = Data(30, seed: 5);
        var encoder = new LtEncoder(data, BlockSize, new LtParameters());
        var decoder = new LtDecoder(encoder.BlockCount, BlockSize, encoder.Parameters);
        byte[] symbol = new byte[BlockSize];

        for (int repeat = 0; repeat < 3; repeat++)
        {
            for (int index = 0; index < encoder.BlockCount + 40; index++)
            {
                encoder.Symbol(index, symbol);
                decoder.Add(index, symbol);
            }
        }

        decoder.IsComplete.Should().BeTrue();
        decoder.Data.AsSpan(0, data.Length).ToArray().Should().Equal(data);
    }

    [Fact]
    public void The_Data_Is_Not_Available_Before_The_Decode_Finishes()
    {
        var decoder = new LtDecoder(10, BlockSize, new LtParameters());

        Func<byte[]> read = () => decoder.Data;

        read.Should().Throw<InvalidOperationException>().WithMessage("*0 of 10*");
    }

    [Fact]
    public void A_Reset_Decoder_Takes_The_Same_File_Again()
    {
        byte[] data = Data(40, seed: 9);
        var encoder = new LtEncoder(data, BlockSize, new LtParameters());
        var decoder = new LtDecoder(encoder.BlockCount, BlockSize, encoder.Parameters);

        Transfer(encoder, decoder, loss: 0.3, new Random(1)).Should().BeTrue();
        decoder.Reset();
        decoder.Decoded.Should().Be(0);
        decoder.Received.Should().Be(0);
        decoder.Pending.Should().Be(0);

        Transfer(encoder, decoder, loss: 0.3, new Random(2)).Should().BeTrue();
        decoder.Data.AsSpan(0, data.Length).ToArray().Should().Equal(data);
    }

    [Fact]
    public void A_File_That_Does_Not_Fill_Its_Last_Block_Round_Trips()
    {
        // 3 blocks and a byte: the padding must never reach the receiver's copy of the file.
        byte[] data = new byte[(BlockSize * 3) + 1];
        new Random(77).NextBytes(data);
        var encoder = new LtEncoder(data, BlockSize, new LtParameters());
        encoder.BlockCount.Should().Be(4);
        var decoder = new LtDecoder(encoder.BlockCount, BlockSize, encoder.Parameters);

        Transfer(encoder, decoder, loss: 0.4, new Random(78)).Should().BeTrue();
        decoder.Data.AsSpan(0, data.Length).ToArray().Should().Equal(data);
    }

    [Fact]
    public void A_Fountain_With_Nothing_To_Pour_Is_Refused()
    {
        Action build = () => new LtEncoder(ReadOnlyMemory<byte>.Empty, BlockSize, new LtParameters());

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Pours symbols through a lossy channel until the decoder finishes or gives up.</summary>
    private static bool Transfer(LtEncoder encoder, LtDecoder decoder, double loss, Random random)
    {
        byte[] symbol = new byte[encoder.BlockSize];
        int ceiling = (encoder.BlockCount * 40) + 4000;
        for (int index = 0; index < ceiling; index++)
        {
            if (random.NextDouble() < loss)
            {
                continue;
            }

            encoder.Symbol(index, symbol);
            decoder.Add(index, symbol);
            if (decoder.IsComplete)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Data(int blockCount, int seed)
    {
        var data = new byte[blockCount * BlockSize];
        new Random(seed).NextBytes(data);
        return data;
    }
}
