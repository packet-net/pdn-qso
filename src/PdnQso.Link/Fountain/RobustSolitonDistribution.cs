namespace PdnQso.Link.Fountain;

/// <summary>
/// The robust soliton degree distribution over degrees 1 to K: how many source blocks a repair
/// symbol combines.
/// </summary>
/// <remarks>
/// <para>
/// Provenance: M. Luby, "LT Codes", FOCS 2002, section 3, in D. J. C. MacKay's notation
/// (<i>Information Theory, Inference, and Learning Algorithms</i>, chapter 50). The three
/// pieces are
/// </para>
/// <list type="bullet">
/// <item><description>the ideal soliton <c>rho(1) = 1/K</c>, <c>rho(d) = 1/(d(d-1))</c> for
/// <c>d = 2..K</c>, which in expectation releases exactly one block per symbol and in practice
/// stalls the moment it is unlucky;</description></item>
/// <item><description><c>S = c ln(K/delta) sqrt(K)</c>, the expected size of the ripple the
/// extra term is there to hold up;</description></item>
/// <item><description>the extra term <c>tau(d) = S/(K d)</c> for <c>d &lt; M</c>,
/// <c>tau(M) = S ln(S/delta)/K</c> at the spike <c>M = K/S</c>, and zero above it, which pays
/// for a supply of degree-1 symbols and one high-degree symbol that mops up the blocks nothing
/// else covered.</description></item>
/// </list>
/// <para>
/// The distribution is <c>mu(d) = (rho(d) + tau(d)) / beta</c> where <c>beta</c> is the sum
/// over all degrees, so it is a distribution by construction and
/// <c>RobustSolitonDistributionTests</c> checks that it is.
/// </para>
/// <para>
/// <b>The spike is clamped into range.</b> <c>M = floor(K/S)</c> is only meaningful when it
/// lands between 1 and K, and for a small file with small <c>c</c> it does not: at K = 10 with
/// the defaults, <c>K/S</c> is about 53, and a symbol cannot combine 53 of 10 blocks. The
/// spike is therefore placed at <c>min(max(floor(K/S), 1), K)</c>, and the <c>tau</c> ramp
/// stops below it. That is a deliberate reading of the formula at the small end rather than a
/// different distribution: at the sizes where the asymptotic argument bites, the clamp never
/// fires.
/// </para>
/// <para>
/// Built once per transfer and then read-only, so nothing here is on a hot path; the sampling
/// that is (<see cref="Sample"/>) is a binary search over a precomputed cumulative table and
/// allocates nothing.
/// </para>
/// </remarks>
public sealed class RobustSolitonDistribution
{
    private readonly double[] _probabilities;
    private readonly double[] _cumulative;

    /// <summary>Builds the distribution for a given number of source blocks.</summary>
    /// <param name="blockCount">K, the number of source blocks; at least 1.</param>
    /// <param name="parameters">The <c>c</c> and <c>delta</c> to shape it with.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockCount"/> is less
    /// than 1, or the parameters are not usable.</exception>
    public RobustSolitonDistribution(int blockCount, LtParameters parameters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockCount, 1);
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();

        BlockCount = blockCount;
        Parameters = parameters;

        // S = c ln(K/delta) sqrt(K). At K = 1 the logarithm of 1/delta is still positive, so S
        // is well defined and the clamp below simply puts the spike on the only degree there is.
        S = parameters.C * Math.Log(blockCount / parameters.Delta) * Math.Sqrt(blockCount);
        int spike = S > 0 ? (int)Math.Floor(blockCount / S) : blockCount;
        SpikeDegree = Math.Clamp(spike, 1, blockCount);

        _probabilities = new double[blockCount];
        double total = 0;
        for (int degree = 1; degree <= blockCount; degree++)
        {
            double value = Ideal(degree) + Extra(degree);
            _probabilities[degree - 1] = value;
            total += value;
        }

        Beta = total;
        _cumulative = new double[blockCount];
        double running = 0;
        for (int i = 0; i < blockCount; i++)
        {
            _probabilities[i] /= total;
            running += _probabilities[i];
            _cumulative[i] = running;
        }

        // Guard the binary search against the last cumulative entry landing a hair below 1
        // through rounding, which would let a draw of 0.9999999999 fall off the end.
        _cumulative[blockCount - 1] = 1.0;

        double mean = 0;
        for (int i = 0; i < blockCount; i++)
        {
            mean += (i + 1) * _probabilities[i];
        }

        MeanDegree = mean;
    }

    /// <summary>K, the number of source blocks the distribution is over.</summary>
    public int BlockCount { get; }

    /// <summary>The <c>c</c> and <c>delta</c> this was built with.</summary>
    public LtParameters Parameters { get; }

    /// <summary><c>S = c ln(K/delta) sqrt(K)</c>, the expected ripple size.</summary>
    public double S { get; }

    /// <summary>
    /// <c>M</c>, the degree carrying the <c>tau</c> spike: <c>floor(K/S)</c> clamped to
    /// <c>1..K</c>. See the class remarks for why it is clamped.
    /// </summary>
    public int SpikeDegree { get; }

    /// <summary>
    /// <c>beta</c>, the sum of <c>rho + tau</c> before normalising: the average number of
    /// blocks a symbol touches is <c>O(ln(K/delta))</c> because of it.
    /// </summary>
    public double Beta { get; }

    /// <summary>The mean degree a symbol drawn from this distribution has.</summary>
    public double MeanDegree { get; }

    /// <summary>
    /// The normalised probability of each degree, index <c>d - 1</c> holding <c>mu(d)</c>.
    /// Sums to 1.
    /// </summary>
    public IReadOnlyList<double> Probabilities => _probabilities;

    /// <summary>The normalised probability <c>mu(d)</c> of one degree.</summary>
    /// <param name="degree">A degree from 1 to <see cref="BlockCount"/>.</param>
    public double Probability(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degree, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degree, BlockCount);
        return _probabilities[degree - 1];
    }

    /// <summary>
    /// The ideal soliton term <c>rho(d)</c>: <c>1/K</c> at degree 1 and <c>1/(d(d-1))</c>
    /// above it. Unnormalised, and exposed so a test can check the formula rather than a
    /// number somebody typed.
    /// </summary>
    /// <param name="degree">A degree from 1 to <see cref="BlockCount"/>.</param>
    public double Ideal(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degree, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degree, BlockCount);
        return degree == 1 ? 1.0 / BlockCount : 1.0 / ((double)degree * (degree - 1));
    }

    /// <summary>
    /// The extra term <c>tau(d)</c>: the <c>S/(K d)</c> ramp below the spike, the
    /// <c>S ln(S/delta)/K</c> spike itself, and zero above it. Unnormalised.
    /// </summary>
    /// <param name="degree">A degree from 1 to <see cref="BlockCount"/>.</param>
    public double Extra(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degree, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degree, BlockCount);
        if (degree < SpikeDegree)
        {
            return S / ((double)BlockCount * degree);
        }

        if (degree > SpikeDegree)
        {
            return 0;
        }

        // At the spike. ln(S/delta) goes negative for a tiny S, which would make the spike a
        // negative probability; a spike that wants to be negative is a spike that is not
        // needed, so it is floored at zero.
        double spike = S * Math.Log(S / Parameters.Delta) / BlockCount;
        return double.IsFinite(spike) && spike > 0 ? spike : 0;
    }

    /// <summary>
    /// Inverse transform sampling: turns a uniform draw into a degree.
    /// </summary>
    /// <param name="uniform">A draw from <c>[0, 1)</c>.</param>
    /// <returns>A degree from 1 to <see cref="BlockCount"/>.</returns>
    /// <remarks>
    /// A binary search over the cumulative table: no allocation, and the same answer on every
    /// machine, which matters because the receiver has to reproduce the sender's choice.
    /// </remarks>
    public int Sample(double uniform)
    {
        double u = uniform;
        if (!(u >= 0))
        {
            u = 0;
        }
        else if (u >= 1)
        {
            u = 0.99999999999;
        }

        int low = 0;
        int high = _cumulative.Length - 1;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (u < _cumulative[mid])
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low + 1;
    }
}
