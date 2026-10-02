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

    /// <summary>Test outcomes kept from earlier evaluations of the commit; null when caching is off.</summary>
    private OutcomeCache? _cache;

    /// <summary>Evaluates sampled changes at <paramref name="sha"/>; a build failure is reported, not thrown.</summary>
    /// <param name="sha">Commit to change.</param>
    /// <param name="request">How many members to change.</param>
    /// <param name="progress">Called after every mutant with the results so far, so a long run can be read while it runs.</param>
    public SyntheticRun Evaluate(string sha, SampleRequest request, Action<SyntheticRun> progress)
    {
        var mutants = new List<SyntheticMutant>();
        try
        {
            _cache = OutcomeCache.Open(settings.OutcomeCache, sha);
            if (_cache is { Count: > 0 } cache)
            {
                LogCacheOpened(cache.Count);
            }

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

            // Selecting edits the clone's sources, so it happens one mutant at a time, and before any
            // binary is touched: a hang in Sift's analysis then leaves the clone as it was.
            var selections = targets.Select(t => Select(sha, t, maps)).ToList();

            using var schema = settings.UseSchema ? TrySchema(assemblies, targets, testProjects, baseline) : null;
            var context = new MutantContext(testProjects, maps, assemblies, baseline, schema);
            var parallelism = Parallelism(schema, testProjects, baseline);
            workspace.Executor.Concurrency = parallelism;

            var results = new SyntheticMutant?[targets.Count];
            var gate = new object();
            Parallel.For(0, targets.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, i =>
            {
                var result = Run(targets[i], context, i + 1, selections[i]);
                lock (gate)
                {
                    results[i] = result;
                    mutants.Clear();
                    mutants.AddRange(results.OfType<SyntheticMutant>());
                    progress(new SyntheticRun(sha, testClasses, baseline.Seconds, [.. mutants], null)
                        { Unreported = unreported, Schema = schema is not null, MutantParallelism = parallelism });
                }
            });

            return new SyntheticRun(sha, testClasses, baseline.Seconds, mutants, null)
                { Unreported = unreported, Schema = schema is not null, MutantParallelism = parallelism };
        }
        catch (Exception ex) when (Expected(ex))
        {
            var cause = ex is AggregateException aggregate ? aggregate.InnerExceptions[0] : ex;
            LogFailed(sha, cause);
            return new SyntheticRun(sha, 0, 0, mutants, cause.Message);
        }
    }

    /// <summary>
    /// True for the failures an evaluation reports instead of crashing on: a build, a run or a file
    /// that went wrong, also when they come out of mutants running in parallel.
    /// </summary>
    /// <param name="exception">The failure.</param>
    private static bool Expected(Exception exception) => exception switch
    {
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.All(Expected),
        _ => exception is InvalidOperationException or InvalidDataException or IOException,
    };

    /// <summary>What every mutant of one commit shares.</summary>
    /// <param name="TestProjects">Built test projects.</param>
    /// <param name="Maps">Coverage maps at the commit, by normalized test project path.</param>
    /// <param name="Assemblies">Repository assemblies in the test outputs, where mutants are injected.</param>
    /// <param name="Baseline">Full run without mutants, whose failures are not a mutant's.</param>
    /// <param name="Schema">Every sampled mutant written in behind a switch; null to write each in alone.</param>
    private sealed record MutantContext(
        List<ProjectNode> TestProjects,
        Dictionary<string, CoverageMap> Maps,
        List<string> Assemblies,
        TestRunOutcome Baseline,
        Schema? Schema);

    /// <summary>
    /// Writes every target in once behind the switch (see <see cref="MutationInjector.InjectSchema"/>)
    /// and runs the full suite again with no mutant on. The guards must not change behaviour: when that
    /// run fails other classes than the baseline, or does not complete, the schema is removed and every
    /// mutant is written in alone, as before; the report says which way the commit was evaluated.
    /// </summary>
    /// <param name="assemblies">Repository assemblies in the test outputs.</param>
    /// <param name="targets">Sampled mutants; their 1-based position is their id.</param>
    /// <param name="testProjects">Test projects that reported in the baseline.</param>
    /// <param name="baseline">Run of those projects without any schema.</param>
    private Schema? TrySchema(List<string> assemblies, List<SyntheticTarget> targets, List<ProjectNode> testProjects, TestRunOutcome baseline)
    {
        var schema = MutationInjector.InjectSchema(
            assemblies,
            [.. targets.Select(static (t, i) => new SchemaTarget(i + 1, t.Member, t.Kind))],
            Path.Combine(workspace.MapCache, "..", "schema-backup"));
        var check = workspace.Executor.RunAll(testProjects, "baseline-schema");
        var same = check.Complete && testProjects.All(p =>
            (baseline.FailedByProject.GetValueOrDefault(p.Path) ?? new HashSet<string>())
                .SetEquals(check.FailedByProject.GetValueOrDefault(p.Path) ?? new HashSet<string>()));
        if (same)
        {
            LogSchema(schema.Entries.Count);
            return schema;
        }

        LogSchemaChangedBehaviour();
        schema.Dispose();
        return null;
    }

    /// <summary>
    /// How many mutants run at once: <see cref="RunSettings.MutantParallelism"/> when the commit has a
    /// schema, the repository has no cleanup command (which would run between other mutants' tests)
    /// and that many runs of the full suite at the same time, with no mutant on, each fail exactly the
    /// baseline's classes; otherwise 1. Suites that write fixed files next to their binaries or bind
    /// fixed ports interfere with themselves, and their mutants are then run one at a time.
    /// </summary>
    /// <param name="schema">The commit's schema, or null.</param>
    /// <param name="testProjects">Test projects that reported in the baseline.</param>
    /// <param name="baseline">Run of those projects without any mutant.</param>
    private int Parallelism(Schema? schema, List<ProjectNode> testProjects, TestRunOutcome baseline)
    {
        var wanted = settings.MutantParallelism;
        if (wanted <= 1 || schema is null || settings.Commands.Cleanup is not null)
        {
            return 1;
        }

        var checks = new TestRunOutcome[wanted];
        workspace.Executor.Concurrency = wanted;
        Parallel.For(0, wanted, new ParallelOptions { MaxDegreeOfParallelism = wanted }, i =>
            checks[i] = workspace.Executor.RunAll(testProjects, $"parallel-check-{i + 1}"));
        var same = checks.All(check => check.Complete && testProjects.All(p =>
            (baseline.FailedByProject.GetValueOrDefault(p.Path) ?? new HashSet<string>())
                .SetEquals(check.FailedByProject.GetValueOrDefault(p.Path) ?? new HashSet<string>())));
        if (same)
        {
            LogParallel(wanted);
            return wanted;
        }

        LogParallelInterferes(wanted);
        return 1;
    }

    /// <summary>
    /// The mutant the tests run against: switched on through the environment when the commit has a
    /// schema, or written into the assemblies alone. Lifting it for a control run is running without
    /// the switch, or putting the original files back.
    /// </summary>
    /// <param name="mutatedMethods">Compiled methods (or field stores) the mutant changes.</param>
    /// <param name="outputs">Directories holding a copy that contains it.</param>
    /// <param name="environment">Environment of the test processes that turns it on; null when written in.</param>
    /// <param name="mutation">The mutant written in alone, or null with a schema.</param>
    private sealed class ActiveMutant(int mutatedMethods, IReadOnlySet<string> outputs, IReadOnlyDictionary<string, string>? environment, Mutation? mutation) : IDisposable
    {
        /// <summary>Compiled methods (or field stores) the mutant changes.</summary>
        public int MutatedMethods { get; } = mutatedMethods;

        /// <summary>Directories holding a copy that contains it.</summary>
        public IReadOnlySet<string> Outputs { get; } = outputs;

        /// <summary>Environment of the test processes that turns it on; null when written in.</summary>
        public IReadOnlyDictionary<string, string>? Environment { get; } = environment;

        /// <summary>Takes the mutant away until the result is disposed (nothing to do with a schema).</summary>
        public IDisposable Lift() => mutation?.Lift() ?? Nothing.Instance;

        /// <summary>Puts the original files back when the mutant was written in alone.</summary>
        public void Dispose() => mutation?.Dispose();

        /// <summary>A lift that changes nothing.</summary>
        private sealed class Nothing : IDisposable
        {
            /// <summary>The one instance.</summary>
            public static readonly Nothing Instance = new();

            /// <summary>Does nothing.</summary>
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Applies the mutant of <paramref name="target"/>: switches it on in the schema, or writes it in alone.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="context">What the mutants of the commit share.</param>
    /// <param name="index">1-based position in the sample, which is its schema id.</param>
    private ActiveMutant Apply(SyntheticTarget target, MutantContext context, int index)
    {
        if (context.Schema is { } schema)
        {
            var entry = schema.Entries.GetValueOrDefault(index);
            var outputs = entry?.Assemblies.Select(static a => Path.GetDirectoryName(a)!).ToHashSet(StringComparer.Ordinal) ?? [];
            return new ActiveMutant(
                entry?.MutatedMethods ?? 0,
                outputs,
                new Dictionary<string, string> { [Schema.Variable] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                mutation: null);
        }

        var mutation = MutationInjector.Inject(context.Assemblies, target.Member, Path.Combine(workspace.MapCache, "..", "mutant-backup"), target.Kind);
        var directories = mutation.Backups.Keys.Select(static f => Path.GetDirectoryName(f)!).ToHashSet(StringComparer.Ordinal);
        return new ActiveMutant(mutation.MutatedMethods, directories, environment: null, mutation);
    }

    /// <summary>What Sift selected for one synthetic change; all a mutant's evaluation needs of the analysis.</summary>
    /// <param name="Tests">Sift's decision per affected test project.</param>
    /// <param name="Detected">Whether Sift saw the change.</param>
    /// <param name="Share">Share of recorded classes Sift selected.</param>
    private sealed record Selection(IReadOnlyList<TestProjectSelection> Tests, bool Detected, double Share);

    /// <summary>Lets Sift select for one synthetic change (editing and restoring the sources).</summary>
    /// <param name="sha">Commit the maps were recorded at.</param>
    /// <param name="target">Member to change.</param>
    /// <param name="maps">Coverage maps at the commit.</param>
    private Selection Select(string sha, SyntheticTarget target, Dictionary<string, CoverageMap> maps)
    {
        // A hang in Sift (an exponential search, Swashbuckle 2026-10-02) would otherwise stop the
        // evaluation for good without a word; it is reported as what it is, a finding about Sift.
        var analyzing = Task.Run(() => Analyze(sha, target));
        if (!analyzing.Wait(settings.AnalysisTimeout))
        {
            LogAnalysisHung(target.Member.TypeName, target.Member.MemberName, settings.AnalysisTimeout.TotalMinutes);
            throw new InvalidOperationException(
                $"Sift's analysis of a change to {target.Member.TypeName}.{target.Member.MemberName} did not finish within " +
                $"{settings.AnalysisTimeout.TotalMinutes:0.#} minute(s): a hang or a slowdown in Sift.");
        }

        var analysis = analyzing.Result;
        var detected = analysis.Impact.CodeChanges.Any(c =>
            c.AffectsWholeProject || c.Symbols.Any(s => s.TypeName == target.Member.TypeName));
        return new Selection(analysis.Tests, detected, SelectedShare(analysis, maps));
    }

    /// <summary>Runs the tests against one mutant and compares the failures with Sift's selection.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="context">What the mutants of the commit share.</param>
    /// <param name="index">1-based number of the mutant, for log files and the schema switch.</param>
    /// <param name="selection">What Sift selected for the change.</param>
    private SyntheticMutant Run(SyntheticTarget target, MutantContext context, int index, Selection selection)
    {
        var (tests, detected, share) = selection;
        var state = new MutantRun(tests, context.TestProjects, index, detected, share);
        if (_cache?.Find(target) is { } stored && FromCache(target, stored, state) is { } reused)
        {
            return reused;
        }

        using var mutation = Apply(target, context, index);
        if (mutation.MutatedMethods == 0)
        {
            LogNotFound(target.Member.TypeName, target.Member.MemberName);
            _cache?.Store(target, new MutantOutcome(0, [], []));
            return new SyntheticMutant(target, detected, 0, [], [], share, Inconclusive: false);
        }

        // Only a test project whose output holds a mutated copy can fail because of the mutant.
        var reached = context.TestProjects
            .Where(p => workspace.Executor.Outputs.TryGetValue(p.Path, out var output) && mutation.Outputs.Contains(output))
            .ToList();
        // A project that wrote no report this time died with the mutant in place, like a crash.
        var run = workspace.Executor.RunAll(reached, $"synthetic-{index}", mutation.Environment);
        if (!run.Complete)
        {
            return RunOutsideSelection(target, new MutantRun(tests, reached, index, detected, share), mutation);
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
                if (!EvalSteps.IsSelected(tests, project, testClass))
                {
                    missed.Add(name);
                }
            }
        }

        var failedBeforeConfirming = new List<string>(killed);
        var alone = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (missed.Count > 0)
        {
            missed = Confirm(missed, new MutantScope(reached, index, mutation), killed, alone);
        }

        _cache?.Store(target, new MutantOutcome(mutation.MutatedMethods, failedBeforeConfirming, alone));
        LogMutant(index, target.Member.TypeName, target.Member.MemberName, killed.Count, missed.Count, share);
        return new SyntheticMutant(target, detected, mutation.MutatedMethods, killed, missed, share, Inconclusive: false);
    }

    /// <summary>
    /// The mutant's result from a stored test outcome and the current selection, without running a
    /// test: the stored failures are checked against what Sift selects now, and the confirmation
    /// rules of <see cref="Confirm"/> are applied to stored answers. Null when an answer is missing
    /// (a class missed now was selected when the outcome was stored) or a failing class belongs to a
    /// project that is no longer evaluated; the mutant is then run.
    /// </summary>
    /// <param name="target">The mutant.</param>
    /// <param name="stored">What the tests did against it.</param>
    /// <param name="state">The mutant's analysis and the evaluated test projects.</param>
    private SyntheticMutant? FromCache(SyntheticTarget target, MutantOutcome stored, MutantRun state)
    {
        if (stored.MutatedMethods == 0)
        {
            LogNotFound(target.Member.TypeName, target.Member.MemberName);
            return new SyntheticMutant(target, state.Detected, 0, [], [], state.Share, Inconclusive: false) { Reused = true };
        }

        var projects = state.Reached.ToDictionary(static p => Path.GetFileNameWithoutExtension(p.Path), static p => p.Path, StringComparer.Ordinal);
        var killed = new List<string>(stored.Failed);
        var missed = new List<string>();
        foreach (var failed in stored.Failed)
        {
            var separator = failed.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0 || !projects.TryGetValue(failed[..separator], out var project))
            {
                return null;
            }

            if (!EvalSteps.IsSelected(state.Tests, project, failed[(separator + 2)..]))
            {
                missed.Add(failed);
            }
        }

        if (missed.Exists(m => !stored.FailsAloneWithMutant.ContainsKey(m)))
        {
            return null;
        }

        var confirmed = missed.FindAll(m => stored.FailsAloneWithMutant[m]);
        if (confirmed.Exists(c => KnownFailsAlone(c) is null))
        {
            return null;
        }

        foreach (var unconfirmed in missed.Where(m => !stored.FailsAloneWithMutant[m]))
        {
            LogUnconfirmed(state.Index, unconfirmed);
            killed.Remove(unconfirmed);
        }

        foreach (var broken in confirmed.Where(c => KnownFailsAlone(c) == true))
        {
            LogFailsWithoutMutant(state.Index, broken);
            killed.Remove(broken);
        }

        missed = confirmed.FindAll(c => KnownFailsAlone(c) == false);
        LogReused(state.Index);
        LogMutant(state.Index, target.Member.TypeName, target.Member.MemberName, killed.Count, missed.Count, state.Share);
        return new SyntheticMutant(target, state.Detected, stored.MutatedMethods, killed, missed, state.Share, Inconclusive: false) { Reused = true };
    }

    /// <summary>Whether a class fails alone without any mutant, from this run or the cache; null when unknown.</summary>
    /// <param name="testClass">Test class (<c>Project: Class</c>).</param>
    private bool? KnownFailsAlone(string testClass) =>
        _failsAlone.TryGetValue(testClass, out var fails) ? fails : _cache?.FailsAlone(testClass);

    /// <summary>One mutant's state while it is evaluated.</summary>
    /// <param name="Tests">Sift's decision per affected test project.</param>
    /// <param name="Reached">Test projects the mutant can reach.</param>
    /// <param name="Index">Number of the mutant, for log files.</param>
    /// <param name="Detected">Whether Sift saw the change.</param>
    /// <param name="Share">Share of recorded classes Sift selected.</param>
    private sealed record MutantRun(IReadOnlyList<TestProjectSelection> Tests, List<ProjectNode> Reached, int Index, bool Detected, double Share);

    /// <summary>
    /// After a full run crashed or hung, runs only what Sift left out, with the mutant in place. A miss
    /// can only be there: if it passes, the failure was in the selection and the mutant counts as
    /// caught; classes that fail are confirmed alone like any miss; a second crash or hang leaves the
    /// mutant inconclusive. A project Sift runs completely has nothing left out.
    /// </summary>
    /// <param name="target">The mutated member.</param>
    /// <param name="state">The mutant's state.</param>
    /// <param name="mutation">The applied mutant.</param>
    private SyntheticMutant RunOutsideSelection(SyntheticTarget target, MutantRun state, ActiveMutant mutation)
    {
        var mutatedMethods = mutation.MutatedMethods;
        var mode = DotnetTestModeDetector.Detect(workspace.RepoRoot);
        var invocations = new List<TestInvocation>();
        foreach (var project in state.Reached)
        {
            var decision = state.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, project.Path, StringComparison.Ordinal));
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
        var outside = workspace.Executor.RunSelected(invocations, $"synthetic-{state.Index}-outside", mutation.Environment);
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
        var missed = Confirm(failed, new MutantScope(state.Reached, state.Index, mutation), killed, new Dictionary<string, bool>(StringComparer.Ordinal));
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
    private sealed record MutantScope(List<ProjectNode> Reached, int Index, ActiveMutant Mutation);

    /// <summary>
    /// Whether a test class fails when it runs alone without any mutant, by <c>Project: Class</c>.
    /// Every mutant of a commit shares the same build, so the answer holds for all of them.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _failsAlone = new(StringComparer.Ordinal);

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
    /// <param name="alone">Receives, for a complete run alone, whether each missed class failed again.</param>
    private List<string> Confirm(List<string> missed, MutantScope scope, List<string> killed, Dictionary<string, bool> alone)
    {
        var again = RunAlone(missed, scope.Reached, $"synthetic-{scope.Index}-confirm", scope.Mutation.Environment);
        if (again.Complete)
        {
            foreach (var testClass in missed)
            {
                alone[testClass] = again.Failed.Contains(testClass);
            }
        }

        var confirmed = missed.FindAll(again.Failed.Contains);
        foreach (var unconfirmed in missed.Where(m => !again.Failed.Contains(m)))
        {
            LogUnconfirmed(scope.Index, unconfirmed);
            killed.Remove(unconfirmed);
        }

        foreach (var testClass in confirmed)
        {
            if (!_failsAlone.ContainsKey(testClass) && _cache?.FailsAlone(testClass) is { } cached)
            {
                _failsAlone[testClass] = cached;
            }
        }

        var unknown = confirmed.FindAll(c => !_failsAlone.ContainsKey(c));
        if (unknown.Count > 0)
        {
            RunOutcome control;
            using (scope.Mutation.Lift())
            {
                control = RunAlone(unknown, scope.Reached, $"synthetic-{scope.Index}-control", environment: null);
            }

            // A control run that crashed proves nothing; the miss stands and the class is asked again next time.
            if (control.Complete)
            {
                foreach (var testClass in unknown)
                {
                    _failsAlone[testClass] = control.Failed.Contains(testClass);
                    _cache?.StoreFailsAlone(testClass, _failsAlone[testClass]);
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
    /// <param name="environment">Environment that switches the mutant on; null for none.</param>
    private RunOutcome RunAlone(List<string> classes, List<ProjectNode> reached, string label, IReadOnlyDictionary<string, string>? environment)
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
        var run = workspace.Executor.RunSelected(invocations, label, environment);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Reusing {Count} stored mutant outcome(s); only the selection is computed for them")]
    private partial void LogCacheOpened(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mutant {Index}: test outcome reused from the cache")]
    private partial void LogReused(int index);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema: {Count} mutant(s) written in behind a switch; the full suite behaves the same with it")]
    private partial void LogSchema(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schema: the full suite fails differently with the switches written in; every mutant is written in alone instead")]
    private partial void LogSchemaChangedBehaviour();

    [LoggerMessage(Level = LogLevel.Error, Message = "Sift's analysis of a change to {TypeName}.{MemberName} did not finish within {Minutes} minute(s)")]
    private partial void LogAnalysisHung(string typeName, string memberName, double minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} mutants run at once: that many runs of the full suite at the same time fail as the baseline does")]
    private partial void LogParallel(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} runs of the full suite at the same time fail differently from the baseline; mutants run one at a time")]
    private partial void LogParallelInterferes(int count);
}
