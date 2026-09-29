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

        var fallbacks = FindProjectLevelChanges(request);
        var affectedProjects = new HashSet<string>(Paths.Comparer);
        foreach (var affected in request.Impact.Affected)
        {
            affectedProjects.Add(affected.Project.Path);
        }

        var index = new SourceIndexBuilder(logger).Build(request.Graph, request.RepoRoot, affectedProjects);
        var propagation = new ImpactPropagation(index, logger);
        var root = Paths.Normalize(request.RepoRoot);

        foreach (var change in request.Impact.CodeChanges)
        {
            var owner = request.Graph.FindOwner(Paths.Normalize(Path.Combine(root, change.Path)));
            if (owner is null || fallbacks.ContainsKey(owner.Path))
            {
                continue;
            }

            foreach (var symbol in change.Symbols)
            {
                propagation.Seed(symbol, owner.Path);
            }
        }

        propagation.Run();
        AddUnreferencedTypes(request.Graph, index, propagation, fallbacks);

        return Decide(request.Graph, affectedTests, index, propagation, fallbacks);
    }

    /// <summary>
    /// Projects whose change cannot be described by symbols, with the reason: build configuration,
    /// non-C# files, C# files without a detected change, and global usings or assembly attributes.
    /// </summary>
    /// <param name="request">Changes and project-level impact.</param>
    private Dictionary<string, string> FindProjectLevelChanges(TestSelectionRequest request)
    {
        var fallbacks = new Dictionary<string, string>(Paths.Comparer);
        foreach (var affected in request.Impact.Affected)
        {
            if (affected.Reason == ImpactReason.BuildConfiguration)
            {
                AddFallback(fallbacks, affected.Project.Path, "build configuration changed");
            }
        }

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

            var owner = request.Graph.FindOwner(Paths.Normalize(Path.Combine(root, file)));
            if (owner is null)
            {
                continue;
            }

            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                AddFallback(fallbacks, owner.Path, $"{file} is not C# code");
                continue;
            }

            if (!codeChanges.TryGetValue(file, out var change))
            {
                AddFallback(fallbacks, owner.Path, $"{file} could not be compared");
                continue;
            }

            if (change.AffectsWholeProject)
            {
                AddFallback(fallbacks, owner.Path, $"{file} changes a global using or an assembly attribute");
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

    /// <summary>Turns fallbacks and affected types into one decision per test project.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="affectedTests">Test projects affected at project level.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="propagation">Finished propagation.</param>
    /// <param name="fallbacks">Projects whose whole downstream must run, with reasons.</param>
    private List<TestProjectSelection> Decide(
        ProjectGraph graph,
        List<AffectedProject> affectedTests,
        SourceIndex index,
        ImpactPropagation propagation,
        Dictionary<string, string> fallbacks)
    {
        // Each test project downstream of a fallback runs completely; remember the first reason.
        var runAll = new Dictionary<string, string>(Paths.Comparer);
        foreach (var (project, reason) in fallbacks)
        {
            foreach (var dependent in graph.WithDependents([project]))
            {
                var projectName = Path.GetFileName(project);
                runAll.TryAdd(dependent, dependent == project ? reason : $"depends on {projectName}: {reason}");
            }
        }

        var classesByProject = new Dictionary<string, List<string>>(Paths.Comparer);
        var candidatesByProject = new Dictionary<string, int>(Paths.Comparer);
        foreach (var type in index.Types)
        {
            if (!type.InTestProject || !type.CanContainTests)
            {
                continue;
            }

            candidatesByProject[type.ProjectPath] = candidatesByProject.GetValueOrDefault(type.ProjectPath) + 1;
            if (!propagation.AffectedTypes.Contains(type.TypeName))
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
            if (runAll.TryGetValue(project.Path, out var reason))
            {
                result.Add(Log(new TestProjectSelection(project, TestRunMode.All, [], reason)));
                continue;
            }

            if (!classesByProject.TryGetValue(project.Path, out var classes))
            {
                result.Add(Log(new TestProjectSelection(project, TestRunMode.Skipped, [], "no test class references the changed code")));
                continue;
            }

            if (classes.Count == candidatesByProject[project.Path])
            {
                result.Add(Log(new TestProjectSelection(project, TestRunMode.All, [], "every test class references the changed code")));
                continue;
            }

            classes.Sort(StringComparer.Ordinal);
            result.Add(Log(new TestProjectSelection(
                project,
                TestRunMode.Filtered,
                classes,
                $"{classes.Count} of {candidatesByProject[project.Path]} test class(es) reference the changed code")));
        }

        return result;
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project} cannot be narrowed below project level: {Reason}")]
    private partial void LogFallback(string project, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project}: {Mode} ({Reason})")]
    private partial void LogDecision(string project, TestRunMode mode, string reason);
}
