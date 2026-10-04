namespace TestDetta.Eval;

/// <summary>One synthetic change: what TestDetta selected for it and which test classes its mutant failed.</summary>
/// <param name="Target">The changed member.</param>
/// <param name="Detected">True when TestDetta reported a change to the member's type or to the whole project.</param>
/// <param name="MutatedMethods">Compiled methods that threw; 0 when the member was not found in any test output.</param>
/// <param name="KilledClasses">Test classes that failed with the mutant but not without it (<c>Project: Class</c>).</param>
/// <param name="MissedClasses">Of those, the classes TestDetta did not select: a missed test.</param>
/// <param name="SelectedShare">Share of the recorded test classes that TestDetta selected, from 0 to 1.</param>
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
    /// True when the full run crashed or hung, but the classes TestDetta left out passed with the mutant
    /// in place: the failure was in the selection, so TestDetta caught it without a class list to show.
    /// </summary>
    public bool CaughtByCrash { get; init; }

    /// <summary>True when the test outcome came from the <see cref="OutcomeCache"/> and only the selection was computed now.</summary>
    public bool Reused { get; init; }

    /// <summary>True when the mutant failed at least one test class, so it can prove a miss or its absence.</summary>
    public bool Killing => !Inconclusive && (KilledClasses.Count > 0 || CaughtByCrash);

    /// <summary>
    /// What each other selector (drift modes with <see cref="RunSettings.MapAge"/>, dotnet-tia with
    /// <see cref="RunSettings.TiaCli"/>) selected and missed, against the same confirmed failures as the
    /// main result. Empty without them, and when the full run crashed (a crash proves nothing about
    /// which classes a selector left out).
    /// </summary>
    public IReadOnlyDictionary<OtherSelector, OtherOutcome> Others { get; init; } = new Dictionary<OtherSelector, OtherOutcome>();
}

/// <summary>
/// A selection the evaluation judges next to TestDetta's own on the same mutants: TestDetta with maps recorded
/// before the evaluated commit (the drift modes), or another tool.
/// </summary>
internal enum OtherSelector
{
    /// <summary>The older maps through the drift bridge (what TestDetta does).</summary>
    Bridge,

    /// <summary>No maps: every test project decided by name (what TestDetta did with an older map before the bridge).</summary>
    NoMap,

    /// <summary>The older maps as if recorded at the evaluated commit; shows the misses the drift causes unbridged.</summary>
    Naive,

    /// <summary>dotnet-tia's static selection (docs/design/tia-comparison.md).</summary>
    Tia,

    /// <summary>
    /// Every test project the change affects, run whole: project-level selection as dotnet-affected
    /// and similar tools do it, from TestDetta's own project graph and impact. Always measured.
    /// </summary>
    ProjectLevel,

    /// <summary>dotnet-affected itself (whole affected test projects), to confirm <see cref="ProjectLevel"/> with the tool teams use.</summary>
    DotnetAffected,
}

/// <summary>One other selector's result for a mutant.</summary>
/// <param name="Missed">Confirmed failing classes the mode did not select (<c>Project: Class</c>).</param>
/// <param name="SelectedShare">Share of the evaluated commit's recorded classes the mode selected.</param>
internal sealed record OtherOutcome(IReadOnlyList<string> Missed, double SelectedShare)
{
    /// <summary>Why the selector ran everything (dotnet-tia's full-run reason, or its failure); null otherwise.</summary>
    public string? FullRun { get; init; }

    /// <summary>The selector's own analysis time, when it reports one (dotnet-tia).</summary>
    public double? Seconds { get; init; }
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

    /// <summary>The pinned toolchain the run used, such as <c>SDK 10.0.401 + 8.0.31 (osx-arm64)</c>; null for the <c>dotnet</c> on PATH.</summary>
    public string? Toolchain { get; init; }

    /// <summary>Wall-clock time of the evaluation so far, recording and building included.</summary>
    public double Seconds { get; init; }

    /// <summary>First-parent commits between the drift modes' map commit and <see cref="Sha"/>; 0 without drift modes.</summary>
    public int MapAge { get; init; }

    /// <summary>Commit the drift modes' maps were recorded at; null without drift modes.</summary>
    public string? MapCommit { get; init; }
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
