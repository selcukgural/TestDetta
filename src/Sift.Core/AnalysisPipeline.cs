using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>Everything one analysis produced.</summary>
/// <param name="RepoRoot">Repository root directory.</param>
/// <param name="ChangedFiles">Changed paths relative to <paramref name="RepoRoot"/>.</param>
/// <param name="Graph">Projects of the repository.</param>
/// <param name="Impact">Affected projects.</param>
/// <param name="Plan">What to run per affected test project, and the estimated saving.</param>
public sealed record AnalysisResult(
    string RepoRoot,
    IReadOnlyList<string> ChangedFiles,
    ProjectGraph Graph,
    ImpactResult Impact,
    TestPlan Plan)
{
    /// <summary>Changes between older coverage maps in use and the merge base; empty when every map is current.</summary>
    public IReadOnlyList<MapDrift> Drifts { get; init; } = [];

    /// <summary>What to run per affected test project.</summary>
    public IReadOnlyList<TestProjectSelection> Tests => Plan.Projects;
}

/// <summary>
/// Runs the whole analysis for a repository: git diff, code-level comparison, project graph,
/// project impact and test selection. Front ends (the CLI, CI integrations) only format the result.
/// </summary>
/// <param name="loggerFactory">Creates the loggers of every step.</param>
/// <param name="evaluator">Evaluates the projects with MSBuild for the graph; null to read them as XML only.</param>
public sealed partial class AnalysisPipeline(ILoggerFactory loggerFactory, IProjectEvaluator? evaluator = null)
{
    /// <summary>Logger for decisions of the pipeline itself, such as ignoring a stale coverage map.</summary>
    private readonly ILogger _logger = loggerFactory.CreateLogger<AnalysisPipeline>();

    /// <summary>Analyzes the changes of the repository containing <paramref name="repoPath"/> against <paramref name="baseRef"/>.</summary>
    /// <param name="repoPath">Any path inside the repository.</param>
    /// <param name="baseRef">Branch, tag or commit to compare against.</param>
    /// <exception cref="InvalidOperationException">git failed, for example because <paramref name="baseRef"/> does not exist.</exception>
    /// <exception cref="InvalidDataException">A project file is not valid XML.</exception>
    public AnalysisResult Run(string repoPath, string baseRef) => Run(repoPath, baseRef, new AnalysisOptions());

    /// <summary>Analyzes the changes of the repository containing <paramref name="repoPath"/> against <paramref name="baseRef"/>.</summary>
    /// <param name="repoPath">Any path inside the repository.</param>
    /// <param name="baseRef">Branch, tag or commit to compare against.</param>
    /// <param name="options">How coverage maps are used.</param>
    /// <exception cref="InvalidOperationException">git failed, for example because <paramref name="baseRef"/> does not exist.</exception>
    /// <exception cref="InvalidDataException">A project file is not valid XML.</exception>
    public AnalysisResult Run(string repoPath, string baseRef, AnalysisOptions options)
    {
        var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
        var root = git.TopLevel(repoPath);
        // Sift's own data (coverage maps) is never part of the code under test.
        var changed = git.ChangedFiles(root, baseRef)
            .Where(static f => !f.StartsWith(".sift/", StringComparison.Ordinal))
            .ToList();
        var mergeBase = git.MergeBase(root, baseRef);

        // The graph first: its evaluation says which preprocessor symbols each project compiles with.
        var graph = new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>(), evaluator).Load(root);
        var symbolSets = SymbolSetsOf(graph, root);
        var reader = new CodeChangeReader(git, new CodeChangeDetector(loggerFactory.CreateLogger<CodeChangeDetector>()), loggerFactory.CreateLogger<CodeChangeReader>());
        var codeChanges = reader.Read(root, mergeBase, changed, symbolSets);
        var packageChanges = PackageChanges(git, root, mergeBase, null, changed);
        var impactAnalyzer = new ImpactAnalyzer(loggerFactory.CreateLogger<ImpactAnalyzer>());
        var impact = impactAnalyzer.Analyze(graph, root, changed, codeChanges, packageChanges);
        var (maps, mapCommits) = LoadCoverageMaps(git, root, graph, mergeBase, options);

        var drifts = new List<MapDrift>();
        foreach (var (commit, distance) in mapCommits)
        {
            if (distance == 0)
            {
                continue;
            }

            var driftFiles = git.ChangedBetween(root, commit, mergeBase)
                .Where(static f => !f.StartsWith(".sift/", StringComparison.Ordinal))
                .ToList();
            var driftImpact = impactAnalyzer.Analyze(
                graph,
                root,
                driftFiles,
                reader.ReadBetween(root, commit, mergeBase, driftFiles, symbolSets),
                PackageChanges(git, root, commit, mergeBase, driftFiles));
            drifts.Add(new MapDrift(commit, distance, driftFiles, driftImpact));
            LogDrift(commit, distance, driftFiles.Count);
        }

        var tests = new TestSelector(loggerFactory.CreateLogger<TestSelector>())
            .Select(new TestSelectionRequest(graph, root, changed, impact, maps) { Drifts = drifts, DriftFallback = options.DriftFallback });

        return new AnalysisResult(root, changed, graph, impact, Plan(graph, tests, maps)) { Drifts = drifts };
    }

    /// <summary>The symbol sets of every project that compiles a file, by repository-relative path.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="root">Repository root.</param>
    private static Func<string, IReadOnlyCollection<IReadOnlyList<string>>> SymbolSetsOf(ProjectGraph graph, string root) =>
        file => [.. graph.ResolveFile(Paths.Normalize(Path.Combine(root, file)))
            .Where(static o => o.Role == FileRole.Source)
            .SelectMany(static o => o.Project.SymbolSets)];

    /// <summary>
    /// Adds up the estimate over every test project with timings. An unaffected test project runs no
    /// test at all, so all of its recorded time counts as saved.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="tests">Decisions for the affected test projects.</param>
    /// <param name="maps">Usable coverage maps by test project.</param>
    private static TestPlan Plan(ProjectGraph graph, IReadOnlyList<TestProjectSelection> tests, Dictionary<string, CoverageMap> maps)
    {
        double full = 0, selected = 0;
        var withTimings = 0;
        var withoutTimings = 0;

        foreach (var project in graph.Projects)
        {
            if (!project.IsTestProject)
            {
                continue;
            }

            var decision = tests.FirstOrDefault(t => Paths.Comparer.Equals(t.Project.Path, project.Path));
            if (decision?.Estimate is { } estimate)
            {
                full += estimate.FullSeconds;
                selected += estimate.SelectedSeconds;
                withTimings++;
                continue;
            }

            if (decision is null && maps.GetValueOrDefault(project.Path)?.Timings is { } timings)
            {
                full += timings.StartupSeconds + timings.ClassSeconds.Values.Sum() + timings.FixtureSeconds.Values.Sum();
                withTimings++;
                continue;
            }

            withoutTimings++;
        }

        return new TestPlan(tests, withTimings == 0 ? null : new TimeEstimate(full, selected), withoutTimings);
    }

    /// <summary>Compares the old and new versions of every changed central package versions file.</summary>
    /// <param name="git">Reads the versions at commits.</param>
    /// <param name="root">Repository root.</param>
    /// <param name="baseCommit">Commit of the old versions.</param>
    /// <param name="newCommit">Commit of the new versions; null for the working tree.</param>
    /// <param name="changed">Changed files.</param>
    private static List<PackageVersionChange> PackageChanges(GitClient git, string root, string baseCommit, string? newCommit, IReadOnlyList<string> changed)
    {
        var files = changed
            .Where(static f => string.Equals(Path.GetFileName(f), PackageVersionChange.FileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
        {
            return [];
        }

        var old = git.ReadFiles(root, baseCommit, files);
        var atNewCommit = newCommit is null ? null : git.ReadFiles(root, newCommit, files);
        var changes = new List<PackageVersionChange>(files.Count);
        foreach (var file in files)
        {
            var current = Path.Combine(root, file);
            var newText = atNewCommit is null
                ? File.Exists(current) ? File.ReadAllText(current) : null
                : atNewCommit.TryGetValue(file, out var newBytes) ? System.Text.Encoding.UTF8.GetString(newBytes) : null;
            changes.Add(PackageVersionChange.Compare(
                file,
                old.TryGetValue(file, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null,
                newText));
        }

        return changes;
    }

    /// <summary>
    /// Coverage maps of the repository's test projects that can be used against <paramref name="baseCommit"/>:
    /// those recorded at it, and those recorded at an ancestor at most <see cref="AnalysisOptions.MaxMapAge"/>
    /// first-parent commits behind, which the selection bridges with the changes in between.
    /// Any other map is ignored: code changed since then may have created calls the map does not know.
    /// </summary>
    /// <param name="git">Checks ancestry.</param>
    /// <param name="root">Repository root.</param>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="baseCommit">Merge base the changes are compared with.</param>
    /// <param name="options">Age limit and map use.</param>
    /// <returns>Usable maps by test project, and each map commit with its distance from the base.</returns>
    private (Dictionary<string, CoverageMap> Maps, Dictionary<string, int> Commits) LoadCoverageMaps(
        GitClient git, string root, ProjectGraph graph, string baseCommit, AnalysisOptions options)
    {
        var maps = new Dictionary<string, CoverageMap>(Paths.Comparer);
        var commits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (options.Maps == MapUse.None)
        {
            return (maps, commits);
        }

        var distances = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in graph.Projects)
        {
            if (!project.IsTestProject || CoverageMapStore.Read(CoverageMapStore.PathFor(root, project.Path)) is not { } map)
            {
                continue;
            }

            if (string.Equals(map.Commit, baseCommit, StringComparison.OrdinalIgnoreCase) || options.Maps == MapUse.AsIfCurrent)
            {
                maps[project.Path] = map;
                commits[map.Commit] = 0;
                continue;
            }

            if (!distances.TryGetValue(map.Commit, out var distance))
            {
                distance = options.MaxMapAge > 0 ? git.FirstParentDistance(root, map.Commit, baseCommit) : null;
                distances[map.Commit] = distance;
            }

            if (distance is not { } behind || behind > options.MaxMapAge)
            {
                LogStaleMap(project.Path, map.Commit, baseCommit, options.MaxMapAge);
                continue;
            }

            maps[project.Path] = map;
            commits[map.Commit] = behind;
        }

        return (maps, commits);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ignoring the coverage map of {Project}: recorded at {MapCommit}, which is not an ancestor of the base {BaseCommit} within {MaxMapAge} commit(s). Run 'sift record' on a newer commit")]
    private partial void LogStaleMap(string project, string mapCommit, string baseCommit, int maxMapAge);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Coverage maps from {MapCommit} are {Commits} commit(s) behind the base; bridging {Files} file(s) changed in between")]
    private partial void LogDrift(string mapCommit, int commits, int files);
}
