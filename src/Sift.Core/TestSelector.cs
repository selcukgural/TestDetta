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

        var projectLevel = FindProjectLevelChanges(request);
        var affectedProjects = new HashSet<string>(Paths.Comparer);
        foreach (var affected in request.Impact.Affected)
        {
            affectedProjects.Add(affected.Project.Path);
        }

        var index = new SourceIndexBuilder(logger).Build(request.Graph, request.RepoRoot, affectedProjects);
        var coverage = new CoverageSelection(request, affectedTests, index);

        // Test projects without a coverage map are decided by name alone; those with one use a second
        // spread that stops wherever the maps know which tests run the reached code.
        var byName = Trace(request, index, projectLevel, coverage: null);
        var withCoverage = coverage.HasAnyMap ? Trace(request, index, projectLevel, coverage) : byName;

        var decisions = Decide(new Decision(request.Graph, affectedTests, index, byName, withCoverage, coverage));
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
    /// Seeds the detected changes and spreads them by name. With <paramref name="coverage"/>, a change or
    /// reached member that the maps can answer is taken from coverage and not spread further.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="projectLevel">Project-level fallbacks from non-C# and build changes.</param>
    /// <param name="coverage">Coverage maps to stop at, or null for a pure name spread.</param>
    private TraceResult Trace(
        TestSelectionRequest request,
        SourceIndex index,
        Dictionary<string, string> projectLevel,
        CoverageSelection? coverage)
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

        propagation.Run();
        AddUnreferencedTypes(request.Graph, index, propagation, fallbacks);
        return new TraceResult(propagation, fallbacks);
    }

    /// <summary>
    /// Projects whose change cannot be described by symbols, with the reason: build configuration,
    /// non-C# files, C# files without a detected change, and global usings or assembly attributes.
    /// </summary>
    /// <param name="request">Changes and project-level impact.</param>
    private Dictionary<string, string> FindProjectLevelChanges(TestSelectionRequest request)
    {
        var fallbacks = new Dictionary<string, string>(Paths.Comparer);
        var codeChanges = new Dictionary<string, CodeChange>(StringComparer.Ordinal);
        foreach (var change in request.Impact.CodeChanges)
        {
            codeChanges[change.Path] = change;
        }

        var cosmetic = new HashSet<string>(request.Impact.CosmeticFiles, StringComparer.Ordinal);
        var root = Paths.Normalize(request.RepoRoot);

        foreach (var file in request.ChangedFiles)
        {
            if (cosmetic.Contains(file))
            {
                continue;
            }

            foreach (var owner in request.Graph.ResolveFile(Paths.Normalize(Path.Combine(root, file))))
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
        foreach (var affected in request.Impact.Affected)
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
        CoverageSelection Coverage);

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
            if (!type.InTestProject || !type.CanContainTests)
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
            if (runAll.TryGetValue(project.Path, out var reason))
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
