namespace TestDetta.Mutants;

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
