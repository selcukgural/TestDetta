namespace Sift.Core;

/// <summary>How much of a test project needs to run.</summary>
public enum TestRunMode
{
    /// <summary>Run every test: the impact could not be narrowed below the project.</summary>
    All,

    /// <summary>Run only the listed test classes.</summary>
    Filtered,

    /// <summary>Nothing in the project can observe the change.</summary>
    Skipped,
}

/// <summary>What to run in one affected test project.</summary>
/// <param name="Project">The test project.</param>
/// <param name="Mode">Whether to run all, some or none of its tests.</param>
/// <param name="TestClasses">
/// Test classes to run when <paramref name="Mode"/> is <see cref="TestRunMode.Filtered"/>, as runtime
/// names (<c>App.Tests.OrderTests</c>, nested classes with '+'). Empty otherwise.
/// </param>
/// <param name="Reason">Human readable explanation of the decision.</param>
/// <param name="Estimate">Recorded test time of the whole project and of what was selected; null without timings.</param>
public sealed record TestProjectSelection(
    ProjectNode Project,
    TestRunMode Mode,
    IReadOnlyList<string> TestClasses,
    string Reason,
    TimeEstimate? Estimate = null);

/// <summary>
/// Test time according to the timings of a coverage map. The numbers come from a recording, which
/// runs one test at a time with instrumentation, so they exceed a normal run; the ratio is what matters.
/// </summary>
/// <param name="FullSeconds">Time of every test.</param>
/// <param name="SelectedSeconds">Time of the selected tests, including the fixtures they use.</param>
public sealed record TimeEstimate(double FullSeconds, double SelectedSeconds)
{
    /// <summary>Recorded seconds not spent because tests were not selected.</summary>
    public double SavedSeconds => Math.Max(0, FullSeconds - SelectedSeconds);

    /// <summary>Share of the recorded time saved, from 0 to 1; 0 when nothing was recorded.</summary>
    public double SavedFraction => FullSeconds <= 0 ? 0 : SavedSeconds / FullSeconds;
}

/// <summary>What to run, and what it saves compared with running every test.</summary>
/// <param name="Projects">Decision per affected test project, ordered by path.</param>
/// <param name="Estimate">
/// Totals over every test project with timings, affected or not: an unaffected project saves all
/// of its time. Null when no test project has timings.
/// </param>
/// <param name="ProjectsWithoutTimings">Test projects left out of <paramref name="Estimate"/> for lack of a usable map.</param>
public sealed record TestPlan(IReadOnlyList<TestProjectSelection> Projects, TimeEstimate? Estimate, int ProjectsWithoutTimings);

/// <summary>Input of <see cref="TestSelector.Select"/>.</summary>
/// <param name="Graph">Projects of the repository.</param>
/// <param name="RepoRoot">Repository root directory.</param>
/// <param name="ChangedFiles">Changed paths relative to <paramref name="RepoRoot"/>.</param>
/// <param name="Impact">Project-level result of <see cref="ImpactAnalyzer"/> for the same changes.</param>
/// <param name="CoverageMaps">
/// Coverage maps recorded at the comparison base or bridged from an ancestor (<see cref="Drifts"/>),
/// keyed by normalized test project path. Test projects without a map are selected by name only.
/// </param>
public sealed record TestSelectionRequest(
    ProjectGraph Graph,
    string RepoRoot,
    IReadOnlyList<string> ChangedFiles,
    ImpactResult Impact,
    IReadOnlyDictionary<string, CoverageMap> CoverageMaps)
{
    /// <summary>
    /// Changes since the commit of every map in <see cref="CoverageMaps"/> that was recorded before the
    /// comparison base, matched by <see cref="MapDrift.FromCommit"/>; empty when all maps are current.
    /// </summary>
    public IReadOnlyList<MapDrift> Drifts { get; init; } = [];
}
