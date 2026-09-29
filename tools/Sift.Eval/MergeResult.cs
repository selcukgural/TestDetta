using Sift.Core;

namespace Sift.Eval;

/// <summary>The merge an evaluation is about.</summary>
/// <param name="Sha">Merge commit.</param>
/// <param name="Parent">First parent, the base Sift compares with.</param>
/// <param name="Subject">Commit subject.</param>
/// <param name="ChangedFiles">Files changed between parent and merge.</param>
internal sealed record MergeInfo(string Sha, string Parent, string Subject, int ChangedFiles);

/// <summary>The same merge tested completely and as Sift selected it.</summary>
/// <param name="Full">Every test of the evaluated test projects.</param>
/// <param name="Selected">The invocations Sift chose.</param>
internal sealed record RunComparison(TestRunOutcome Full, TestRunOutcome Selected);

/// <summary>One injected fault and whether Sift's selection would have caught it.</summary>
/// <param name="Target">The mutated member.</param>
/// <param name="MutatedMethods">Compiled methods that threw; 0 when the member was not found in any test output.</param>
/// <param name="KilledClasses">Test classes that failed with the mutant but not without it.</param>
/// <param name="MissedClasses">Of those, the classes Sift did not select: a missed test.</param>
/// <param name="Inconclusive">True when the mutant's run crashed, so neither list can be trusted.</param>
internal sealed record MutantResult(
    CoveredMethod Target,
    int MutatedMethods,
    IReadOnlyList<string> KilledClasses,
    IReadOnlyList<string> MissedClasses,
    bool Inconclusive = false);

/// <summary>Checks that Sift's selection did not leave out tests that could see the change.</summary>
/// <param name="CoverageMisses">
/// Test classes that, according to coverage recorded after the merge, run a changed member but were
/// not selected. Coverage cannot see field reads or constants, so this check is weaker than mutants.
/// </param>
/// <param name="Mutants">Results of the injected faults.</param>
internal sealed record SafetyCheck(IReadOnlyList<string> CoverageMisses, IReadOnlyList<MutantResult> Mutants)
{
    /// <summary>True when neither check found a missed test class.</summary>
    public bool Passed => CoverageMisses.Count == 0 && Mutants.All(static m => m.MissedClasses.Count == 0);
}

/// <summary>Everything measured for one merge.</summary>
/// <param name="Merge">The merge.</param>
/// <param name="Plan">What Sift selected, with its estimate; null when the merge could not be evaluated.</param>
/// <param name="Runs">Full and selected runs; null when the merge could not be evaluated.</param>
/// <param name="Safety">Missed-test checks; null when the merge could not be evaluated.</param>
/// <param name="Error">Why the merge could not be evaluated, or null.</param>
internal sealed record MergeResult(MergeInfo Merge, TestPlan? Plan, RunComparison? Runs, SafetyCheck? Safety, string? Error);
