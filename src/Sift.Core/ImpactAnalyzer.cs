using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Maps changed files to the projects they can affect. When unsure it selects more, not less:
/// a missed test is a bug, an extra test only costs CI time.
/// </summary>
/// <param name="logger">Receives one debug line per changed file and per propagated dependency.</param>
public sealed partial class ImpactAnalyzer(ILogger<ImpactAnalyzer> logger)
{
    /// <summary>
    /// Files that MSBuild or the SDK pick up implicitly for every project beneath their directory,
    /// so a change affects all of those projects.
    /// </summary>
    private static readonly FrozenSet<string> DirectoryWideFiles = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp",
        "Directory.Packages.props", "Directory.Solution.props", "Directory.Solution.targets",
        "global.json", "NuGet.config", ".editorconfig");

    /// <summary>Returns the projects affected by <paramref name="changedFiles"/>.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root that <paramref name="changedFiles"/> are relative to.</param>
    /// <param name="changedFiles">Changed file paths relative to <paramref name="repoRoot"/>, as git prints them.</param>
    /// <param name="codeChanges">
    /// Code-level changes of the C# files among <paramref name="changedFiles"/>. Files reported as
    /// cosmetic are skipped; C# files without an entry are treated as changed.
    /// </param>
    public ImpactResult Analyze(
        ProjectGraph graph,
        string repoRoot,
        IEnumerable<string> changedFiles,
        IReadOnlyList<CodeChange> codeChanges)
    {
        var root = Paths.Normalize(repoRoot);
        var reasons = new Dictionary<string, ImpactReason>(Paths.Comparer);
        var unmapped = new List<string>();
        var cosmetic = new HashSet<string>(StringComparer.Ordinal);
        var relevantChanges = new List<CodeChange>(codeChanges.Count);

        foreach (var change in codeChanges)
        {
            if (change.IsCosmetic)
            {
                cosmetic.Add(change.Path);
            }
            else
            {
                relevantChanges.Add(change);
            }
        }

        foreach (var relative in changedFiles)
        {
            if (cosmetic.Contains(relative))
            {
                LogCosmetic(relative);
                continue;
            }

            var file = Paths.Normalize(Path.Combine(root, relative));

            if (DirectoryWideFiles.Contains(Path.GetFileName(file)))
            {
                MarkBuildConfiguration(graph, file, reasons);
                continue;
            }

            var owner = graph.FindOwner(file);
            if (owner is null)
            {
                LogUnmapped(relative);
                unmapped.Add(relative);
                continue;
            }

            LogDirectChange(relative, owner.Path);
            reasons.TryAdd(owner.Path, ImpactReason.DirectChange);
        }

        PropagateToDependents(graph, reasons);

        var affected = reasons
            .Select(kv => new AffectedProject(graph[kv.Key], kv.Value))
            .OrderBy(static a => a.Project.Path, Paths.Comparer)
            .ToList();

        if (logger.IsEnabled(LogLevel.Debug))
        {
            var testCount = 0;
            foreach (var a in affected)
            {
                if (a.Project.IsTestProject)
                {
                    testCount++;
                }
            }

            LogSummary(affected.Count, testCount, unmapped.Count);
        }

        return new ImpactResult(affected, unmapped, [.. cosmetic.Order(StringComparer.Ordinal)], relevantChanges);
    }

    /// <summary>Marks every project in or below the directory of a build-wide <paramref name="file"/>.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="file">Normalized path of the changed build-wide file.</param>
    /// <param name="reasons">Selected projects so far; updated in place.</param>
    private void MarkBuildConfiguration(ProjectGraph graph, string file, Dictionary<string, ImpactReason> reasons)
    {
        var dir = Paths.Normalize(Path.GetDirectoryName(file)!);
        var count = 0;

        foreach (var project in graph.Projects)
        {
            if (!Paths.IsUnder(project.Directory, dir))
            {
                continue;
            }

            reasons.TryAdd(project.Path, ImpactReason.BuildConfiguration);
            count++;
        }

        LogBuildConfiguration(file, count);
    }

    /// <summary>Adds, breadth first, every project that transitively references an already selected one.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="reasons">Selected projects so far; updated in place.</param>
    private void PropagateToDependents(ProjectGraph graph, Dictionary<string, ImpactReason> reasons)
    {
        var queue = new Queue<string>(reasons.Keys);

        while (queue.TryDequeue(out var project))
        {
            foreach (var dependent in graph.DependentsOf(project))
            {
                if (!reasons.TryAdd(dependent, ImpactReason.Dependency))
                {
                    continue;
                }

                LogDependency(dependent, project);
                queue.Enqueue(dependent);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{File} belongs to {Project}")]
    private partial void LogDirectChange(string file, string project);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{File} has only whitespace or comment changes, ignored")]
    private partial void LogCosmetic(string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{File} is outside every project, ignored")]
    private partial void LogUnmapped(string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{File} is build-wide configuration, affects {Count} project(s)")]
    private partial void LogBuildConfiguration(string file, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project} is affected because it references {Dependency}")]
    private partial void LogDependency(string project, string dependency);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{AffectedCount} affected project(s), {TestCount} of them tests, {UnmappedCount} unmapped file(s)")]
    private partial void LogSummary(int affectedCount, int testCount, int unmappedCount);
}
