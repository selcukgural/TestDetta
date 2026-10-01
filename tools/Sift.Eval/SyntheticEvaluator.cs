using Microsoft.Extensions.Logging;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Eval;

/// <summary>
/// Evaluates Sift on changes it makes itself instead of the repository's history: coverage is
/// recorded once at a commit, then for each sampled member the source is edited (so Sift sees the
/// member change and selects tests) and the compiled member is made to throw (so the tests show
/// which classes really depend on it). A class that fails but was not selected is a missed test.
/// History rarely changes enough library code for a measurement; this gives hundreds of data points
/// per recording.
/// </summary>
/// <param name="workspace">Where to work.</param>
/// <param name="settings">Evaluation settings.</param>
internal sealed partial class SyntheticEvaluator(EvalWorkspace workspace, RunSettings settings)
{
    private readonly ILogger _logger = workspace.LoggerFactory.CreateLogger<SyntheticEvaluator>();

    /// <summary>Recording, building and selection checks shared with the history mode.</summary>
    private readonly EvalSteps _steps = new(workspace, settings);

    /// <summary>Evaluates sampled changes at <paramref name="sha"/>; a build failure is reported, not thrown.</summary>
    /// <param name="sha">Commit to change.</param>
    /// <param name="request">How many members to change.</param>
    /// <param name="progress">Called after every mutant with the results so far, so a long run can be read while it runs.</param>
    public SyntheticRun Evaluate(string sha, SampleRequest request, Action<SyntheticRun> progress)
    {
        var mutants = new List<SyntheticMutant>();
        try
        {
            var maps = _steps.RecordAt(sha);
            workspace.Git.Checkout(sha);
            workspace.Executor.Setup();
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>()).Load(workspace.RepoRoot);
            var testProjects = _steps.Build(_steps.TestProjects(graph));
            if (testProjects.Count == 0)
            {
                throw new InvalidOperationException("No test project could be built.");
            }

            workspace.Executor.Outputs = _steps.OutputDirectories(testProjects);
            var baseline = workspace.Executor.RunAll(testProjects, "baseline");
            var testClasses = maps.Values.Sum(static m => m.Classes.Count);

            var targets = TargetSampler.Sample(graph, workspace.RepoRoot, CoveredMembers(maps), request);
            var coveredTargets = targets.Count(static t => t.Covered);
            LogSampled(targets.Count, coveredTargets);

            var assemblies = EvalSteps.RepositoryAssemblies(
                new ProjectAssemblies(_logger).FileNames(graph, workspace.RepoRoot), workspace.Executor.Outputs.Values);

            foreach (var target in targets)
            {
                mutants.Add(Run(sha, target, new MutantContext(testProjects, maps, assemblies, baseline), mutants.Count + 1));
                progress(new SyntheticRun(sha, testClasses, baseline.Seconds, mutants, null));
            }

            return new SyntheticRun(sha, testClasses, baseline.Seconds, mutants, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            LogFailed(sha, ex);
            return new SyntheticRun(sha, 0, 0, mutants, ex.Message);
        }
    }

    /// <summary>What every mutant of one commit shares.</summary>
    /// <param name="TestProjects">Built test projects.</param>
    /// <param name="Maps">Coverage maps at the commit, by normalized test project path.</param>
    /// <param name="Assemblies">Repository assemblies in the test outputs, where mutants are injected.</param>
    /// <param name="Baseline">Full run without mutants, whose failures are not a mutant's.</param>
    private sealed record MutantContext(
        List<ProjectNode> TestProjects,
        Dictionary<string, CoverageMap> Maps,
        List<string> Assemblies,
        TestRunOutcome Baseline);

    /// <summary>Selects for one synthetic change and runs the tests against its mutant.</summary>
    /// <param name="sha">Commit the maps were recorded at.</param>
    /// <param name="target">Member to change.</param>
    /// <param name="context">What the mutants of the commit share.</param>
    /// <param name="index">1-based number of the mutant, for log files.</param>
    private SyntheticMutant Run(string sha, SyntheticTarget target, MutantContext context, int index)
    {
        var analysis = Analyze(sha, target);
        var detected = analysis.Impact.CodeChanges.Any(c =>
            c.AffectsWholeProject || c.Symbols.Any(s => s.TypeName == target.Member.TypeName));
        var share = SelectedShare(analysis, context.Maps);

        using var mutation = MutationInjector.Inject(context.Assemblies, target.Member, Path.Combine(workspace.MapCache, "..", "mutant-backup"), target.Kind);
        if (mutation.MutatedMethods == 0)
        {
            LogNotFound(target.Member.TypeName, target.Member.MemberName);
            return new SyntheticMutant(target, detected, 0, [], [], share, Inconclusive: false);
        }

        // Only a test project whose output holds a mutated copy can fail because of the mutant.
        var mutatedDirectories = mutation.Backups.Keys.Select(static f => Path.GetDirectoryName(f)!).ToHashSet(StringComparer.Ordinal);
        var reached = context.TestProjects
            .Where(p => workspace.Executor.Outputs.TryGetValue(p.Path, out var output) && mutatedDirectories.Contains(output))
            .ToList();
        var run = workspace.Executor.RunAll(reached, $"synthetic-{index}");
        if (run.Aborted > 0)
        {
            return RunOutsideSelection(target, new MutantRun(analysis, reached, index, detected, share), mutation.MutatedMethods);
        }

        var killed = new List<string>();
        var missed = new List<string>();
        foreach (var (project, failed) in run.FailedByProject)
        {
            var before = context.Baseline.FailedByProject.GetValueOrDefault(project) ?? new HashSet<string>();
            foreach (var testClass in failed.Where(c => !before.Contains(c)).Order(StringComparer.Ordinal))
            {
                var name = $"{Path.GetFileNameWithoutExtension(project)}: {testClass}";
                killed.Add(name);
                if (!EvalSteps.IsSelected(analysis, project, testClass))
                {
                    missed.Add(name);
                }
            }
        }

        if (missed.Count > 0)
        {
            missed = Confirm(missed, reached, index, killed);
        }

        LogMutant(index, target.Member.TypeName, target.Member.MemberName, killed.Count, missed.Count, share);
        return new SyntheticMutant(target, detected, mutation.MutatedMethods, killed, missed, share, Inconclusive: false);
    }

    /// <summary>One mutant's state while it is evaluated.</summary>
    /// <param name="Analysis">Sift's analysis of the synthetic change.</param>
    /// <param name="Reached">Test projects the mutant can reach.</param>
    /// <param name="Index">Number of the mutant, for log files.</param>
    /// <param name="Detected">Whether Sift saw the change.</param>
    /// <param name="Share">Share of recorded classes Sift selected.</param>
    private sealed record MutantRun(AnalysisResult Analysis, List<ProjectNode> Reached, int Index, bool Detected, double Share);

    /// <summary>
    /// After a full run crashed or hung, runs only what Sift left out, with the mutant in place. A miss
    /// can only be there: if it passes, the failure was in the selection and the mutant counts as
    /// caught; classes that fail are confirmed alone like any miss; a second crash or hang leaves the
    /// mutant inconclusive. A project Sift runs completely has nothing left out.
    /// </summary>
    /// <param name="target">The mutated member.</param>
    /// <param name="state">The mutant's state.</param>
    /// <param name="mutatedMethods">Number of mutated compiled methods.</param>
    private SyntheticMutant RunOutsideSelection(SyntheticTarget target, MutantRun state, int mutatedMethods)
    {
        var mode = DotnetTestModeDetector.Detect(workspace.RepoRoot);
        var invocations = new List<TestInvocation>();
        foreach (var project in state.Reached)
        {
            var decision = state.Analysis.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, project.Path, StringComparison.Ordinal));
            if (decision?.Mode == TestRunMode.All)
            {
                continue;
            }

            var relative = Path.GetRelativePath(workspace.RepoRoot, project.Path).Replace('\\', '/');
            List<string> arguments = mode == DotnetTestMode.TestingPlatform ? ["test", "--project", relative] : ["test", relative];
            if (decision is { Mode: TestRunMode.Filtered, TestClasses.Count: > 0 })
            {
                var xunitV3 = mode == DotnetTestMode.TestingPlatform && TestFilter.IsXunitV3(project);
                arguments.AddRange(TestFilter.Arguments(decision.TestClasses, exclude: true, xunitV3));
            }

            invocations.Add(new TestInvocation(new TestProjectSelection(project, TestRunMode.Filtered, [], "outside the selection"), arguments));
        }

        var outside = workspace.Executor.RunSelected(invocations, $"synthetic-{state.Index}-outside");
        if (outside.Aborted > 0)
        {
            return new SyntheticMutant(target, state.Detected, mutatedMethods, [], [], state.Share, Inconclusive: true);
        }

        var failed = outside.FailedByProject
            .SelectMany(p => p.Value.Select(c => $"{Path.GetFileNameWithoutExtension(p.Key)}: {c}"))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (failed.Count == 0)
        {
            LogCaughtByCrash(state.Index, target.Member.TypeName, target.Member.MemberName);
            return new SyntheticMutant(target, state.Detected, mutatedMethods, [], [], state.Share, Inconclusive: false) { CaughtByCrash = true };
        }

        var killed = new List<string>(failed);
        var missed = Confirm(failed, state.Reached, state.Index, killed);
        LogMutant(state.Index, target.Member.TypeName, target.Member.MemberName, killed.Count, missed.Count, state.Share);
        return new SyntheticMutant(target, state.Detected, mutatedMethods, killed, missed, state.Share, Inconclusive: false)
        {
            CaughtByCrash = killed.Count == 0,
        };
    }

    /// <summary>
    /// Runs the missed classes again, alone, with the mutant still in place, and keeps only those that
    /// fail again. A class can fail in the full run without depending on the change: a timing-sensitive
    /// test on a busy machine, or a class that runs after a failing one which left shared state behind
    /// (a global option it never restored). Sift selected the class that really fails in that case, so
    /// counting the others as misses would blame Sift for the suite's order dependence. A class that
    /// passes alone is dropped from the killed ones too.
    /// </summary>
    /// <param name="missed">Missed classes of the first run (<c>Project: Class</c>).</param>
    /// <param name="reached">Test projects the mutant can reach.</param>
    /// <param name="index">Number of the mutant, for log files.</param>
    /// <param name="killed">Killed classes of the first run; unconfirmed ones are removed in place.</param>
    private List<string> Confirm(List<string> missed, List<ProjectNode> reached, int index, List<string> killed)
    {
        var selections = new List<TestProjectSelection>();
        foreach (var project in reached)
        {
            var prefix = Path.GetFileNameWithoutExtension(project.Path) + ": ";
            var classes = missed.Where(m => m.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m[prefix.Length..]).ToList();
            if (classes.Count > 0)
            {
                selections.Add(new TestProjectSelection(project, TestRunMode.Filtered, classes, "confirming a miss"));
            }
        }

        var invocations = TestInvocationBuilder.Build(selections, workspace.RepoRoot, [], DotnetTestModeDetector.Detect(workspace.RepoRoot));
        var again = workspace.Executor.RunSelected(invocations, $"synthetic-{index}-confirm");
        var failedAgain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (project, failed) in again.FailedByProject)
        {
            foreach (var testClass in failed)
            {
                failedAgain.Add($"{Path.GetFileNameWithoutExtension(project)}: {testClass}");
            }
        }

        var confirmed = missed.FindAll(failedAgain.Contains);
        foreach (var unconfirmed in missed.Where(m => !failedAgain.Contains(m)))
        {
            LogUnconfirmed(index, unconfirmed);
            killed.Remove(unconfirmed);
        }

        return confirmed;
    }

    /// <summary>Applies the target's source edits, lets Sift select against the commit's maps, and restores the files.</summary>
    /// <param name="sha">Commit the maps were recorded at and the base of the diff.</param>
    /// <param name="target">Member to change.</param>
    private AnalysisResult Analyze(string sha, SyntheticTarget target)
    {
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var group in target.Edits.GroupBy(static e => e.Path, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(group.Key);
                originals[group.Key] = text;

                // From the end, so earlier offsets stay valid.
                var edited = new System.Text.StringBuilder(text);
                foreach (var edit in group.OrderByDescending(static e => e.Position))
                {
                    edited.Remove(edit.Position, edit.Replaces).Insert(edit.Position, edit.Text);
                }

                File.WriteAllText(group.Key, edited.ToString());
            }

            _steps.PlaceMaps(sha);
            return new AnalysisPipeline(workspace.LoggerFactory).Run(workspace.RepoRoot, sha);
        }
        finally
        {
            _steps.ClearMaps();
            foreach (var (path, text) in originals)
            {
                File.WriteAllText(path, text);
            }
        }
    }

    /// <summary>
    /// Share of recorded test classes that Sift's selection runs: 1 when every test project runs
    /// completely, 0 when nothing runs. Projects without a map are not counted.
    /// </summary>
    /// <param name="analysis">Sift's analysis.</param>
    /// <param name="maps">Maps by normalized test project path.</param>
    private static double SelectedShare(AnalysisResult analysis, Dictionary<string, CoverageMap> maps)
    {
        var total = 0;
        var selected = 0;
        foreach (var (project, map) in maps)
        {
            total += map.Classes.Count;
            var decision = analysis.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, project, StringComparison.Ordinal));
            selected += decision?.Mode switch
            {
                TestRunMode.All => map.Classes.Count,
                TestRunMode.Filtered => decision.TestClasses.Count(map.Classes.ContainsKey),
                _ => 0,
            };
        }

        return total == 0 ? 0 : (double)selected / total;
    }

    /// <summary>Every member that ran in any test class, fixture or start-up of any map.</summary>
    /// <param name="maps">Maps of the commit.</param>
    private static HashSet<CoveredMethod> CoveredMembers(Dictionary<string, CoverageMap> maps)
    {
        var covered = new HashSet<CoveredMethod>();
        foreach (var map in maps.Values)
        {
            foreach (var ids in map.Classes.Values.Concat(map.Fixtures.Values).Append(map.Ambient))
            {
                foreach (var id in ids)
                {
                    covered.Add(map.Methods[id]);
                }
            }
        }

        return covered;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Mutant {Index} {TypeName}.{MemberName}: the full run crashed or hung, everything Sift left out passed; caught")]
    private partial void LogCaughtByCrash(int index, string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sampled {Count} member(s), {Covered} of them covered")]
    private partial void LogSampled(int count, int covered);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Mutant {Index} {TypeName}.{MemberName}: {Killed} class(es) failed, {Missed} not selected, {Share:P0} of classes selected")]
    private partial void LogMutant(int index, string typeName, string memberName, int killed, int missed, double share);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mutant {Index}: {TestClass} failed in the full run but passed alone; not counted")]
    private partial void LogUnconfirmed(int index, string testClass);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName}.{MemberName} is in no test output; not run")]
    private partial void LogNotFound(string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Sha} could not be evaluated synthetically")]
    private partial void LogFailed(string sha, Exception exception);
}
