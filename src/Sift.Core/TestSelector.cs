using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Narrows the affected test projects down to test classes. Falls back to running a whole project
/// whenever a change cannot be traced by name: non-C# files, build configuration, global usings,
/// and types that nothing names (and so are probably found through reflection).
/// </summary>
/// <param name="logger">Receives the reasons for every fallback and every selected class.</param>
public sealed partial class TestSelector(ILogger<TestSelector> logger)
{
    /// <summary>Decides, per affected test project, which tests to run.</summary>
    /// <param name="request">Changes, project graph and the project-level impact.</param>
    /// <returns>One entry per affected test project, ordered by path.</returns>
    public IReadOnlyList<TestProjectSelection> Select(TestSelectionRequest request)
    {
        var affectedTests = request.Impact.Tests.ToList();
        if (affectedTests.Count == 0)
        {
            return [];
        }

        (request, var driftRunAll) = WithoutDriftedMaps(request);
        var projectLevel = FindProjectLevelChanges(request.Graph, request.RepoRoot, request.ChangedFiles, request.Impact);
        var affectedProjects = new HashSet<string>(Paths.Comparer);
        foreach (var affected in request.Impact.Affected)
        {
            affectedProjects.Add(affected.Project.Path);
        }

        var generated = request.CoverageMaps.Values.SelectMany(static m => m.Generated);
        var index = new SourceIndexBuilder(logger).Build(request.Graph, request.RepoRoot, affectedProjects, generated);
        var coverage = new CoverageSelection(request, affectedTests, index);

        // Test projects without a coverage map are decided by name alone; those with one use a second
        // spread that stops wherever the maps know which tests run the reached code.
        var byName = Trace(request, index, projectLevel, coverage: null, bridged: []);
        var withCoverage = coverage.HasAnyMap
            ? Trace(request, index, projectLevel, coverage, Bridge(request, index, byName))
            : byName;

        var decisions = Decide(new Decision(request.Graph, affectedTests, index, byName, withCoverage, coverage) { DriftRunAll = driftRunAll });
        for (var i = 0; i < decisions.Count; i++)
        {
            decisions[i] = decisions[i] with { Estimate = coverage.Estimate(decisions[i]) };
        }

        return decisions;
    }

    /// <summary>Result of one spread: the propagation and the projects that must run completely.</summary>
    /// <param name="Propagation">Finished propagation.</param>
    /// <param name="Fallbacks">Projects whose whole downstream must run, with the reason.</param>
    private sealed record TraceResult(ImpactPropagation Propagation, Dictionary<string, string> Fallbacks);

    /// <summary>
    /// Drops the maps whose drift changed something no member describes (build files, non-C# files,
    /// global usings) in a project the test project depends on: the map's view of that project may be
    /// wrong anywhere. With <see cref="DriftFallback.RunAll"/> (the default) such a test project runs
    /// completely; with <see cref="DriftFallback.ByName"/> it is decided by name, as without a map.
    /// </summary>
    /// <param name="request">Selection input with every usable map.</param>
    /// <returns>The request without those maps, and the test projects that must run completely, with the reason.</returns>
    private (TestSelectionRequest Request, Dictionary<string, string> RunAll) WithoutDriftedMaps(TestSelectionRequest request)
    {
        var runAll = new Dictionary<string, string>(Paths.Comparer);
        if (request.Drifts.Count == 0)
        {
            return (request, runAll);
        }

        var maps = new Dictionary<string, CoverageMap>(request.CoverageMaps, Paths.Comparer);
        foreach (var drift in request.Drifts)
        {
            var fallbacks = FindProjectLevelChanges(request.Graph, request.RepoRoot, drift.ChangedFiles, drift.Impact);
            foreach (var (project, reason) in RunAll(request.Graph, fallbacks))
            {
                if (maps.TryGetValue(project, out var map) && string.Equals(map.Commit, drift.FromCommit, StringComparison.OrdinalIgnoreCase))
                {
                    maps.Remove(project);
                    LogDriftedMap(project, drift.FromCommit, reason);
                    if (request.DriftFallback == DriftFallback.RunAll)
                    {
                        runAll.TryAdd(project, $"since the coverage map was recorded ({drift.FromCommit[..Math.Min(12, drift.FromCommit.Length)]}), {reason}");
                    }
                }
            }
        }

        return (request with { CoverageMaps = maps }, runAll);
    }

    /// <summary>
    /// The drift bridge: every member the drift added or changed that can reach one of the changes by
    /// name. A test class that reaches a change only through code changed since the map was recorded
    /// ran one of these members at the map's commit (or is new and found by name), so selecting the
    /// classes the map credits to them, or tracing them by name where it cannot answer, covers what the
    /// map does not know (docs/design/map-drift-bridge.md).
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="byName">The uncut name spread of the changes.</param>
    private List<(SymbolChange Change, ProjectNode Owner)> Bridge(TestSelectionRequest request, SourceIndex index, TraceResult byName)
    {
        var bridged = new List<(SymbolChange, ProjectNode)>();
        var root = Paths.Normalize(request.RepoRoot);
        foreach (var drift in request.Drifts)
        {
            foreach (var change in drift.Impact.CodeChanges)
            {
                foreach (var owner in request.Graph.ResolveFile(Paths.Normalize(Path.Combine(root, change.Path))))
                {
                    if (owner.Role != FileRole.Source)
                    {
                        continue;
                    }

                    foreach (var symbol in change.Symbols)
                    {
                        // Code removed before the base is on no path at the base.
                        if (symbol.Kind != SymbolChangeKind.Removed && ReachesChange(index, byName.Propagation, symbol))
                        {
                            LogBridged(symbol.TypeName, symbol.MemberName ?? "(type)", drift.FromCommit);
                            bridged.Add((symbol, owner.Project));
                        }
                    }
                }
            }
        }

        return bridged;
    }

    /// <summary>True when the name spread of the changes reached <paramref name="symbol"/>, or for a type, any of its members.</summary>
    /// <param name="index">Indexed source code.</param>
    /// <param name="propagation">Finished name spread.</param>
    /// <param name="symbol">A member or type changed by the drift.</param>
    private static bool ReachesChange(SourceIndex index, ImpactPropagation propagation, SymbolChange symbol) =>
        symbol.MemberName is null
            ? propagation.AffectedTypes.Contains(symbol.TypeName)
            : index.FindMember(symbol.TypeName, symbol.MemberName) is { } member && propagation.Reached(member);

    /// <summary>
    /// Seeds the detected changes and spreads them by name. With <paramref name="coverage"/>, a change or
    /// reached member that the maps can answer is taken from coverage and not spread further.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="projectLevel">Project-level fallbacks from non-C# and build changes.</param>
    /// <param name="coverage">Coverage maps to stop at, or null for a pure name spread.</param>
    /// <param name="bridged">Drift members to resolve like changes (<see cref="Bridge"/>); empty without drift.</param>
    private TraceResult Trace(
        TestSelectionRequest request,
        SourceIndex index,
        Dictionary<string, string> projectLevel,
        CoverageSelection? coverage,
        List<(SymbolChange Change, ProjectNode Owner)> bridged)
    {
        var fallbacks = new Dictionary<string, string>(projectLevel, Paths.Comparer);
        var propagation = new ImpactPropagation(
            index,
            logger,
            coverage is null
                ? null
                : member => coverage.TryResolve(
                    new SymbolChange(member.TypeName, member.MemberName, SymbolChangeKind.Modified),
                    request.Graph[member.ProjectPath]));
        var root = Paths.Normalize(request.RepoRoot);

        foreach (var change in request.Impact.CodeChanges)
        {
            // A linked file is compiled into every project that includes it; each gets the change.
            foreach (var owner in request.Graph.ResolveFile(Paths.Normalize(Path.Combine(root, change.Path))))
            {
                if (owner.Role != FileRole.Source || fallbacks.ContainsKey(owner.Project.Path))
                {
                    continue;
                }

                foreach (var symbol in change.Symbols)
                {
                    if (coverage?.TryResolve(symbol, owner.Project) == true)
                    {
                        LogResolvedByCoverage(symbol.TypeName, symbol.MemberName ?? "(type)");
                        continue;
                    }

                    propagation.Seed(symbol, owner.Project.Path);
                }
            }
        }

        foreach (var (symbol, owner) in bridged)
        {
            if (fallbacks.ContainsKey(owner.Path) || coverage?.TryResolve(symbol, owner) == true)
            {
                continue;
            }

            propagation.Seed(symbol, owner.Path);
        }

        propagation.Run();
        AddUnreferencedTypes(request.Graph, index, propagation, fallbacks);
        return new TraceResult(propagation, fallbacks);
    }

    /// <summary>
    /// Projects whose change cannot be described by symbols, with the reason: build configuration,
    /// non-C# files, C# files without a detected change, and global usings or assembly attributes.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="changedFiles">Changed paths relative to <paramref name="repoRoot"/>.</param>
    /// <param name="impact">Project-level impact of the same changes.</param>
    private Dictionary<string, string> FindProjectLevelChanges(ProjectGraph graph, string repoRoot, IReadOnlyList<string> changedFiles, ImpactResult impact)
    {
        var fallbacks = new Dictionary<string, string>(Paths.Comparer);
        var codeChanges = new Dictionary<string, CodeChange>(StringComparer.Ordinal);
        foreach (var change in impact.CodeChanges)
        {
            codeChanges[change.Path] = change;
        }

        var cosmetic = new HashSet<string>(impact.CosmeticFiles, StringComparer.Ordinal);
        var root = Paths.Normalize(repoRoot);

        foreach (var file in changedFiles)
        {
            if (cosmetic.Contains(file))
            {
                continue;
            }

            foreach (var owner in graph.ResolveFile(Paths.Normalize(Path.Combine(root, file))))
            {
                var project = owner.Project.Path;
                if (owner.Role == FileRole.BuildInput)
                {
                    AddFallback(fallbacks, project, $"{file} is part of the project's build");
                    continue;
                }

                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    AddFallback(fallbacks, project, $"{file} is not C# code");
                    continue;
                }

                if (!codeChanges.TryGetValue(file, out var change))
                {
                    AddFallback(fallbacks, project, $"{file} could not be compared");
                    continue;
                }

                if (change.AffectsWholeProject)
                {
                    AddFallback(fallbacks, project, $"{file} changes a global using or an assembly attribute");
                }
            }
        }

        // After the file loop, so the file that caused a fallback is what its reason names.
        foreach (var affected in impact.Affected)
        {
            if (affected.Reason == ImpactReason.BuildConfiguration)
            {
                AddFallback(fallbacks, affected.Project.Path, "build configuration changed");
            }

            // Sift reads C# only; a change cannot be traced through F# or Visual Basic code.
            if (affected.Project.Language != ProjectLanguage.CSharp)
            {
                AddFallback(fallbacks, affected.Project.Path, $"{Path.GetFileName(affected.Project.Path)} is not a C# project");
            }
        }

        return fallbacks;
    }

    /// <summary>
    /// Adds a fallback for every spread type outside test projects that no other type names and
    /// that a framework could discover (it has a base list or attributes): controllers, message
    /// handlers, hosted services. Name tracing cannot see who runs those. A plain type that nothing
    /// names is dead code and cannot affect a test. A type that no longer exists is unknown, so it
    /// also falls back.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="propagation">Finished propagation.</param>
    /// <param name="fallbacks">Project-level fallbacks; updated in place.</param>
    private void AddUnreferencedTypes(
        ProjectGraph graph,
        SourceIndex index,
        ImpactPropagation propagation,
        Dictionary<string, string> fallbacks)
    {
        foreach (var (typeName, projectPath) in propagation.SeededTypes)
        {
            if (graph[projectPath].IsTestProject || fallbacks.ContainsKey(projectPath))
            {
                continue;
            }

            if (index.IsReferencedOutside(DeclarationWalker.SimpleTypeName(typeName), typeName))
            {
                continue;
            }

            var type = index.FindType(typeName);
            if (type is null)
            {
                AddFallback(fallbacks, projectPath, $"{typeName} was removed and nothing names it, so it may have been created through reflection");
                continue;
            }

            if (!type.MayBeFoundByReflection)
            {
                LogDeadType(typeName);
                continue;
            }

            AddFallback(fallbacks, projectPath, $"{typeName} is affected and nothing names it, so it may be created through reflection");
        }
    }

    /// <summary>Everything <see cref="Decide"/> combines.</summary>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="AffectedTests">Test projects affected at project level.</param>
    /// <param name="Index">Indexed source code.</param>
    /// <param name="ByName">Spread used for test projects without a coverage map.</param>
    /// <param name="WithCoverage">Spread used for test projects with a coverage map.</param>
    /// <param name="Coverage">Classes selected from coverage maps.</param>
    private sealed record Decision(
        ProjectGraph Graph,
        List<AffectedProject> AffectedTests,
        SourceIndex Index,
        TraceResult ByName,
        TraceResult WithCoverage,
        CoverageSelection Coverage)
    {
        /// <summary>Test projects that run completely because their map's drift cannot be bridged, with the reason.</summary>
        public Dictionary<string, string> DriftRunAll { get; init; } = [];
    }

    /// <summary>Turns fallbacks, name propagation and coverage into one decision per test project.</summary>
    /// <param name="decision">Inputs of the decision.</param>
    private List<TestProjectSelection> Decide(Decision decision)
    {
        var (graph, affectedTests, index, byName, withCoverage, coverage) = decision;
        var runAllByName = RunAll(graph, byName.Fallbacks);
        var runAllWithCoverage = RunAll(graph, withCoverage.Fallbacks);
        var classesByProject = new Dictionary<string, List<string>>(Paths.Comparer);
        var candidatesByProject = new Dictionary<string, int>(Paths.Comparer);
        foreach (var type in index.Types)
        {
            // A class the coverage map recorded ran tests, whatever attributes the index recognised.
            if (!type.InTestProject || !(type.CanContainTests || coverage.Recorded(type)))
            {
                continue;
            }

            candidatesByProject[type.ProjectPath] = candidatesByProject.GetValueOrDefault(type.ProjectPath) + 1;
            var trace = coverage.HasMap(type.ProjectPath) ? withCoverage : byName;
            if (!trace.Propagation.AffectedTypes.Contains(type.TypeName) && !coverage.Selects(type) && !coverage.FailedWhileRecording(type))
            {
                continue;
            }

            if (!classesByProject.TryGetValue(type.ProjectPath, out var classes))
            {
                classes = [];
                classesByProject[type.ProjectPath] = classes;
            }

            classes.Add(type.RuntimeTypeName);
        }

        var result = new List<TestProjectSelection>(affectedTests.Count);
        foreach (var test in affectedTests)
        {
            var project = test.Project;
            var runAll = coverage.HasMap(project.Path) ? runAllWithCoverage : runAllByName;
            if (decision.DriftRunAll.TryGetValue(project.Path, out var reason) || runAll.TryGetValue(project.Path, out reason))
            {
                result.Add(Log(new TestProjectSelection(project, TestRunMode.All, [], reason)));
                continue;
            }

            if (!classesByProject.TryGetValue(project.Path, out var classes))
            {
                var skipReason = coverage.HasMap(project.Path)
                    ? "no test class runs or references the changed code"
                    : "no test class references the changed code";
                result.Add(Log(new TestProjectSelection(project, TestRunMode.Skipped, [], skipReason)));
                continue;
            }

            if (classes.Count == candidatesByProject[project.Path])
            {
                result.Add(Log(new TestProjectSelection(project, TestRunMode.All, [], "every test class references the changed code")));
                continue;
            }

            classes.Sort(StringComparer.Ordinal);
            var source = coverage.HasMap(project.Path) ? "run or reference" : "reference";
            result.Add(Log(new TestProjectSelection(
                project,
                TestRunMode.Filtered,
                classes,
                $"{classes.Count} of {candidatesByProject[project.Path]} test class(es) {source} the changed code")));
        }

        return result;
    }

    /// <summary>Every project downstream of a fallback, with the first reason that reaches it.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="fallbacks">Projects that cannot be narrowed, with reasons.</param>
    private static Dictionary<string, string> RunAll(ProjectGraph graph, Dictionary<string, string> fallbacks)
    {
        var runAll = new Dictionary<string, string>(Paths.Comparer);
        foreach (var (project, reason) in fallbacks)
        {
            var projectName = Path.GetFileName(project);
            foreach (var dependent in graph.WithDependents([project]))
            {
                runAll.TryAdd(dependent, dependent == project ? reason : $"depends on {projectName}: {reason}");
            }
        }

        return runAll;
    }

    /// <summary>Records the first reason a project needs to run completely.</summary>
    /// <param name="fallbacks">Fallbacks so far.</param>
    /// <param name="project">Normalized .csproj path.</param>
    /// <param name="reason">Why the project cannot be narrowed.</param>
    private void AddFallback(Dictionary<string, string> fallbacks, string project, string reason)
    {
        if (fallbacks.TryAdd(project, reason))
        {
            LogFallback(project, reason);
        }
    }

    /// <summary>Logs a decision and passes it through.</summary>
    /// <param name="selection">The decision.</param>
    private TestProjectSelection Log(TestProjectSelection selection)
    {
        LogDecision(selection.Project.Path, selection.Mode, selection.Reason);
        return selection;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Not using the coverage map of {Project} from {MapCommit}: since then, {Reason}")]
    private partial void LogDriftedMap(string project, string mapCommit, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName} changed since {MapCommit} and can reach the change; bridged")]
    private partial void LogBridged(string typeName, string memberName, string mapCommit);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{TypeName} is named by nothing and has no base list or attributes; treated as unused")]
    private partial void LogDeadType(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: test classes taken from coverage")]
    private partial void LogResolvedByCoverage(string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project} cannot be narrowed below project level: {Reason}")]
    private partial void LogFallback(string project, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project}: {Mode} ({Reason})")]
    private partial void LogDecision(string project, TestRunMode mode, string reason);
}
