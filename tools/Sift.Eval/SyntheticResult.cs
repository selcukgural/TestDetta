namespace Sift.Eval;

/// <summary>One synthetic change: what Sift selected for it and which test classes its mutant failed.</summary>
/// <param name="Target">The changed member.</param>
/// <param name="Detected">True when Sift reported a change to the member's type or to the whole project.</param>
/// <param name="MutatedMethods">Compiled methods that threw; 0 when the member was not found in any test output.</param>
/// <param name="KilledClasses">Test classes that failed with the mutant but not without it (<c>Project: Class</c>).</param>
/// <param name="MissedClasses">Of those, the classes Sift did not select: a missed test.</param>
/// <param name="SelectedShare">Share of the recorded test classes that Sift selected, from 0 to 1.</param>
/// <param name="Inconclusive">True when a mutant run crashed, so neither list can be trusted.</param>
internal sealed record SyntheticMutant(
    SyntheticTarget Target,
    bool Detected,
    int MutatedMethods,
    IReadOnlyList<string> KilledClasses,
    IReadOnlyList<string> MissedClasses,
    double SelectedShare,
    bool Inconclusive)
{
    /// <summary>
    /// True when the full run crashed or hung, but the classes Sift left out passed with the mutant
    /// in place: the failure was in the selection, so Sift caught it without a class list to show.
    /// </summary>
    public bool CaughtByCrash { get; init; }

    /// <summary>True when the test outcome came from the <see cref="OutcomeCache"/> and only the selection was computed now.</summary>
    public bool Reused { get; init; }

    /// <summary>True when the mutant failed at least one test class, so it can prove a miss or its absence.</summary>
    public bool Killing => !Inconclusive && (KilledClasses.Count > 0 || CaughtByCrash);
}

/// <summary>A synthetic evaluation of one commit.</summary>
/// <param name="Sha">Commit the changes were made on.</param>
/// <param name="TestClasses">Test classes in the recorded maps.</param>
/// <param name="BaselineSeconds">Time of the full suite without mutants.</param>
/// <param name="Mutants">One result per sampled member, in sample order.</param>
/// <param name="Error">Why the commit could not be evaluated, or null.</param>
internal sealed record SyntheticRun(string Sha, int TestClasses, double BaselineSeconds, IReadOnlyList<SyntheticMutant> Mutants, string? Error)
{
    /// <summary>Test projects left out because they wrote no test report, relative to the repository root.</summary>
    public IReadOnlyList<string> Unreported { get; init; } = [];

    /// <summary>True when the mutants were switched on in a schema rather than written in one at a time.</summary>
    public bool Schema { get; init; }

    /// <summary>Mutants that ran at once; 1 when one at a time.</summary>
    public int MutantParallelism { get; init; } = 1;
}

/// <summary>Confidence bounds for a miss rate measured with mutants.</summary>
internal static class MissRate
{
    /// <summary>
    /// Exact (Clopper-Pearson) one-sided upper bound of the miss rate: with <paramref name="misses"/>
    /// misses in <paramref name="trials"/> killing mutants, the true rate is below the result with the
    /// given confidence. With no misses this is <c>1 - (1 - confidence)^(1/n)</c>, about <c>3/n</c> at 95%.
    /// </summary>
    /// <param name="misses">Mutants with at least one missed test class.</param>
    /// <param name="trials">Mutants that failed at least one test class.</param>
    /// <param name="confidence">Confidence level, such as 0.95.</param>
    public static double UpperBound(int misses, int trials, double confidence = 0.95)
    {
        if (trials == 0 || misses >= trials)
        {
            return 1;
        }

        var alpha = 1 - confidence;
        if (misses == 0)
        {
            return 1 - Math.Pow(alpha, 1.0 / trials);
        }

        // The binomial CDF at the observed count falls as the rate rises; find where it equals alpha.
        double low = (double)misses / trials, high = 1;
        for (var i = 0; i < 60; i++)
        {
            var middle = (low + high) / 2;
            if (BinomialCdf(misses, trials, middle) > alpha)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return high;
    }

    /// <summary>P(X ≤ k) for X ~ Binomial(n, p), summed in log space so large n does not overflow.</summary>
    /// <param name="k">Upper count.</param>
    /// <param name="n">Trials.</param>
    /// <param name="p">Success probability.</param>
    private static double BinomialCdf(int k, int n, double p)
    {
        var sum = 0d;
        var logCoefficient = 0d;
        for (var i = 0; i <= k; i++)
        {
            if (i > 0)
            {
                logCoefficient += Math.Log(n - i + 1) - Math.Log(i);
            }

            sum += Math.Exp(logCoefficient + (i * Math.Log(p)) + ((n - i) * Math.Log(1 - p)));
        }

        return sum;
    }
}
