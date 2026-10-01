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
            var unreported = testProjects.Where(p => baseline.Unreported.Contains(p.Path) || baseline.Crashed.Contains(p.Path))
                .Select(p => Path.GetRelativePath(workspace.RepoRoot, p.Path).Replace('\\', '/')).ToList();
            testProjects = _steps.Reporting(testProjects, baseline);
            if (testProjects.Count == 0)
            {
                throw new InvalidOperationException("No test project wrote a test report.");
            }

            var testClasses = maps.Values.Sum(static m => m.Classes.Count);

            var targets = TargetSampler.Sample(graph, workspace.RepoRoot, CoveredMembers(maps), request);
            var coveredTargets = targets.Count(static t => t.Covered);
            LogSampled(targets.Count, coveredTargets);

            var assemblies = EvalSteps.RepositoryAssemblies(
                new ProjectAssemblies(_logger).FileNames(graph, workspace.RepoRoot), workspace.Executor.Outputs.Values);

            foreach (var target in targets)
            {
                mutants.Add(Run(sha, target, new MutantContext(testProjects, maps, assemblies, baseline), mutants.Count + 1));
                progress(new SyntheticRun(sha, testClasses, baseline.Seconds, mutants, null) { Unreported = unreported });
            }

            return new SyntheticRun(sha, testClasses, baseline.Seconds, mutants, null) { Unreported = unreported };
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
        // A project that wrote no report this time died with the mutant in place, like a crash.
        var run = workspace.Executor.RunAll(reached, $"synthetic-{index}");
        if (!run.Complete)
        {
            return RunOutsideSelection(target, new MutantRun(analysis, reached, index, detected, share), mutation);
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
            missed = Confirm(missed, new MutantScope(reached, index, mutation), killed);
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
    /// <param name="mutation">The applied mutant.</param>
    private SyntheticMutant RunOutsideSelection(SyntheticTarget target, MutantRun state, Mutation mutation)
    {
        var mutatedMethods = mutation.MutatedMethods;
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

        // No report is no evidence that what Sift left out passed.
        var outside = workspace.Executor.RunSelected(invocations, $"synthetic-{state.Index}-outside");
        if (!outside.Complete)
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
        var missed = Confirm(failed, new MutantScope(state.Reached, state.Index, mutation), killed);
        LogMutant(state.Index, target.Member.TypeName, target.Member.MemberName, killed.Count, missed.Count, state.Share);
        return new SyntheticMutant(target, state.Detected, mutatedMethods, killed, missed, state.Share, Inconclusive: false)
        {
            CaughtByCrash = killed.Count == 0,
        };
    }

    /// <summary>The mutant a miss is confirmed for.</summary>
    /// <param name="Reached">Test projects the mutant can reach.</param>
    /// <param name="Index">Number of the mutant, for log files.</param>
    /// <param name="Mutation">The applied mutant, lifted for control runs.</param>
    private sealed record MutantScope(List<ProjectNode> Reached, int Index, Mutation Mutation);

    /// <summary>
    /// Whether a test class fails when it runs alone without any mutant, by <c>Project: Class</c>.
    /// Every mutant of a commit shares the same build, so the answer holds for all of them.
    /// </summary>
    private readonly Dictionary<string, bool> _failsAlone = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs the missed classes again, alone, with the mutant still in place, and keeps only those that
    /// fail again. A class can fail in the full run without depending on the change: a timing-sensitive
    /// test on a busy machine, or a class that runs after a failing one which left shared state behind
    /// (a global option it never restored). Sift selected the class that really fails in that case, so
    /// counting the others as misses would blame Sift for the suite's order dependence. A class that
    /// passes alone is dropped from the killed ones too. A class that fails alone also fails alone
    /// without the mutant (NLog: tests that pass only after others set things up), so the classes that
    /// fail again are run once more with the mutant lifted, and those failing there too are dropped.
    /// </summary>
    /// <param name="missed">Missed classes of the first run (<c>Project: Class</c>).</param>
    /// <param name="scope">The mutant and the projects it reaches.</param>
    /// <param name="killed">Killed classes of the first run; unconfirmed ones are removed in place.</param>
    private List<string> Confirm(List<string> missed, MutantScope scope, List<string> killed)
    {
        var again = RunAlone(missed, scope.Reached, $"synthetic-{scope.Index}-confirm");
        var confirmed = missed.FindAll(again.Failed.Contains);
        foreach (var unconfirmed in missed.Where(m => !again.Failed.Contains(m)))
        {
            LogUnconfirmed(scope.Index, unconfirmed);
            killed.Remove(unconfirmed);
        }

        var unknown = confirmed.FindAll(c => !_failsAlone.ContainsKey(c));
        if (unknown.Count > 0)
        {
            RunOutcome control;
            using (scope.Mutation.Lift())
            {
                control = RunAlone(unknown, scope.Reached, $"synthetic-{scope.Index}-control");
            }

            // A control run that crashed proves nothing; the miss stands and the class is asked again next time.
            if (control.Complete)
            {
                foreach (var testClass in unknown)
                {
                    _failsAlone[testClass] = control.Failed.Contains(testClass);
                }
            }
        }

        foreach (var broken in confirmed.Where(c => _failsAlone.GetValueOrDefault(c)))
        {
            LogFailsWithoutMutant(scope.Index, broken);
            killed.Remove(broken);
        }

        return confirmed.FindAll(c => !_failsAlone.GetValueOrDefault(c));
    }

    /// <summary>Failed classes of a run of some classes alone, and whether the run was complete.</summary>
    /// <param name="Failed">Failed classes as <c>Project: Class</c>.</param>
    /// <param name="Complete">Every run wrote a report and no host crashed.</param>
    private sealed record RunOutcome(HashSet<string> Failed, bool Complete);

    /// <summary>Runs <paramref name="classes"/> (<c>Project: Class</c>) alone, each project filtered to its own.</summary>
    /// <param name="classes">Classes to run.</param>
    /// <param name="reached">Test projects they may belong to.</param>
    /// <param name="label">Name for log files.</param>
    private RunOutcome RunAlone(List<string> classes, List<ProjectNode> reached, string label)
    {
        var selections = new List<TestProjectSelection>();
        foreach (var project in reached)
        {
            var prefix = Path.GetFileNameWithoutExtension(project.Path) + ": ";
            var own = classes.Where(m => m.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m[prefix.Length..]).ToList();
            if (own.Count > 0)
            {
                selections.Add(new TestProjectSelection(project, TestRunMode.Filtered, own, "confirming a miss"));
            }
        }

        var invocations = TestInvocationBuilder.Build(selections, workspace.RepoRoot, [], DotnetTestModeDetector.Detect(workspace.RepoRoot));
        var run = workspace.Executor.RunSelected(invocations, label);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (project, classesFailed) in run.FailedByProject)
        {
            foreach (var testClass in classesFailed)
            {
                failed.Add($"{Path.GetFileNameWithoutExtension(project)}: {testClass}");
            }
        }

        return new RunOutcome(failed, run.Complete);
    }

    /// <summary>Applies the target's source edits, lets Sift select against the commit's maps, and restores the files.</summary>
    /// <param name="sha">Commit the maps were recorded at and the base of the diff.</param>
    /// <param name="target">Member to change.</param>
    private AnalysisResult Analyze(string sha, SyntheticTarget target)
    {
        // Bytes, not text: a rewrite through File.WriteAllText drops a UTF-8 byte order mark, and the
        // clone would keep a change in that file for every later mutant.
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var group in target.Edits.GroupBy(static e => e.Path, StringComparer.Ordinal))
            {
                originals[group.Key] = File.ReadAllBytes(group.Key);
                var text = File.ReadAllText(group.Key);

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
            foreach (var (path, bytes) in originals)
            {
                File.WriteAllBytes(path, bytes);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mutant {Index}: {TestClass} fails alone without the mutant too; not counted")]
    private partial void LogFailsWithoutMutant(int index, string testClass);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mutant {Index}: {TestClass} failed in the full run but passed alone; not counted")]
    private partial void LogUnconfirmed(int index, string testClass);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName}.{MemberName} is in no test output; not run")]
    private partial void LogNotFound(string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Sha} could not be evaluated synthetically")]
    private partial void LogFailed(string sha, Exception exception);
}
