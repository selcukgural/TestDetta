using Microsoft.Extensions.Logging;
using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>What <c>td record --incremental</c> does with one test project.</summary>
internal enum StepKind
{
    /// <summary>Record every class: there is no usable previous map, or a threshold says so.</summary>
    Full,

    /// <summary>Record only the selected classes and merge them into the previous map.</summary>
    Partial,

    /// <summary>Nothing in the project can observe the commits: keep the previous map, moved to the new commit.</summary>
    Carry,

    /// <summary>The previous map was recorded at the current commit already.</summary>
    Current,
}

/// <summary>The decision for one test project of an incremental recording.</summary>
/// <param name="Project">The test project.</param>
/// <param name="Kind">What to do.</param>
/// <param name="Reason">Why, for the summary.</param>
/// <param name="Previous">The previous map; null when there is none that can be used.</param>
/// <param name="Classes">Classes to record for <see cref="StepKind.Partial"/>; empty otherwise.</param>
internal sealed record ProjectStep(ProjectNode Project, StepKind Kind, string Reason, CoverageMap? Previous, IReadOnlyList<string> Classes)
{
    /// <summary>The code changes since the previous map, for the members they make unobserved; empty for a full recording.</summary>
    public IReadOnlyList<SymbolChange> Changes { get; init; } = [];
}

/// <summary>What the planner needs besides the projects.</summary>
/// <param name="Root">Repository root.</param>
/// <param name="Graph">Projects of the repository at the current commit.</param>
/// <param name="Settings">Options of <c>td record</c>.</param>
/// <param name="MaxMapAge">How many first-parent commits the previous map may be behind.</param>
/// <param name="SelectionLicensed">False when no license covers selection: every project is then recorded in full.</param>
internal sealed record IncrementalContext(string Root, ProjectGraph Graph, RecordSettings Settings, int MaxMapAge, bool SelectionLicensed)
{
    /// <summary>What a changed non-code file runs in the selection the step is planned with, as <c>td test</c> would decide it.</summary>
    public NonCodeFileRule NonCodeFiles { get; init; } = AnalysisOptions.DefaultNonCodeFiles;

    /// <summary>The team's <c>--not-build-inputs</c> patterns; see <see cref="AnalysisOptions.NotBuildInputs"/>.</summary>
    public IReadOnlyList<string> NotBuildInputs { get; init; } = [];
}

/// <summary>
/// Plans <c>td record --incremental</c> (docs/design/incremental-recording.md): for each test project, the classes the
/// commits since its previous map can affect, decided by the same selection <c>td test</c> makes with that map as an
/// exact map at its own commit; or a full recording when the map cannot be used or a threshold is reached.
/// </summary>
/// <param name="loggerFactory">Creates the loggers of the analysis.</param>
/// <param name="evaluator">Evaluates projects with MSBuild for the analysis.</param>
internal sealed partial class IncrementalRecording(ILoggerFactory loggerFactory, IProjectEvaluator evaluator)
{
    /// <summary>Logger of the planner's decisions.</summary>
    private readonly ILogger _logger = loggerFactory.CreateLogger<IncrementalRecording>();

    /// <summary>Decides what to record for every test project.</summary>
    /// <param name="context">Repository, settings and license.</param>
    /// <param name="projects">Test projects to record.</param>
    /// <param name="now">The current time, for the age threshold.</param>
    public List<ProjectStep> Plan(IncrementalContext context, IReadOnlyList<ProjectNode> projects, DateTimeOffset now)
    {
        var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
        var head = git.Head(context.Root);
        var steps = new List<ProjectStep>(projects.Count);
        var waiting = new Dictionary<string, List<(ProjectNode Project, CoverageMap Map)>>(StringComparer.OrdinalIgnoreCase);
        var distances = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var map = ReadMap(context.Root, project);
            if (map is not null && string.Equals(map.Commit, head, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new ProjectStep(project, StepKind.Current, "its map was recorded at this commit", map, []));
                continue;
            }

            if (FullReason(context, map, now) is { } reason)
            {
                steps.Add(new ProjectStep(project, StepKind.Full, reason, map, []));
                continue;
            }

            if (!distances.TryGetValue(map!.Commit, out var distance))
            {
                distance = git.FirstParentDistance(context.Root, map.Commit, head);
                distances[map.Commit] = distance;
            }

            if (distance is not { } behind || behind > context.MaxMapAge)
            {
                steps.Add(new ProjectStep(project, StepKind.Full,
                    $"its map ({Short(map.Commit)}) is not within {context.MaxMapAge} first-parent commits before this one", map, []));
                continue;
            }

            if (!waiting.TryGetValue(map.Commit, out var group))
            {
                waiting[map.Commit] = group = [];
            }

            group.Add((project, map));
        }

        foreach (var (commit, group) in waiting)
        {
            steps.AddRange(Select(context, commit, group));
        }

        steps.Sort(static (a, b) => string.CompareOrdinal(a.Project.Path, b.Project.Path));
        return steps;
    }

    /// <summary>Why a project is recorded in full before any selection, or null when its map can start an incremental step.</summary>
    /// <param name="context">Repository, settings and license.</param>
    /// <param name="map">The project's previous map, or null.</param>
    /// <param name="now">The current time.</param>
    private static string? FullReason(IncrementalContext context, CoverageMap? map, DateTimeOffset now)
    {
        if (map is null)
        {
            return "it has no previous map";
        }

        if (!context.SelectionLicensed)
        {
            return "no license covers selection";
        }

        // A map that cannot tell which classes it saw, or which failed, cannot say which entries are safe to carry over.
        if (!map.KnowsClasses)
        {
            return "its map does not know every class the runner reported";
        }

        if (map.Platform != System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)
        {
            return $"its map was recorded on {map.Platform ?? "an unknown platform"}";
        }

        if (context.Settings.Framework is { } framework && map.Framework is { } recorded && !string.Equals(framework, recorded, StringComparison.OrdinalIgnoreCase))
        {
            return $"its map was recorded for {recorded}";
        }

        if (map.Lineage is not { } lineage)
        {
            return null;
        }

        var thresholds = context.Settings.Thresholds;
        if (lineage.Steps >= thresholds.FullEvery)
        {
            return $"{lineage.Steps} incremental step(s) since the last full map (--full-record-every {thresholds.FullEvery})";
        }

        return now - lineage.FullRecordedAt >= TimeSpan.FromDays(thresholds.FullDays)
            ? $"the last full map is {(now - lineage.FullRecordedAt).TotalDays:0} day(s) old (--full-record-days {thresholds.FullDays})"
            : null;
    }

    /// <summary>
    /// Selects with the maps at <paramref name="commit"/> as exact maps and turns each project's selection into a step.
    /// </summary>
    /// <param name="context">Repository, settings and license.</param>
    /// <param name="commit">Commit of the previous maps.</param>
    /// <param name="group">Projects whose previous map is at that commit.</param>
    private IEnumerable<ProjectStep> Select(IncrementalContext context, string commit, List<(ProjectNode Project, CoverageMap Map)> group)
    {
        var analysis = new AnalysisPipeline(loggerFactory, evaluator).Run(context.Root, commit, new AnalysisOptions { MaxMapAge = 0, NonCodeFiles = context.NonCodeFiles, NotBuildInputs = context.NotBuildInputs });
        var changes = analysis.Impact.CodeChanges.SelectMany(static c => c.Symbols).ToList();
        foreach (var (project, map) in group)
        {
            var selection = analysis.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, project.Path, StringComparison.Ordinal));
            LogSelection(project.Path, commit, selection?.Mode, selection?.TestClasses.Count ?? 0);

            if (selection is null || selection.Mode == TestRunMode.Skipped)
            {
                yield return new ProjectStep(project, StepKind.Carry, $"nothing in it can observe the changes since {Short(commit)}", map, []) { Changes = changes };
                continue;
            }

            if (selection.Mode == TestRunMode.All)
            {
                yield return new ProjectStep(project, StepKind.Full, $"the changes since {Short(commit)} reach the whole project: {selection.Reason}", map, []);
                continue;
            }

            var total = map.Classes.Count + (map.FailedClasses?.Count ?? 0) + (map.SkippedClasses?.Count ?? 0);
            var share = 100.0 * selection.TestClasses.Count / Math.Max(1, total);
            if (share > context.Settings.Thresholds.FullShare)
            {
                yield return new ProjectStep(project, StepKind.Full,
                    $"the changes since {Short(commit)} select {share:0}% of its classes (--full-record-share {context.Settings.Thresholds.FullShare})", map, []);
                continue;
            }

            yield return new ProjectStep(project, StepKind.Partial,
                $"{selection.TestClasses.Count} of {total} class(es) can observe the changes since {Short(commit)}", map, selection.TestClasses) { Changes = changes };
        }
    }

    /// <summary>The map of <paramref name="project"/>, or null when it has none this TestDetta can read.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="project">A test project.</param>
    private CoverageMap? ReadMap(string root, ProjectNode project)
    {
        try
        {
            return CoverageMapStore.Read(CoverageMapStore.PathFor(root, project.Path));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            LogUnreadableMap(project.Path, ex.Message);
            return null;
        }
    }

    /// <summary>The first 12 characters of a commit id, as the summary shows it.</summary>
    /// <param name="commit">Full commit id.</param>
    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project}: selection since {Commit} is {Mode} (none: unaffected) with {Classes} class(es)")]
    private partial void LogSelection(string project, string commit, TestRunMode? mode, int classes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project}: its previous map cannot be read ({Reason}); it is recorded in full")]
    private partial void LogUnreadableMap(string project, string reason);
}
