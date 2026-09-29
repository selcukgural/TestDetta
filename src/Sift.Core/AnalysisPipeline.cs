using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>Everything one analysis produced.</summary>
/// <param name="RepoRoot">Repository root directory.</param>
/// <param name="ChangedFiles">Changed paths relative to <paramref name="RepoRoot"/>.</param>
/// <param name="Graph">Projects of the repository.</param>
/// <param name="Impact">Affected projects.</param>
/// <param name="Tests">What to run per affected test project.</param>
public sealed record AnalysisResult(
    string RepoRoot,
    IReadOnlyList<string> ChangedFiles,
    ProjectGraph Graph,
    ImpactResult Impact,
    IReadOnlyList<TestProjectSelection> Tests);

/// <summary>
/// Runs the whole analysis for a repository: git diff, code-level comparison, project graph,
/// project impact and test selection. Front ends (the CLI, CI integrations) only format the result.
/// </summary>
/// <param name="loggerFactory">Creates the loggers of every step.</param>
public sealed class AnalysisPipeline(ILoggerFactory loggerFactory)
{
    /// <summary>Analyzes the changes of the repository containing <paramref name="repoPath"/> against <paramref name="baseRef"/>.</summary>
    /// <param name="repoPath">Any path inside the repository.</param>
    /// <param name="baseRef">Branch, tag or commit to compare against.</param>
    /// <exception cref="InvalidOperationException">git failed, for example because <paramref name="baseRef"/> does not exist.</exception>
    /// <exception cref="InvalidDataException">A project file is not valid XML.</exception>
    public AnalysisResult Run(string repoPath, string baseRef)
    {
        var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
        var root = git.TopLevel(repoPath);
        var changed = git.ChangedFiles(root, baseRef);
        var mergeBase = git.MergeBase(root, baseRef);

        var detector = new CodeChangeDetector(loggerFactory.CreateLogger<CodeChangeDetector>());
        var codeChanges = new CodeChangeReader(git, detector, loggerFactory.CreateLogger<CodeChangeReader>())
            .Read(root, mergeBase, changed);

        var graph = new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>()).Load(root);
        var impact = new ImpactAnalyzer(loggerFactory.CreateLogger<ImpactAnalyzer>())
            .Analyze(graph, root, changed, codeChanges);
        var tests = new TestSelector(loggerFactory.CreateLogger<TestSelector>())
            .Select(new TestSelectionRequest(graph, root, changed, impact));

        return new AnalysisResult(root, changed, graph, impact, tests);
    }
}
