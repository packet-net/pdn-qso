using PdnQso.Link.Fountain;

namespace PdnQso.Tests.Fountain;

/// <summary>
/// The geometry both ends regenerate from the index alone: the property that lets a symbol
/// carry four bytes of header instead of a block map.
/// </summary>
public class LtSymbolLayoutTests
{
    [Fact]
    public void A_Systematic_Index_Is_Its_Own_Block()
    {
        var layout = new LtSymbolLayout(50, new LtParameters());
        Span<int> neighbours = stackalloc int[layout.MaxDegree];

        for (int index = 0; index < 50; index++)
        {
            layout.Neighbours(index, neighbours).Should().Be(1);
            neighbours[0].Should().Be(index);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public void A_Repair_Symbol_Combines_Distinct_Blocks(int blockCount)
    {
        var layout = new LtSymbolLayout(blockCount, new LtParameters());
        Span<int> neighbours = stackalloc int[layout.MaxDegree];
        var seen = new HashSet<int>();

        for (int index = blockCount; index < blockCount + 2000; index++)
        {
            int degree = layout.Neighbours(index, neighbours);
            degree.Should().BeInRange(1, blockCount);

            seen.Clear();
            for (int i = 0; i < degree; i++)
            {
                neighbours[i].Should().BeInRange(0, blockCount - 1);
                seen.Add(neighbours[i]).Should().BeTrue(
                    $"symbol {index} listed block {neighbours[i]} twice, and a block XORed "
                    + "into a symbol twice is a block that is not there at all");
            }
        }
    }

    [Fact]
    public void The_Same_Seed_Gives_The_Same_Geometry()
    {
        var parameters = new LtParameters { Seed = 0xDEADBEEF };
        var sender = new LtSymbolLayout(300, parameters);
        var receiver = new LtSymbolLayout(300, parameters);
        Span<int> mine = stackalloc int[sender.MaxDegree];
        Span<int> theirs = stackalloc int[receiver.MaxDegree];

        // Deliberately out of order and with gaps: the receiver regenerates symbol 9000
        // without ever having seen 300 to 8999, which is the whole point.
        foreach (int index in new[] { 300, 9000, 301, 12345, 5000, 302 })
        {
            int degree = sender.Neighbours(index, mine);
            receiver.Neighbours(index, theirs).Should().Be(degree);
            mine[..degree].ToArray().Should().Equal(theirs[..degree].ToArray());
        }
    }

    [Fact]
    public void A_Different_Seed_Gives_A_Different_Geometry()
    {
        var a = new LtSymbolLayout(500, new LtParameters { Seed = 1 });
        var b = new LtSymbolLayout(500, new LtParameters { Seed = 2 });
        Span<int> left = stackalloc int[a.MaxDegree];
        Span<int> right = stackalloc int[b.MaxDegree];

        int same = 0;
        for (int index = 500; index < 700; index++)
        {
            int degreeA = a.Neighbours(index, left);
            int degreeB = b.Neighbours(index, right);
            if (degreeA == degreeB && left[..degreeA].SequenceEqual(right[..degreeB]))
            {
                same++;
            }
        }

        same.Should().BeLessThan(10, "two seeds should not produce the same fountain");
    }

    [Fact]
    public void Generating_A_Symbol_Leaves_The_Layout_As_It_Found_It()
    {
        // The neighbour draw is a partial shuffle of an internal permutation that is wound
        // back afterwards; if it were not, symbol 700 would depend on whether 699 was asked
        // for, and the receiver would decode a different file from the one that was sent.
        var layout = new LtSymbolLayout(200, new LtParameters());
        Span<int> first = stackalloc int[layout.MaxDegree];
        Span<int> again = stackalloc int[layout.MaxDegree];

        int degree = layout.Neighbours(777, first);
        for (int index = 200; index < 500; index++)
        {
            layout.Neighbours(index, again);
        }

        layout.Neighbours(777, again).Should().Be(degree);
        again[..degree].ToArray().Should().Equal(first[..degree].ToArray());
    }

    [Fact]
    public void A_Negative_Index_Is_Refused()
    {
        var layout = new LtSymbolLayout(10, new LtParameters());
        int[] neighbours = new int[10];

        Action call = () => layout.Neighbours(-1, neighbours);

        call.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_Buffer_That_Cannot_Hold_The_Worst_Case_Is_Refused()
    {
        var layout = new LtSymbolLayout(10, new LtParameters());
        int[] tooSmall = new int[3];

        Action call = () => layout.Neighbours(11, tooSmall);

        call.Should().Throw<ArgumentException>();
    }
}
