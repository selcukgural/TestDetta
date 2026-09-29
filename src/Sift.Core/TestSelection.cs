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
public sealed record TestProjectSelection(
    ProjectNode Project,
    TestRunMode Mode,
    IReadOnlyList<string> TestClasses,
    string Reason);

/// <summary>Input of <see cref="TestSelector.Select"/>.</summary>
/// <param name="Graph">Projects of the repository.</param>
/// <param name="RepoRoot">Repository root directory.</param>
/// <param name="ChangedFiles">Changed paths relative to <paramref name="RepoRoot"/>.</param>
/// <param name="Impact">Project-level result of <see cref="ImpactAnalyzer"/> for the same changes.</param>
public sealed record TestSelectionRequest(
    ProjectGraph Graph,
    string RepoRoot,
    IReadOnlyList<string> ChangedFiles,
    ImpactResult Impact);
