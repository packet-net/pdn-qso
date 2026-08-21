using PdnQso.Link.Fountain;

namespace PdnQso.Tests.Fountain;

/// <summary>
/// The degree distribution of docs/design.md section 4, checked against the formulas rather
/// than against numbers somebody typed: Luby's LT codes paper in MacKay's notation.
/// </summary>
public class RobustSolitonDistributionTests
{
    private static readonly LtParameters Defaults = new();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void The_Probabilities_Sum_To_One(int blockCount)
    {
        var distribution = new RobustSolitonDistribution(blockCount, Defaults);

        double total = 0;
        foreach (double probability in distribution.Probabilities)
        {
            probability.Should().BeGreaterThanOrEqualTo(0);
            total += probability;
        }

        distribution.Probabilities.Should().HaveCount(blockCount);
        total.Should().BeApproximately(1.0, 1e-12);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    public void The_Ideal_Soliton_Term_Is_The_Formula(int blockCount)
    {
        var distribution = new RobustSolitonDistribution(blockCount, Defaults);

        distribution.Ideal(1).Should().BeApproximately(1.0 / blockCount, 1e-15);
        for (int degree = 2; degree <= blockCount; degree++)
        {
            distribution.Ideal(degree).Should()
                .BeApproximately(1.0 / ((double)degree * (degree - 1)), 1e-15);
        }

        // The ideal soliton is itself a distribution: rho sums to 1 over 1..K by construction.
        double total = 0;
        for (int degree = 1; degree <= blockCount; degree++)
        {
            total += distribution.Ideal(degree);
        }

        total.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void The_Extra_Term_Is_The_Ramp_The_Spike_And_Nothing_Above_It()
    {
        const int BlockCount = 1000;
        var distribution = new RobustSolitonDistribution(BlockCount, Defaults);

        double s = Defaults.C * Math.Log(BlockCount / Defaults.Delta) * Math.Sqrt(BlockCount);
        distribution.S.Should().BeApproximately(s, 1e-12);
        distribution.SpikeDegree.Should().Be((int)Math.Floor(BlockCount / s));

        for (int degree = 1; degree < distribution.SpikeDegree; degree++)
        {
            distribution.Extra(degree).Should()
                .BeApproximately(s / (BlockCount * (double)degree), 1e-15);
        }

        distribution.Extra(distribution.SpikeDegree).Should()
            .BeApproximately(s * Math.Log(s / Defaults.Delta) / BlockCount, 1e-15);

        for (int degree = distribution.SpikeDegree + 1; degree <= BlockCount; degree++)
        {
            distribution.Extra(degree).Should().Be(0);
        }
    }

    [Fact]
    public void Beta_Is_The_Sum_Of_The_Two_Terms()
    {
        var distribution = new RobustSolitonDistribution(250, Defaults);

        double total = 0;
        for (int degree = 1; degree <= 250; degree++)
        {
            total += distribution.Ideal(degree) + distribution.Extra(degree);
        }

        distribution.Beta.Should().BeApproximately(total, 1e-12);

        // And every normalised probability is (rho + tau) / beta, which is what makes the
        // whole thing a distribution rather than a pair of curves.
        for (int degree = 1; degree <= 250; degree++)
        {
            distribution.Probability(degree).Should().BeApproximately(
                (distribution.Ideal(degree) + distribution.Extra(degree)) / distribution.Beta, 1e-12);
        }
    }

    [Fact]
    public void A_Single_Block_Can_Only_Have_Degree_One()
    {
        var distribution = new RobustSolitonDistribution(1, Defaults);

        distribution.Probabilities.Should().ContainSingle().Which.Should().Be(1.0);
        distribution.Sample(0).Should().Be(1);
        distribution.Sample(0.999999).Should().Be(1);
        distribution.MeanDegree.Should().Be(1);
    }

    [Fact]
    public void The_Spike_Is_Clamped_Into_The_Degrees_That_Exist()
    {
        // K/S is about 53 here, and a symbol cannot combine 53 of 10 blocks.
        var distribution = new RobustSolitonDistribution(10, Defaults);

        distribution.SpikeDegree.Should().Be(10);
        distribution.Probabilities.Should().HaveCount(10);
    }

    [Fact]
    public void Sampling_Follows_The_Probabilities()
    {
        const int BlockCount = 200;
        var distribution = new RobustSolitonDistribution(BlockCount, Defaults);
        var counts = new int[BlockCount + 1];
        var random = new Random(20260821);
        const int Draws = 400_000;

        for (int i = 0; i < Draws; i++)
        {
            counts[distribution.Sample(random.NextDouble())]++;
        }

        counts[0].Should().Be(0, "degrees start at 1");
        for (int degree = 1; degree <= BlockCount; degree++)
        {
            double expected = distribution.Probability(degree);
            double measured = (double)counts[degree] / Draws;
            // Three standard errors of a binomial, plus a floor so that a degree with a
            // probability of one in ten thousand does not make this a lottery.
            double tolerance = (3 * Math.Sqrt(expected * (1 - expected) / Draws)) + 1e-4;
            measured.Should().BeApproximately(expected, tolerance, $"degree {degree}");
        }
    }

    [Fact]
    public void The_Mean_Degree_Grows_Like_The_Logarithm_Of_The_File()
    {
        // Not a tight claim, a shape claim: the average symbol touches a handful of blocks
        // however big the file is, which is the reason an LT decode is cheap.
        new RobustSolitonDistribution(10, Defaults).MeanDegree.Should().BeInRange(2, 5);
        new RobustSolitonDistribution(100, Defaults).MeanDegree.Should().BeInRange(4, 9);
        new RobustSolitonDistribution(1000, Defaults).MeanDegree.Should().BeInRange(6, 14);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(-1.0, 0.5)]
    [InlineData(0.1, 0.0)]
    [InlineData(0.1, 1.0)]
    [InlineData(0.1, 1.5)]
    [InlineData(double.NaN, 0.5)]
    public void Parameters_That_Are_Not_A_Distribution_Are_Refused(double c, double delta)
    {
        Action build = () => new RobustSolitonDistribution(10, new LtParameters { C = c, Delta = delta });

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_File_Of_No_Blocks_Is_Not_A_File()
    {
        Action build = () => new RobustSolitonDistribution(0, Defaults);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }
}
