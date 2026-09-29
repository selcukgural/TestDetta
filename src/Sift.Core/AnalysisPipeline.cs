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
public sealed partial class AnalysisPipeline(ILoggerFactory loggerFactory)
{
    /// <summary>Logger for decisions of the pipeline itself, such as ignoring a stale coverage map.</summary>
    private readonly ILogger _logger = loggerFactory.CreateLogger<AnalysisPipeline>();

    /// <summary>Analyzes the changes of the repository containing <paramref name="repoPath"/> against <paramref name="baseRef"/>.</summary>
    /// <param name="repoPath">Any path inside the repository.</param>
    /// <param name="baseRef">Branch, tag or commit to compare against.</param>
    /// <exception cref="InvalidOperationException">git failed, for example because <paramref name="baseRef"/> does not exist.</exception>
    /// <exception cref="InvalidDataException">A project file is not valid XML.</exception>
    public AnalysisResult Run(string repoPath, string baseRef)
    {
        var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
        var root = git.TopLevel(repoPath);
        // Sift's own data (coverage maps) is never part of the code under test.
        var changed = git.ChangedFiles(root, baseRef)
            .Where(static f => !f.StartsWith(".sift/", StringComparison.Ordinal))
            .ToList();
        var mergeBase = git.MergeBase(root, baseRef);

        var detector = new CodeChangeDetector(loggerFactory.CreateLogger<CodeChangeDetector>());
        var codeChanges = new CodeChangeReader(git, detector, loggerFactory.CreateLogger<CodeChangeReader>())
            .Read(root, mergeBase, changed);

        var graph = new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>()).Load(root);
        var impact = new ImpactAnalyzer(loggerFactory.CreateLogger<ImpactAnalyzer>())
            .Analyze(graph, root, changed, codeChanges);
        var maps = LoadCoverageMaps(root, graph, mergeBase);
        var tests = new TestSelector(loggerFactory.CreateLogger<TestSelector>())
            .Select(new TestSelectionRequest(graph, root, changed, impact, maps));

        return new AnalysisResult(root, changed, graph, impact, tests);
    }

    /// <summary>
    /// Coverage maps of the repository's test projects that were recorded exactly at
    /// <paramref name="baseCommit"/>. A map from another commit is ignored: code changed since then
    /// may have created calls the map does not know.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="baseCommit">Merge base the changes are compared with.</param>
    private Dictionary<string, CoverageMap> LoadCoverageMaps(string root, ProjectGraph graph, string baseCommit)
    {
        var maps = new Dictionary<string, CoverageMap>(Paths.Comparer);
        foreach (var project in graph.Projects)
        {
            if (!project.IsTestProject || CoverageMapStore.Read(CoverageMapStore.PathFor(root, project.Path)) is not { } map)
            {
                continue;
            }

            if (!string.Equals(map.Commit, baseCommit, StringComparison.OrdinalIgnoreCase))
            {
                LogStaleMap(project.Path, map.Commit, baseCommit);
                continue;
            }

            maps[project.Path] = map;
        }

        return maps;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ignoring the coverage map of {Project}: recorded at {MapCommit}, but the base is {BaseCommit}. Run 'sift record' on the base commit")]
    private partial void LogStaleMap(string project, string mapCommit, string baseCommit);
}
