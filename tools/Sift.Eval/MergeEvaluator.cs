using Microsoft.Extensions.Logging;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Eval;

/// <summary>Where an evaluation works.</summary>
/// <param name="Git">Git of the clone.</param>
/// <param name="RepoRoot">Clone directory.</param>
/// <param name="MapCache">Directory holding recorded coverage maps, one subdirectory per commit.</param>
/// <param name="Executor">Runs tests in the clone.</param>
/// <param name="LoggerFactory">Creates loggers for Sift's own classes.</param>
internal sealed record EvalWorkspace(EvalGit Git, string RepoRoot, string MapCache, TestExecutor Executor, ILoggerFactory LoggerFactory)
{
    /// <summary>Evaluates the projects with MSBuild for every graph the evaluation and Sift load; its cache is shared.</summary>
    public IProjectEvaluator Evaluator { get; } = new Sift.Coverage.MsBuildProjectEvaluator(LoggerFactory);
}

/// <summary>
/// Evaluates one merge: records coverage at its parent, lets Sift select, runs selection and full
/// suite, and checks the selection with coverage recorded after the merge and with mutants.
/// </summary>
/// <param name="workspace">Where to work.</param>
/// <param name="settings">Evaluation settings.</param>
internal sealed partial class MergeEvaluator(EvalWorkspace workspace, RunSettings settings)
{
    private readonly ILogger _logger = workspace.LoggerFactory.CreateLogger<MergeEvaluator>();

    /// <summary>Recording, building and selection checks shared with the synthetic mode.</summary>
    private readonly EvalSteps _steps = new(workspace, settings);

    /// <summary>Evaluates <paramref name="commit"/>; a build or run failure is reported, not thrown.</summary>
    /// <param name="commit">Merge to evaluate.</param>
    public MergeResult Evaluate(EvalCommit commit)
    {
        var info = new MergeInfo(commit.Sha, commit.Parent, commit.Subject, workspace.Git.ChangedFileCount(commit.Parent, commit.Sha));
        try
        {
            _steps.RecordAt(commit.Parent);

            workspace.Git.Checkout(commit.Sha);
            workspace.Executor.Setup();
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>(), workspace.Evaluator).Load(workspace.RepoRoot);
            var testProjects = _steps.Build(_steps.TestProjects(graph));
            if (testProjects.Count == 0)
            {
                throw new InvalidOperationException("No test project could be built.");
            }

            workspace.Executor.Outputs = _steps.OutputDirectories(testProjects);

            _steps.PlaceMaps(commit.Parent);
            var analysis = new AnalysisPipeline(workspace.LoggerFactory, workspace.Evaluator).Run(workspace.RepoRoot, commit.Parent);
            _steps.ClearMaps();

            var inScope = new HashSet<string>(testProjects.Select(static p => p.Path), StringComparer.Ordinal);
            var invocations = TestInvocationBuilder.Build(
                [.. analysis.Tests.Where(t => inScope.Contains(t.Project.Path))], workspace.RepoRoot, []);

            var selected = workspace.Executor.RunSelected(invocations, "selected");
            var full = workspace.Executor.RunAll(testProjects, "full");
            var mutants = RunMutants(analysis, _steps.Reporting(testProjects, full), full);

            var afterMaps = _steps.RecordAt(commit.Sha);
            var coverageMisses = CoverageMisses(analysis, afterMaps);

            LogEvaluated(commit.Sha, full.Seconds, selected.Seconds);
            return new MergeResult(info, analysis.Plan, new RunComparison(full, selected), new SafetyCheck(coverageMisses, mutants), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            LogFailed(commit.Sha, ex);
            return new MergeResult(info, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Mutates up to <see cref="RunSettings.Mutants"/> changed members, one at a time, and runs the
    /// full suite against each. A class that fails only with the mutant must be in Sift's selection.
    /// Targets and assemblies are found independently of Sift's project mapping, so a file Sift
    /// cannot place (a linked source file, say) is still checked.
    /// </summary>
    /// <param name="analysis">Sift's analysis of the merge.</param>
    /// <param name="testProjects">Evaluated test projects.</param>
    /// <param name="baseline">Full run without mutants, whose failures are not the mutant's.</param>
    private List<MutantResult> RunMutants(AnalysisResult analysis, List<ProjectNode> testProjects, TestRunOutcome baseline)
    {
        var results = new List<MutantResult>();
        var index = 0;
        var assemblies = EvalSteps.RepositoryAssemblies(new ProjectAssemblies(_logger).FileNames(analysis.Graph, workspace.RepoRoot), workspace.Executor.Outputs.Values);
        foreach (var target in MutationTargets(analysis).Take(settings.Mutants))
        {
            index++;
            using var mutation = MutationInjector.Inject(assemblies, target, Path.Combine(workspace.MapCache, "..", "mutant-backup"));
            if (mutation.MutatedMethods == 0)
            {
                results.Add(new MutantResult(target, 0, [], []));
                continue;
            }

            var run = workspace.Executor.RunAll(testProjects, $"mutant-{index}");
            if (!run.Complete)
            {
                // A crash or a missing report hides which classes failed; a missing class would prove nothing either way.
                results.Add(new MutantResult(target, mutation.MutatedMethods, [], [], Inconclusive: true));
                continue;
            }

            var killed = new List<string>();
            var missed = new List<string>();
            foreach (var (project, failed) in run.FailedByProject)
            {
                var before = baseline.FailedByProject.GetValueOrDefault(project) ?? new HashSet<string>();
                foreach (var testClass in failed.Where(c => !before.Contains(c)).Order(StringComparer.Ordinal))
                {
                    killed.Add(testClass);
                    if (!EvalSteps.IsSelected(analysis, project, testClass))
                    {
                        missed.Add(testClass);
                    }
                }
            }

            LogMutant(target.TypeName, target.MemberName, killed.Count, missed.Count);
            results.Add(new MutantResult(target, mutation.MutatedMethods, killed, missed));
        }

        return results;
    }

    /// <summary>
    /// Modified members of every changed C# file outside test projects, including files that belong
    /// to no project directory.
    /// </summary>
    /// <param name="analysis">Sift's analysis of the merge.</param>
    private IEnumerable<CoveredMethod> MutationTargets(AnalysisResult analysis)
    {
        var seen = new HashSet<CoveredMethod>();
        foreach (var change in analysis.Impact.CodeChanges.OrderBy(static c => c.Path, StringComparer.Ordinal))
        {
            var owner = analysis.Graph.FindOwner(Path.GetFullPath(Path.Combine(workspace.RepoRoot, change.Path)).Replace('\\', '/'));
            if (owner is { IsTestProject: true })
            {
                continue;
            }

            foreach (var symbol in change.Symbols)
            {
                if (symbol is not { Kind: SymbolChangeKind.Modified, MemberName: { } member })
                {
                    continue;
                }

                var name = member.StartsWith(".ctor", StringComparison.Ordinal) ? ".ctor"
                    : DeclarationWalker.ReferenceNames(member) is [var single] ? single
                    : null;
                if (name is not null && seen.Add(new CoveredMethod(symbol.TypeName, name)))
                {
                    yield return new CoveredMethod(symbol.TypeName, name);
                }
            }
        }
    }

    /// <summary>
    /// Test classes that, per coverage recorded after the merge, ran a changed member (or any member
    /// of a changed type) but were not selected.
    /// </summary>
    /// <param name="analysis">Sift's analysis.</param>
    /// <param name="afterMaps">Maps recorded at the merge.</param>
    /// <remarks>
    /// Maps name types without their assembly, so a change counts for a test project only when the
    /// changed file's project is one it depends on; otherwise a tool's <c>Program</c> would match the
    /// application's. A file outside every project counts everywhere, so Sift's blind spots stay visible.
    /// </remarks>
    private List<string> CoverageMisses(AnalysisResult analysis, Dictionary<string, CoverageMap> afterMaps)
    {
        var changed = new List<(string Type, string? Member, ProjectNode? Owner)>();
        foreach (var change in analysis.Impact.CodeChanges)
        {
            var owner = analysis.Graph.FindOwner(Path.GetFullPath(Path.Combine(workspace.RepoRoot, change.Path)).Replace('\\', '/'));
            foreach (var symbol in change.Symbols)
            {
                var member = symbol.MemberName switch
                {
                    null => null,
                    var m when m.StartsWith(".ctor", StringComparison.Ordinal) => ".ctor",
                    var m => DeclarationWalker.ReferenceNames(m) is [var single] ? single : null,
                };

                if (symbol.MemberName is null || member is not null)
                {
                    changed.Add((symbol.TypeName, member, owner));
                }
            }
        }

        var misses = new List<string>();
        foreach (var (project, map) in afterMaps)
        {
            var relevant = changed.FindAll(c => c.Owner is null || analysis.Graph.WithDependents([c.Owner.Path]).Contains(project));
            foreach (var (testClass, ids) in map.Classes)
            {
                var ran = ids.Select(id => map.Methods[id]).ToList();
                var touches = relevant.Exists(c => c.Member is null
                    ? ran.Exists(m => m.TypeName == c.Type || m.TypeName.StartsWith(c.Type + ".", StringComparison.Ordinal))
                    : ran.Contains(new CoveredMethod(c.Type, c.Member)));

                if (touches && !EvalSteps.IsSelected(analysis, project, testClass))
                {
                    misses.Add($"{Path.GetFileNameWithoutExtension(project)}: {testClass}");
                }
            }
        }

        misses.Sort(StringComparer.Ordinal);
        return misses;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sha}: full {FullSeconds:0.0} s, selected {SelectedSeconds:0.0} s")]
    private partial void LogEvaluated(string sha, double fullSeconds, double selectedSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mutant {TypeName}.{MemberName}: {Killed} class(es) failed, {Missed} not selected")]
    private partial void LogMutant(string typeName, string memberName, int killed, int missed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Sha} could not be evaluated")]
    private partial void LogFailed(string sha, Exception exception);
}
