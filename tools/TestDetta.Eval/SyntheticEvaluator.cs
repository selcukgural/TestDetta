using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Eval;

/// <summary>
/// Evaluates TestDetta on changes it makes itself instead of the repository's history: coverage is
/// recorded once at a commit, then for each sampled member the source is edited (so TestDetta sees the
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

    /// <summary>Builds a change that reaches a source generator, for the selection to read what it generates; set per evaluation.</summary>
    private GeneratorWorkspace? _generators;

    /// <summary>
    /// The generator mutants of the evaluation (docs/design/generator-mutants.md): the evaluated test
    /// projects that see what the generator writes, by target.
    /// </summary>
    private Dictionary<SyntheticTarget, List<ProjectNode>> _generatorTests = new(ReferenceEqualityComparer.Instance);

    /// <summary>The generator projects and their consumers in the commit's graph; set per evaluation.</summary>
    private GeneratorMutants? _generatorMutants;

    /// <summary>The built assemblies of each generator mutant's generators in the clone, which its consumers' compilers load.</summary>
    private readonly Dictionary<SyntheticTarget, List<string>> _generatorAssemblies = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// True when a file of <paramref name="target"/> is compiled by a project that another project
    /// references without compiling against it, not as a source generator (<see cref="EvalFocus.Output"/>).
    /// </summary>
    /// <param name="graph">Projects of the clone.</param>
    /// <param name="target">A sampled change.</param>
    private static bool ReferencedForOutput(ProjectGraph graph, SyntheticTarget target) =>
        target.Edits
            .SelectMany(e => graph.ResolveFile(Paths.Normalize(e.Path)))
            .Where(static o => o.Role == FileRole.Source)
            .Any(o => graph.OpaqueConsumersOf(o.Project.Path).Any(c => !c.GeneratorReferences.Contains(o.Project.Path)));

    /// <summary>Evaluates sampled changes at <paramref name="sha"/>; a build failure is reported, not thrown.</summary>
    /// <param name="sha">Commit to change.</param>
    /// <param name="request">How many members to change.</param>
    /// <param name="progress">Called after every mutant with the results so far, so a long run can be read while it runs.</param>
    public SyntheticRun Evaluate(string sha, SampleRequest request, Action<SyntheticRun> progress)
    {
        var mutants = new List<SyntheticMutant>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        List<string> incidents = [];
        List<MapCount> mapCounts = [];
        try
        {
            // What the tests do depends on the SDK and runtime too, so a toolchain has its own outcomes.
            var cacheKey = settings.Toolchain is { } toolchain ? $"{sha}-{toolchain.Rid}-{toolchain.DirectoryName}" : sha;
            if (settings.Environment.Count > 0)
            {
                // Variables can change what the tests do, so they get outcomes of their own.
                cacheKey += "-env" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(EvalSteps.EnvironmentKey(settings))))[..12];
            }

            _cache = OutcomeCache.Open(settings.OutcomeCache, cacheKey);
            _generators = new GeneratorWorkspace(workspace, settings.OutcomeCache is { } outcomes ? Path.Combine(outcomes, "generated", cacheKey) : null);
            if (_cache is { Count: > 0 } cache)
            {
                LogCacheOpened(cache.Count);
            }

            // Drift modes select with maps from an older commit; recorded first, the evaluated commit's
            // recording then leaves its own build behind for the mutants.
            var mapCommit = settings.MapAge > 0 ? workspace.Git.FirstParentAncestor(sha, settings.MapAge) : null;
            if (mapCommit is not null)
            {
                _steps.RecordAt(mapCommit);
            }

            var maps = _steps.RecordAt(sha);
            incidents = _steps.Incidents(sha, maps);
            mapCounts = [.. maps.Values
                .OrderBy(static m => m.TestProject, StringComparer.Ordinal)
                .Select(static m => new MapCount(Path.GetFileNameWithoutExtension(m.TestProject), m.Classes.Count, m.FailedClasses?.Count))];
            workspace.Git.Checkout(sha);
            if (settings.ExplainOnly)
            {
                Explain(sha, maps, request);
                return new SyntheticRun(sha, maps.Values.Sum(static m => m.Classes.Count), 0, [], null)
                {
                    Seconds = started.Elapsed.TotalSeconds,
                    Incidents = incidents,
                    Maps = mapCounts,
                };
            }

            workspace.Executor.Setup();
            var untracked = workspace.Git.Untracked();
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>(), workspace.Evaluator).Load(workspace.RepoRoot);
            var candidates = _steps.TestProjects(graph);
            var testClasses = maps.Values.Sum(static m => m.Classes.Count);
            var targets = TargetSampler.Sample(graph, workspace.RepoRoot, CoveredMembers(maps), request);

            // After the members, so the member sample and its stored outcomes stay as they were.
            targets.AddRange(DataFileSampler.Sample(graph, workspace.RepoRoot, maps.Values, request.DataFiles, request.Seed));
            var coveredTargets = targets.Count(static t => t.Covered);
            LogSampled(targets.Count, coveredTargets);
            var generatorMutants = new GeneratorMutants(graph);
            _generatorMutants = generatorMutants;
            _generatorTests = new Dictionary<SyntheticTarget, List<ProjectNode>>(ReferenceEqualityComparer.Instance);
            foreach (var target in targets)
            {
                if (generatorMutants.TestProjectsReached(target.Edits.Select(static e => e.Path), candidates) is { Count: > 0 } reachedByGenerator)
                {
                    _generatorTests[target] = reachedByGenerator;
                }
            }

            if (_generatorTests.Count > 0)
            {
                LogGeneratorMutants(_generatorTests.Count);
            }

            // A quick check keeps part of the usual sample: the same targets, so their stored outcomes count.
            if (settings.Focus != EvalFocus.All || settings.MaxMutants is not null)
            {
                var sampled = targets.Count;
                targets = [.. targets
                    .Where(t => settings.Focus switch
                    {
                        EvalFocus.Generator => _generatorTests.ContainsKey(t),
                        EvalFocus.DataFiles => t.Kind == MutantKind.EmptyFile,
                        EvalFocus.Output => ReferencedForOutput(graph, t),
                        _ => true,
                    })
                    .Take(settings.MaxMutants ?? int.MaxValue)];
                var focus = EvalFocusNames.Name(settings.Focus);
                LogFocused(targets.Count, sampled, focus);
            }

            // A stored baseline means the outcomes below were measured against it; only the mutants
            // without a stored outcome then need a build and test runs.
            var stored = StoredBaselineFor(candidates);
            var baseline = stored ?? RunBaseline(candidates);
            if (stored is not null)
            {
                LogBaselineReused(stored.Projects.Count);
            }

            // Selecting edits the clone's sources, so it happens one mutant at a time, and before any
            // binary is touched: a hang in TestDetta's analysis then leaves the clone as it was. What the
            // baseline run wrote into the clone goes first: TestDetta would see it as a change.
            _steps.RemoveLeftBehind(untracked);
            var selections = targets.Select(t => Select(sha, t, maps, mapCommit, candidates)).ToList();

            var results = new SyntheticMutant?[targets.Count];
            var pending = new List<int>();
            for (var i = 0; i < targets.Count; i++)
            {
                var (tests, detected, share, drift) = selections[i];
                var state = new MutantRun(tests, baseline.Projects, i + 1, detected, share) { Others = drift };
                // An outcome stored before generator mutants compiled their consumers does not hold for one.
                if (_cache?.Find(targets[i]) is { } outcome
                    && (outcome.ConsumersCompiled || !_generatorTests.ContainsKey(targets[i]))
                    && FromCache(targets[i], outcome, state, baseline, candidates) is { } reused)
                {
                    results[i] = reused with { StoppedOnBuild = selections[i].StoppedOnBuild };
                    continue;
                }

                pending.Add(i);
            }

            var gate = new object();
            var usedSchema = false;
            var usedParallelism = 1;
            SyntheticRun Snapshot() => new(sha, testClasses, baseline.Outcome.Seconds, [.. results.OfType<SyntheticMutant>()], null)
            {
                Unreported = baseline.Unreported,
                Schema = usedSchema,
                MutantParallelism = usedParallelism,
                Toolchain = settings.Toolchain?.Description,
                Seconds = started.Elapsed.TotalSeconds,
                MapAge = settings.MapAge,
                MapCommit = mapCommit,
                Incidents = incidents,
                Maps = mapCounts,
            };

            void Report()
            {
                lock (gate)
                {
                    mutants.Clear();
                    mutants.AddRange(results.OfType<SyntheticMutant>());
                    progress(Snapshot());
                }
            }

            Report();
            if (pending.Count == 0)
            {
                return Snapshot();
            }

            if (stored is not null)
            {
                baseline = BuildForStoredBaseline(stored);
            }

            var testProjects = baseline.Projects;
            var assemblies = EvalSteps.RepositoryAssemblies(
                new ProjectAssemblies(_logger).FileNames(graph, workspace.RepoRoot), workspace.Executor.Outputs.Values);

            // A generator mutant also goes into the generator's own build, which its consumers' compilers load.
            var built = new Dictionary<string, List<string>>(Paths.Comparer);
            foreach (var target in pending.Select(i => targets[i]).Where(_generatorTests.ContainsKey))
            {
                var own = new List<string>();
                foreach (var generator in generatorMutants.Generators(target.Edits.Select(static e => e.Path)))
                {
                    if (!built.TryGetValue(generator.Path, out var found))
                    {
                        built[generator.Path] = found = GeneratorMutants.Assemblies(generator, _logger);
                    }

                    own.AddRange(found);
                }

                _generatorAssemblies[target] = own;
            }

            assemblies.AddRange([.. built.Values.SelectMany(static a => a).Distinct(StringComparer.Ordinal).Where(a => !assemblies.Contains(a, StringComparer.Ordinal))]);
            var pendingTargets = pending.Select(i => (Id: i + 1, Target: targets[i])).ToList();
            using var schema = settings.UseSchema ? TrySchema(assemblies, pendingTargets, testProjects, baseline.Outcome) : null;
            var context = new MutantContext(testProjects, maps, assemblies, baseline.Outcome, schema) { BaselineClasses = baseline.ClassesByProject };
            var (parallelism, copies) = Parallelism(schema, testProjects, baseline.Outcome, pending.Count);
            using var workerCopies = copies;
            workspace.Executor.Concurrency = parallelism;
            usedSchema = schema is not null;
            usedParallelism = parallelism;

            // Each worker runs in its own copy of the clone; a mutant takes a free one for all its runs.
            var free = new System.Collections.Concurrent.ConcurrentBag<TestExecutor>(
                copies is null ? [workspace.Executor] : Executors(copies));
            Parallel.For(0, pending.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, n =>
            {
                var i = pending[n];
                if (!free.TryTake(out var executor))
                {
                    throw new InvalidOperationException("More mutants ran at once than there are worker copies.");
                }

                SyntheticMutant result;
                try
                {
                    result = Run(targets[i], context, i + 1, selections[i], executor) with { StoppedOnBuild = selections[i].StoppedOnBuild };
                }
                finally
                {
                    free.Add(executor);
                }

                lock (gate)
                {
                    results[i] = result;
                }

                Report();
            });

            if (!settings.KeepClones)
            {
                RemoveReports(Path.Combine(workspace.Executor.LogDirectory, "trx"), results);
            }

            return Snapshot();
        }
        catch (Exception ex) when (Expected(ex))
        {
            var cause = ex is AggregateException aggregate ? aggregate.InnerExceptions[0] : ex;
            LogFailed(sha, cause);
            return new SyntheticRun(sha, 0, 0, mutants, cause.Message)
            {
                Toolchain = settings.Toolchain?.Description,
                Seconds = started.Elapsed.TotalSeconds,
                Incidents = incidents,
                Maps = mapCounts,
            };
        }
        finally
        {
            _generators?.Remove();
            _generators = null;
        }
    }

    /// <summary>
    /// Deletes the test reports of the evaluation, which are read by now, except those of mutants that
    /// missed a class or crashed, which are kept to look into; <see cref="RunSettings.KeepClones"/> keeps all.
    /// </summary>
    /// <param name="directory">The reports, one directory per run.</param>
    /// <param name="results">The mutants, by index from 0; a report's run name holds the index from 1.</param>
    internal static void RemoveReports(string directory, IReadOnlyList<SyntheticMutant?> results)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var report in Directory.EnumerateDirectories(directory))
        {
            var match = MutantRunName().Match(Path.GetFileName(report));
            var index = match.Success ? int.Parse(match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) - 1 : -1;
            if (index >= 0 && index < results.Count && results[index] is not { MissedClasses.Count: 0, Inconclusive: false, CaughtByCrash: false })
            {
                continue;
            }

            Directory.Delete(report, recursive: true);
        }
    }

    /// <summary>A mutant's run directory, such as <c>042-synthetic-7</c> or <c>043-synthetic-7-confirm</c>; group 1 is the mutant's index from 1.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+-synthetic-(\d+)(?:-|$)")]
    private static partial System.Text.RegularExpressions.Regex MutantRunName();

    /// <summary>The run without mutants that every mutant of the commit is compared with.</summary>
    /// <param name="Projects">Test projects that built and reported; the ones the mutants run.</param>
    /// <param name="Outcome">The run.</param>
    /// <param name="Unreported">Projects left out because they wrote no report or crashed, relative to the clone.</param>
    private sealed record Baseline(List<ProjectNode> Projects, TestRunOutcome Outcome, List<string> Unreported)
    {
        /// <summary>Test classes in each reporting project's report, by normalized path; a whole run with a mutant that reports fewer did not complete.</summary>
        public IReadOnlyDictionary<string, int> ClassesByProject { get; init; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Builds the test projects and runs them without mutants. A complete run is stored with the
    /// outcomes, so a later round whose mutants are all stored skips the build and this run.
    /// </summary>
    /// <param name="candidates">Test projects to evaluate.</param>
    private Baseline RunBaseline(List<ProjectNode> candidates)
    {
        var built = _steps.Build(candidates);
        if (built.Count == 0)
        {
            throw new InvalidOperationException("No test project could be built.");
        }

        workspace.Executor.Outputs = _steps.OutputDirectories(built);
        var outcome = workspace.Executor.RunAll(built, "baseline");
        var unreported = built.Where(p => outcome.Unreported.Contains(p.Path) || outcome.Crashed.Contains(p.Path)
                || outcome.Projects.TryGetValue(p.Path, out var run) && !run.Complete)
            .Select(Relative).ToList();
        var reporting = _steps.Reporting(built, outcome);
        if (reporting.Count == 0)
        {
            throw new InvalidOperationException("No test project wrote a test report.");
        }

        // A project that writes no report without crashing (no tests, no TRX logger) does so every
        // time; one that crashed or hung may have done so under load and must run again next time.
        var classes = reporting.ToDictionary(static p => p.Path, p => outcome.Projects.GetValueOrDefault(p.Path)?.Classes ?? 0, StringComparer.Ordinal);
        if (outcome.Aborted == 0 && outcome.Crashed.Count == 0)
        {
            _cache?.StoreBaseline(new StoredBaseline(
                [.. candidates.Select(Relative)],
                [.. reporting.Select(Relative)],
                outcome.FailedByProject.ToDictionary(p => Relative(p.Key), static p => p.Value.Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal),
                outcome.Seconds,
                outcome.Total)
            {
                Unreported = unreported,
                ClassesByProject = classes.ToDictionary(p => Relative(p.Key), static p => p.Value, StringComparer.Ordinal),
            });
        }

        return new Baseline(reporting, outcome, unreported) { ClassesByProject = classes };
    }

    /// <summary>
    /// The stored run without mutants, when it was made with the same test projects as today; null
    /// otherwise, and the baseline is run. No project crashed or hung in a stored run (see <see cref="OutcomeCache.StoreBaseline"/>).
    /// </summary>
    /// <param name="candidates">Test projects to evaluate.</param>
    private Baseline? StoredBaselineFor(List<ProjectNode> candidates)
    {
        // A baseline stored without its class counts cannot tell a whole run that stopped early: it runs again.
        if (_cache?.Baseline is not { ClassesByProject: { } counts } stored || !stored.Candidates.SequenceEqual(candidates.Select(Relative), StringComparer.Ordinal))
        {
            return null;
        }

        var byPath = candidates.ToDictionary(Relative, StringComparer.Ordinal);
        var projects = new List<ProjectNode>(stored.Reporting.Count);
        foreach (var path in stored.Reporting)
        {
            if (!byPath.TryGetValue(path, out var project))
            {
                return null;
            }

            projects.Add(project);
        }

        var failed = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (path, classes) in stored.FailedByProject)
        {
            if (byPath.TryGetValue(path, out var project))
            {
                failed[project.Path] = classes.ToHashSet(StringComparer.Ordinal);
            }
        }

        var outcome = new TestRunOutcome(stored.Seconds, stored.Total, failed, new HashSet<string>(StringComparer.Ordinal), 0);
        return new Baseline(projects, outcome, stored.Unreported)
        {
            ClassesByProject = projects.ToDictionary(static p => p.Path, p => counts.GetValueOrDefault(Relative(p)), StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Builds the projects of a stored baseline when some mutant has to run after all. A project that
    /// no longer builds is left out of those runs; its stored failures still hold for the others.
    /// </summary>
    /// <param name="stored">The stored baseline.</param>
    private Baseline BuildForStoredBaseline(Baseline stored)
    {
        var built = _steps.Build(stored.Projects);
        if (built.Count == 0)
        {
            throw new InvalidOperationException("No test project could be built.");
        }

        workspace.Executor.Outputs = _steps.OutputDirectories(built);
        return stored with { Projects = built };
    }

    /// <summary>A path of the clone relative to its root, with forward slashes, as the cache stores it.</summary>
    /// <param name="path">Full path inside the clone.</param>
    private string Relative(string path) => Path.GetRelativePath(workspace.RepoRoot, path).Replace('\\', '/');

    /// <summary>See <see cref="Relative(string)"/>, for a project.</summary>
    /// <param name="project">A project of the clone.</param>
    private string Relative(ProjectNode project) => Relative(project.Path);

    /// <summary>
    /// Writes <c>explain.json</c> next to the maps' workspace: for every sampled change, TestDetta's
    /// decision per affected test project and the evidence of each selected class, without building
    /// or running anything. The same sample as an evaluation with the same maps and request.
    /// </summary>
    /// <param name="sha">Commit the changes are made on.</param>
    /// <param name="maps">Maps of the commit.</param>
    /// <param name="request">The sample.</param>
    private void Explain(string sha, Dictionary<string, CoverageMap> maps, SampleRequest request)
    {
        workspace.Executor.Setup();
        var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>(), workspace.Evaluator).Load(workspace.RepoRoot);
        var targets = TargetSampler.Sample(graph, workspace.RepoRoot, CoveredMembers(maps), request);
        var covered = targets.Count(static t => t.Covered);
        LogSampled(targets.Count, covered);
        var path = Path.GetFullPath(Path.Combine(workspace.MapCache, "..", "explain.json"));
        using var stream = File.Create(path);
        using var json = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("sha", sha);
        json.WriteNumber("testClasses", maps.Values.Sum(static m => m.Classes.Count));
        json.WriteStartArray("changes");
        foreach (var target in targets)
        {
            var (analysis, _) = Analyze(sha, target, mapCommit: null, new AnalysisStop(CancellationToken.None, CancellationToken.None));
            json.WriteStartObject();
            json.WriteString("target", $"{target.Member.TypeName}.{target.Member.MemberName}");
            json.WriteString("kind", MutantKinds.Name(target.Kind));
            json.WriteBoolean("covered", target.Covered);
            json.WriteNumber("selectedShare", Math.Round(SelectedShare(analysis, maps), 4));
            json.WriteStartArray("projects");
            foreach (var test in analysis.Tests)
            {
                json.WriteStartObject();
                json.WriteString("project", Path.GetFileNameWithoutExtension(test.Project.Path));
                json.WriteString("mode", test.Mode.ToString());
                json.WriteString("reason", test.Reason);
                json.WriteNumber("candidates", test.Decisions.Count);
                json.WriteStartArray("selected");
                foreach (var decision in test.Decisions.Where(static d => d.Selected))
                {
                    json.WriteStartObject();
                    json.WriteString("class", decision.TestClass);
                    json.WriteString("kind", decision.Kind.ToString());
                    json.WriteString("evidence", decision.Evidence);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        LogExplained(targets.Count, path);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Quick check: {Kept} of {Sampled} sampled change(s) kept (focus {Focus})")]
    private partial void LogFocused(int kept, int sampled, string focus);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} sampled change(s) are in a source generator; their consumers are built with the mutant on (docs/design/generator-mutants.md)")]
    private partial void LogGeneratorMutants(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "The analysis of the change to {TypeName}.{MemberName} stops on a failing build, as td test would: {Error}")]
    private partial void LogStoppedOnBuild(string typeName, string memberName, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mutant {Index}: {Project} does not build with it (see {Log})")]
    private partial void LogConsumerNotBuilt(int index, string project, string log);

    [LoggerMessage(Level = LogLevel.Information, Message = "Explained the selection of {Count} change(s) in {Path}")]
    private partial void LogExplained(int count, string path);

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
        Schema? Schema)
    {
        /// <summary>Test classes in the baseline's report of each project, by normalized path.</summary>
        public IReadOnlyDictionary<string, int> BaselineClasses { get; init; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Writes every target in once behind the switch (see <see cref="MutationInjector.InjectSchema"/>)
    /// and runs the full suite again with no mutant on. The guards must not change behaviour: when that
    /// run fails other classes than the baseline, or does not complete, the schema is removed and every
    /// mutant is written in alone, as before; the report says which way the commit was evaluated.
    /// </summary>
    /// <param name="assemblies">Repository assemblies in the test outputs.</param>
    /// <param name="targets">Mutants that run, with their 1-based position in the sample as id; stored ones are not written in.</param>
    /// <param name="testProjects">Test projects that reported in the baseline.</param>
    /// <param name="baseline">Run of those projects without any schema.</param>
    private Schema? TrySchema(List<string> assemblies, List<(int Id, SyntheticTarget Target)> targets, List<ProjectNode> testProjects, TestRunOutcome baseline)
    {
        var schema = MutationInjector.InjectSchema(
            assemblies,
            [.. targets.Where(static t => t.Target.Kind != MutantKind.EmptyFile).Select(static t => new SchemaTarget(t.Id, t.Target.Member, t.Target.Kind))],
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
    /// How many mutants run at once, and the worker copies they run in: <see cref="RunSettings.MutantParallelism"/>
    /// when the commit has a schema, the repository has no cleanup command (which would run between
    /// other mutants' tests), every test output lies in the clone, so each worker can get its own copy
    /// (<see cref="WorkerCopies"/>), and that many runs of the full suite at the same time, one per
    /// copy and with no mutant on, each fail exactly the baseline's classes; otherwise 1 and no copies.
    /// The copies keep apart what tests write into the repository; the check catches what they cannot,
    /// such as a fixed port or a file outside the repository.
    /// </summary>
    /// <param name="schema">The commit's schema, or null.</param>
    /// <param name="testProjects">Test projects that reported in the baseline.</param>
    /// <param name="baseline">Run of those projects without any mutant.</param>
    /// <param name="mutantCount">Mutants that run; more workers than that would only cost the check.</param>
    private (int Count, WorkerCopies? Copies) Parallelism(Schema? schema, List<ProjectNode> testProjects, TestRunOutcome baseline, int mutantCount)
    {
        var wanted = Math.Min(settings.MutantParallelism, mutantCount);
        if (wanted <= 1 || schema is null || settings.Commands.Cleanup is not null)
        {
            return (1, null);
        }

        WorkerCopies? copies;
        try
        {
            copies = WorkerCopies.Create(workspace.RepoRoot, wanted, workspace.Executor.Outputs.Values, _logger);
        }
        catch (IOException ex)
        {
            LogNoCopies(ex);
            return (1, null);
        }

        if (copies is null)
        {
            return (1, null);
        }

        var executors = Executors(copies);
        var checks = new TestRunOutcome[wanted];
        workspace.Executor.Concurrency = wanted;
        Parallel.For(0, wanted, new ParallelOptions { MaxDegreeOfParallelism = wanted }, i =>
            checks[i] = executors[i].RunAll(testProjects, $"parallel-check-{i + 1}"));
        var same = checks.All(check => check.Complete && testProjects.All(p =>
            (baseline.FailedByProject.GetValueOrDefault(p.Path) ?? new HashSet<string>())
                .SetEquals(check.FailedByProject.GetValueOrDefault(p.Path) ?? new HashSet<string>())));
        if (same)
        {
            LogParallel(wanted);
            return (wanted, copies);
        }

        LogParallelInterferes(wanted);
        copies.Dispose();
        return (1, null);
    }

    /// <summary>One executor per worker, each in its copy of the clone with its own temporary directory.</summary>
    /// <param name="copies">The workers' copies.</param>
    private List<TestExecutor> Executors(WorkerCopies copies) =>
        [.. copies.Roots.Select((root, i) => workspace.Executor.In(root, copies.Environment(i)))];

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

        /// <summary>The emptied data file of a <see cref="MutantKind.EmptyFile"/> change; null for a member's mutant.</summary>
        public DataFile? File { get; init; }

        /// <summary>Takes the mutant away until the result is disposed (nothing to do with a schema).</summary>
        public IDisposable Lift() => File?.Lift() ?? mutation?.Lift() ?? Nothing.Instance;

        /// <summary>Puts the original files back when the mutant was written in alone.</summary>
        public void Dispose()
        {
            mutation?.Dispose();
            File?.Dispose();
        }

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
    /// <param name="executor">The worker's executor, in whose copy of the clone a data file is emptied.</param>
    private ActiveMutant Apply(SyntheticTarget target, MutantContext context, int index, TestExecutor executor)
    {
        // A data file is emptied in the worker's copy, where every test project may read it.
        if (target.Kind == MutantKind.EmptyFile)
        {
            var path = Path.Combine(executor.Root, Path.GetRelativePath(workspace.RepoRoot, target.Edits[0].Path));
            return new ActiveMutant(1, workspace.Executor.Outputs.Values.ToHashSet(StringComparer.Ordinal), environment: null, mutation: null)
            {
                File = new DataFile(path).Apply(),
            };
        }

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

    /// <summary>What TestDetta selected for one synthetic change; all a mutant's evaluation needs of the analysis.</summary>
    /// <param name="Tests">TestDetta's decision per affected test project.</param>
    /// <param name="Detected">Whether TestDetta saw the change.</param>
    /// <param name="Share">Share of recorded classes TestDetta selected.</param>
    /// <param name="Others">What each other selector selected; empty without drift modes or dotnet-tia.</param>
    private sealed record Selection(IReadOnlyList<TestProjectSelection> Tests, bool Detected, double Share, IReadOnlyDictionary<OtherSelector, OtherSelection> Others)
    {
        /// <summary>See <see cref="SyntheticMutant.StoppedOnBuild"/>.</summary>
        public bool StoppedOnBuild { get; init; }
    }

    /// <summary>What one drift mode selected.</summary>
    /// <param name="Tests">Decision per affected test project.</param>
    /// <param name="Share">Share of the evaluated commit's recorded classes selected.</param>
    private sealed record OtherSelection(IReadOnlyList<TestProjectSelection> Tests, double Share)
    {
        /// <summary>See <see cref="OtherOutcome.FullRun"/>.</summary>
        public string? FullRun { get; init; }

        /// <summary>See <see cref="OtherOutcome.Seconds"/>.</summary>
        public double? Seconds { get; init; }
    }

    /// <summary>Lets TestDetta select for one synthetic change (editing and restoring the sources).</summary>
    /// <param name="sha">Commit the maps were recorded at.</param>
    /// <param name="target">Member to change.</param>
    /// <param name="maps">Coverage maps at the commit.</param>
    /// <param name="mapCommit">Older commit whose maps the drift modes use; null for none.</param>
    /// <param name="testProjects">Test projects of the commit, which all run when the analysis stops on a failing build.</param>
    private Selection Select(string sha, SyntheticTarget target, Dictionary<string, CoverageMap> maps, string? mapCommit, List<ProjectNode> testProjects)
    {
        // A hang in TestDetta (an exponential search, Swashbuckle 2026-10-02) would otherwise stop the
        // evaluation for good without a word; it is reported as what it is, a finding about TestDetta.
        using var stop = new CancellationTokenSource();
        using var abandon = new CancellationTokenSource();
        var analyzing = Task.Run(() => Analyze(sha, target, mapCommit, new AnalysisStop(stop.Token, abandon.Token)));
        AnalysisResult analysis;
        Dictionary<OtherSelector, OtherSelection> drift;
        try
        {
            // Wait throws a failed analysis's exception, wrapped.
            if (!analyzing.Wait(settings.AnalysisTimeout))
            {
                LogAnalysisHung(target.Member.TypeName, target.Member.MemberName, settings.AnalysisTimeout.TotalMinutes);
                var still = !Stopped(analyzing, stop, abandon);
                throw new InvalidOperationException(
                    $"TestDetta's analysis of a change to {target.Member.TypeName}.{target.Member.MemberName} did not finish within " +
                    $"{settings.AnalysisTimeout.TotalMinutes:0.#} minute(s): a hang or a slowdown in TestDetta." +
                    (still ? $" It did not stop within {AbandonGrace.TotalMinutes:0} more minute(s) either and is left running; the clone keeps its edit." : string.Empty));
            }

            (analysis, drift) = analyzing.Result;
        }
        catch (AggregateException ex) when (ex.InnerException is GeneratedCodeBuildException stopped)
        {
            // A generator mutant the consumers do not compile with: td test stops on the build, as dotnet
            // test would fail on it, so nothing can be missed; judged like running every test project.
            var lines = stopped.Message.Split('\n', StringSplitOptions.TrimEntries);
            var error = Array.Find(lines, static l => l.Contains(": error ", StringComparison.Ordinal)) ?? lines[0];
            LogStoppedOnBuild(target.Member.TypeName, target.Member.MemberName, error);

            List<TestProjectSelection> all = [.. testProjects.Select(static p => new TestProjectSelection(p, TestRunMode.All, [], "td test stops: the build fails"))];
            var share = SelectedShare(all, maps);
            return new Selection(all, Detected: true, share, new Dictionary<OtherSelector, OtherSelection> { [OtherSelector.ProjectLevel] = new(all, share) }) { StoppedOnBuild = true };
        }

        // A data file is seen when TestDetta runs something for it: its readers.
        var detected = target.Kind == MutantKind.EmptyFile
            ? analysis.Tests.Any(static t => t.Mode != TestRunMode.Skipped)
            : analysis.Impact.CodeChanges.Any(c => c.AffectsWholeProject || c.Symbols.Any(s => s.TypeName == target.Member.TypeName));
        return new Selection(
            analysis.Tests,
            detected,
            SelectedShare(analysis, maps),
            drift.ToDictionary(static d => d.Key, d => d.Value with { Share = SelectedShare(d.Value.Tests, maps) }));
    }

    /// <summary>
    /// Longest an analysis that ran past its timeout is waited for after it was asked to stop: long enough for the
    /// step it is in (an MSBuild evaluation, a build of a generator's consumer) to end on a loaded machine.
    /// </summary>
    private static readonly TimeSpan AbandonGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How an analysis that ran past its timeout is ended. <see cref="Stop"/> asks it to stop at its next step; it then
    /// restores the sources and removes the maps as usual, and the evaluation waits for that before it reports the
    /// timeout, so nothing writes into the clone (or a folder being deleted) after the evaluation moved on.
    /// <see cref="Abandon"/> says the evaluation gave up waiting: from then on the analysis writes nothing at all.
    /// </summary>
    /// <param name="Stop">Cancelled when the analysis should stop.</param>
    /// <param name="Abandon">Cancelled when nobody waits for it any more.</param>
    private sealed record AnalysisStop(CancellationToken Stop, CancellationToken Abandon);

    /// <summary>
    /// Asks an analysis that ran past its timeout to stop and waits up to <see cref="AbandonGrace"/> for it; true when it
    /// ended (with a result, an error or its cancellation), false when it is abandoned.
    /// </summary>
    /// <param name="analyzing">The analysis.</param>
    /// <param name="stop">Asks it to stop.</param>
    /// <param name="abandon">Tells it nobody waits any more.</param>
    private static bool Stopped(Task analyzing, CancellationTokenSource stop, CancellationTokenSource abandon)
    {
        stop.Cancel();
        try
        {
            if (analyzing.Wait(AbandonGrace))
            {
                return true;
            }
        }
        catch (AggregateException)
        {
            // It ended by throwing, most likely its cancellation: it has stopped either way.
            return true;
        }

        abandon.Cancel();
        return false;
    }

    /// <summary>The classes among <paramref name="failed"/> (<c>Project: Class</c>) that <paramref name="tests"/> left out.</summary>
    /// <param name="tests">A selection.</param>
    /// <param name="failed">Confirmed failing classes.</param>
    /// <param name="projects">Test project paths by file name, to resolve <c>Project</c>.</param>
    private static List<string> LeftOut(IReadOnlyList<TestProjectSelection> tests, List<string> failed, Dictionary<string, string> projects) =>
        failed.FindAll(c =>
        {
            var separator = c.IndexOf(": ", StringComparison.Ordinal);
            return !EvalSteps.IsSelected(tests, projects[c[..separator]], c[(separator + 2)..]);
        });

    /// <summary>Each drift mode's misses among the confirmed failures some selection left out.</summary>
    /// <param name="state">The mutant's selections.</param>
    /// <param name="confirmed">Confirmed failing classes (<c>Project: Class</c>).</param>
    /// <param name="projects">Test project paths by file name.</param>
    private static Dictionary<OtherSelector, OtherOutcome> OtherOutcomes(MutantRun state, List<string> confirmed, Dictionary<string, string> projects) =>
        state.Others.ToDictionary(static d => d.Key, d => new OtherOutcome(LeftOut(d.Value.Tests, confirmed, projects), d.Value.Share)
        {
            FullRun = d.Value.FullRun,
            Seconds = d.Value.Seconds,
        });

    /// <summary>Names of the evaluated test projects, sorted, as an outcome stores them.</summary>
    /// <param name="testProjects">Test projects the evaluation runs.</param>
    private static List<string> Evaluated(List<ProjectNode> testProjects) =>
        [.. testProjects.Select(static p => Path.GetFileNameWithoutExtension(p.Path)).Order(StringComparer.Ordinal)];

    /// <summary>Test project paths by file name without extension, the <c>Project</c> part of <c>Project: Class</c>.</summary>
    /// <param name="reached">Test projects of the mutant.</param>
    private static Dictionary<string, string> ProjectsByName(List<ProjectNode> reached) =>
        reached.ToDictionary(static p => Path.GetFileNameWithoutExtension(p.Path), static p => p.Path, StringComparer.Ordinal);

    /// <summary>Runs the tests against one mutant and judges the failures against TestDetta's selection (<see cref="MutantJudge"/>).</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="context">What the mutants of the commit share.</param>
    /// <param name="index">1-based number of the mutant, for log files and the schema switch.</param>
    /// <param name="selection">What TestDetta selected for the change.</param>
    /// <param name="executor">Runs the tests, in the worker's copy of the clone.</param>
    private SyntheticMutant Run(SyntheticTarget target, MutantContext context, int index, Selection selection, TestExecutor executor)
    {
        var (tests, detected, share, drift) = selection;
        var state = new MutantRun(tests, context.TestProjects, index, detected, share) { Others = drift };
        using var mutation = Apply(target, context, index, executor);
        if (mutation.MutatedMethods == 0)
        {
            LogNotFound(target.Member.TypeName, target.Member.MemberName);
            Store(target, new MutantOutcome(0, Evaluated(context.TestProjects)));
            return new SyntheticMutant(target, detected, 0, [], [], share, Inconclusive: false);
        }

        // Only a test project whose output holds a mutated copy can fail because of the mutant.
        var reached = context.TestProjects
            .Where(p => workspace.Executor.Outputs.TryGetValue(p.Path, out var output) && mutation.Outputs.Contains(output))
            .ToList();

        // A generator mutant also reaches the test projects that see what the generator writes, built with
        // the mutant on in a copy of the worker's clone; one that does not build fails as a whole
        // (docs/design/generator-mutants.md). Control runs without the mutant stay in the worker's clone.
        using var compiled = _generatorTests.TryGetValue(target, out var seeing) ? CompileConsumers(target, seeing, index, mutation, executor) : null;
        var notBuilt = compiled?.NotBuilt ?? [];
        if (compiled is not null)
        {
            reached = context.TestProjects.FindAll(p => (reached.Contains(p) || seeing!.Contains(p)) && !notBuilt.Contains(p));
        }

        // Runs stored for this mutant before are what they were: only the runs today's selection needs and the
        // cache lacks are made.
        var generator = _generatorTests.ContainsKey(target);
        var earlier = _cache?.Find(target) is { } found && found.MutatedMethods == mutation.MutatedMethods && (found.ConsumersCompiled || !generator)
            ? found.Runs
            : new Dictionary<string, StoredRun>(StringComparer.Ordinal);
        Store(target, new MutantOutcome(mutation.MutatedMethods, Evaluated(context.TestProjects))
        {
            Reached = [.. reached.Select(Relative)],
            NotBuilt = [.. notBuilt.Select(Relative)],
            Runs = earlier,
        });

        var scope = new MutantScope(reached, index, mutation, compiled?.Executor ?? executor) { Control = executor };
        var runs = new ExecutorRuns(this, target, scope, earlier);
        var judgement = Judge(runs, state, Judged(reached, state, context.Baseline, context.BaselineClasses), Judged(notBuilt, state, context.Baseline, context.BaselineClasses));
        return Result(target, state, mutation.MutatedMethods, judgement, [.. reached, .. notBuilt], reused: false);
    }

    /// <summary>
    /// The mutant's result from its stored runs and the current selection, without running a test: the judging is
    /// applied to the stored runs (<see cref="MutantJudge"/>). Null when it needs a run the cache does not have (today's
    /// selection leaves out other classes, a run without the mutant was never made or did not complete), a reached
    /// project is no longer evaluated, or a project evaluated now was left out when the outcome was stored; the mutant
    /// is then run, and only the missing runs are made.
    /// </summary>
    /// <param name="target">The mutant.</param>
    /// <param name="stored">What the tests did against it.</param>
    /// <param name="state">The mutant's analysis and the evaluated test projects.</param>
    /// <param name="baseline">The run without mutants the stored runs are compared with.</param>
    /// <param name="candidates">Test projects of the commit, to find a project that did not build with the mutant.</param>
    private SyntheticMutant? FromCache(SyntheticTarget target, MutantOutcome stored, MutantRun state, Baseline baseline, List<ProjectNode> candidates)
    {
        if (!EvaluatedAll(stored, state.Reached))
        {
            // Recorded while a project evaluated now was left out: the project's failures are not in the outcome.
            return null;
        }

        if (stored.MutatedMethods == 0)
        {
            LogNotFound(target.Member.TypeName, target.Member.MemberName);
            return new SyntheticMutant(target, state.Detected, 0, [], [], state.Share, Inconclusive: false) { Reused = true };
        }

        var evaluated = state.Reached.ToDictionary(Relative, StringComparer.Ordinal);
        var all = candidates.ToDictionary(Relative, StringComparer.Ordinal);
        if (stored.Reached.Exists(p => !evaluated.ContainsKey(p)) || stored.NotBuilt.Exists(p => !all.ContainsKey(p)))
        {
            return null;
        }

        var reached = stored.Reached.ConvertAll(p => evaluated[p]);
        var notBuilt = stored.NotBuilt.ConvertAll(p => all[p]);
        Judgement judgement;
        try
        {
            judgement = Judge(new StoredRuns(this, stored), state, Judged(reached, state, baseline.Outcome, baseline.ClassesByProject), Judged(notBuilt, state, baseline.Outcome, baseline.ClassesByProject));
        }
        catch (RunNeededException)
        {
            return null;
        }

        LogReused(state.Index);
        return Result(target, state, stored.MutatedMethods, judgement, [.. reached, .. notBuilt], reused: true);
    }

    /// <summary>Judges one mutant with <paramref name="runs"/>; a class another selector left out is confirmed whatever the verdict.</summary>
    /// <param name="runs">Makes or finds the runs.</param>
    /// <param name="state">The mutant's selections.</param>
    /// <param name="reached">The test projects it reaches.</param>
    /// <param name="notBuilt">Test projects that do not build with it.</param>
    private static Judgement Judge(IMutantRuns runs, MutantRun state, List<JudgedProject> reached, List<JudgedProject> notBuilt) =>
        new MutantJudge(runs, (project, testClass) => state.Others.Values.All(d => EvalSteps.IsSelected(d.Tests, project, testClass)))
            .Judge(reached, notBuilt);

    /// <summary>The projects a mutant reaches, with TestDetta's decision and the baseline the judging needs.</summary>
    /// <param name="projects">The projects.</param>
    /// <param name="state">The mutant's selections.</param>
    /// <param name="baseline">The run without mutants.</param>
    /// <param name="classes">Test classes in the baseline's report of each project.</param>
    private List<JudgedProject> Judged(List<ProjectNode> projects, MutantRun state, TestRunOutcome baseline, IReadOnlyDictionary<string, int> classes) =>
        projects.ConvertAll(p => new JudgedProject(
            p,
            Relative(p),
            state.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, p.Path, StringComparison.Ordinal)),
            baseline.FailedByProject.GetValueOrDefault(p.Path) ?? new HashSet<string>(StringComparer.Ordinal),
            classes.GetValueOrDefault(p.Path)));

    /// <summary>The mutant's result from its judgement; the other selectors are judged on the same failures when they are named by class.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="state">The mutant's selections.</param>
    /// <param name="mutatedMethods">Compiled methods the mutant changes.</param>
    /// <param name="judgement">What its runs showed.</param>
    /// <param name="projects">The projects it reached or that did not build with it, to resolve <c>Project</c> in <c>Project: Class</c>.</param>
    /// <param name="reused">True when no test ran for it now.</param>
    private SyntheticMutant Result(SyntheticTarget target, MutantRun state, int mutatedMethods, Judgement judgement, List<ProjectNode> projects, bool reused)
    {
        LogMutant(state.Index, target.Member.TypeName, target.Member.MemberName, judgement.Killed.Count, judgement.Missed.Count, state.Share);
        if (judgement.CaughtByCrash)
        {
            LogCaughtByCrash(state.Index, target.Member.TypeName, target.Member.MemberName);
        }

        if (judgement.Inconclusive)
        {
            LogInconclusive(state.Index, judgement.Note ?? string.Empty);
        }

        var byName = ProjectsByName(projects);
        return new SyntheticMutant(target, state.Detected, mutatedMethods, judgement.Killed, judgement.Missed, state.Share, judgement.Inconclusive)
        {
            CaughtByCrash = judgement.CaughtByCrash,
            Reused = reused,
            Note = judgement.Note,
            NotCounted = judgement.NotCounted,
            // A crash proves nothing about which classes another selector left out.
            Others = judgement.ByClasses && !judgement.Inconclusive
                ? OtherOutcomes(state, [.. judgement.Failures], byName)
                : [],
        };
    }

    /// <summary>Stores a test outcome, marked when the mutant's generator consumers were compiled with it.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="outcome">What the tests did.</param>
    private void Store(SyntheticTarget target, MutantOutcome outcome) =>
        _cache?.Store(target, outcome with { ConsumersCompiled = _generatorTests.ContainsKey(target) });

    /// <summary>
    /// Runs without any mutant made in this evaluation, by what they ran (<see cref="RunAsk.Key"/>), complete ones only:
    /// every mutant of a commit shares the same build, so they hold for all of them (the cache keeps them across
    /// evaluations).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProjectRun> _controls = new(StringComparer.Ordinal);

    /// <summary>A stored run without any mutant, from this evaluation or the cache; null when none is known.</summary>
    /// <param name="key">What it ran.</param>
    private ProjectRun? KnownControl(string key) =>
        _controls.TryGetValue(key, out var run) ? run : _cache?.Control(key)?.ToRun();

    /// <summary>
    /// The runs of one mutant's judgement from its stored outcome and the stored runs without any mutant; a run neither
    /// holds throws <see cref="RunNeededException"/>, so the mutant is run.
    /// </summary>
    /// <param name="evaluator">The evaluation, for the runs without any mutant.</param>
    /// <param name="stored">The mutant's stored outcome.</param>
    private sealed class StoredRuns(SyntheticEvaluator evaluator, MutantOutcome stored) : IMutantRuns
    {
        /// <inheritdoc />
        public ProjectRun Run(RunAsk ask)
        {
            var key = ask.Key;
            if (ask.WithMutant)
            {
                return stored.Runs.TryGetValue(key, out var run) ? run.ToRun() : throw new RunNeededException(ask);
            }

            return evaluator.KnownControl(key) ?? throw new RunNeededException(ask);
        }
    }

    /// <summary>
    /// The runs of one mutant's judgement, made in the worker's copy: a run stored before is taken as it is, any
    /// other is made and stored (with the mutant: in the mutant's outcome; without: for every mutant, when it completed).
    /// </summary>
    /// <param name="evaluator">The evaluation.</param>
    /// <param name="target">The mutant.</param>
    /// <param name="scope">The applied mutant and the executors of its runs.</param>
    /// <param name="stored">Runs with the mutant stored before, by key; runs made now are added.</param>
    private sealed class ExecutorRuns(SyntheticEvaluator evaluator, SyntheticTarget target, MutantScope scope, Dictionary<string, StoredRun> stored) : IMutantRuns
    {
        /// <inheritdoc />
        public ProjectRun Run(RunAsk ask)
        {
            var key = ask.Key;
            if (ask.WithMutant && stored.TryGetValue(key, out var known))
            {
                return known.ToRun();
            }

            if (!ask.WithMutant && evaluator.KnownControl(key) is { } control)
            {
                return control;
            }

            var label = ask.Label.Length == 0 ? $"synthetic-{scope.Index}" : $"synthetic-{scope.Index}-{ask.Label}";
            var invocation = evaluator.Invocation(ask);
            TestRunOutcome outcome;
            if (ask.WithMutant)
            {
                outcome = scope.Executor.RunSelected([invocation], label, scope.Mutation.Environment);
            }
            else
            {
                using (scope.Mutation.Lift())
                {
                    outcome = (scope.Control ?? scope.Executor).RunSelected([invocation], label, environment: null);
                }
            }

            var none = new HashSet<string>(StringComparer.Ordinal);
            var run = outcome.Projects.GetValueOrDefault(ask.Project.Node.Path) ?? new ProjectRun(false, "the run left no result", none, none, 0);
            if (ask.WithMutant)
            {
                var entry = StoredRun.From(run, ask.Part);
                stored[key] = entry;
                evaluator._cache?.StoreRun(target, key, entry);
            }
            else if (run.Complete)
            {
                evaluator._controls[key] = run;
                evaluator._cache?.StoreControl(key, StoredRun.From(run, ask.Part));
            }

            return run;
        }
    }

    /// <summary>The <c>dotnet test</c> invocation of a judged run: the whole project, only some classes, or all but some.</summary>
    /// <param name="ask">The run.</param>
    private TestInvocation Invocation(RunAsk ask)
    {
        var project = ask.Project.Node;
        if (ask.Part == RunPart.Except)
        {
            var location = DotnetTestLocation.For(workspace.RepoRoot, project.Path);
            var arguments = location.TestArguments(workspace.RepoRoot, project.Path);
            arguments.AddRange(TestFilter.Arguments(ask.Classes, exclude: true, TestFilter.SyntaxFor(project, location.Mode)));
            return new TestInvocation(new TestProjectSelection(project, TestRunMode.Filtered, [], "the classes TestDetta left out"), arguments, location);
        }

        var selection = ask.Part == RunPart.Whole
            ? new TestProjectSelection(project, TestRunMode.All, [], "the whole project")
            : new TestProjectSelection(project, TestRunMode.Filtered, ask.Classes, "judging a mutant");
        return TestInvocationBuilder.Build([selection], workspace.RepoRoot, [])[0];
    }

    /// <summary>
    /// Builds the test projects that see what a mutant's generator writes, with the mutant on, in a new
    /// copy of the worker's clone: each consumer, the projects after it and the test project, one by one
    /// without their dependencies (see <see cref="GeneratorMutants.BuildOrder"/>), without a compiler
    /// server, which would keep the generator it loaded before. A test project one of whose builds fails
    /// does not build.
    /// </summary>
    /// <param name="target">The generator mutant.</param>
    /// <param name="testProjects">Test projects that see what the generator writes.</param>
    /// <param name="index">The mutant's number.</param>
    /// <param name="mutation">The applied mutant, whose switch the builds get.</param>
    /// <param name="executor">The worker's executor, whose clone is copied.</param>
    private CompiledConsumers CompileConsumers(SyntheticTarget target, List<ProjectNode> testProjects, int index, ActiveMutant mutation, TestExecutor executor)
    {
        var root = executor.Root + $".generator-{index}";
        WorkerCopies.Remove(root);
        WorkerCopies.Copy(executor.Root, root);
        var compiled = new CompiledConsumers(root, executor.InCopy(root));
        try
        {
            string InCopy(string path) => Path.Combine(root, Path.GetRelativePath(workspace.RepoRoot, path));
            GeneratorMutants.Touch(_generatorAssemblies.GetValueOrDefault(target, []).Where(a => Paths.IsUnder(a, workspace.RepoRoot)).Select(InCopy).Where(File.Exists));
            var dotnet = new DotnetProcess(_logger);
            var built = new Dictionary<string, bool>(Paths.Comparer);
            var files = target.Edits.Select(static e => e.Path).ToList();
            foreach (var testProject in testProjects)
            {
                foreach (var project in _generatorMutants!.BuildOrder(files, testProject))
                {
                    if (!built.TryGetValue(project.Path, out var ok))
                    {
                        List<string> arguments = ["build", InCopy(project.Path), "-c", "Debug", "--no-dependencies", .. GeneratorMutants.NoCompilerServer];
                        if (project.IsTestProject && settings.Framework is { } framework)
                        {
                            arguments.AddRange(["-f", framework]);
                        }

                        var logPath = Path.Combine(executor.LogDirectory, $"synthetic-{index}-build-{Path.GetFileNameWithoutExtension(project.Path)}.log");
                        using (var log = File.CreateText(logPath))
                        {
                            ok = dotnet.Run(arguments, Path.GetDirectoryName(InCopy(project.Path))!, mutation.Environment ?? new Dictionary<string, string>(), log, ConsumerBuildTimeout) == 0;
                        }

                        built[project.Path] = ok;
                        if (!ok)
                        {
                            LogConsumerNotBuilt(index, project.Path, logPath);
                        }
                    }

                    if (!ok)
                    {
                        compiled.NotBuilt.Add(testProject);
                        break;
                    }
                }
            }

            return compiled;
        }
        catch
        {
            compiled.Dispose();
            throw;
        }
    }

    /// <summary>Longest a generator mutant's build of one test project may take.</summary>
    private static readonly TimeSpan ConsumerBuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A copy of a worker's clone whose generator consumers were built with one mutant on; deleted when disposed.</summary>
    /// <param name="root">Root of the copy.</param>
    /// <param name="executor">Runs the tests in the copy.</param>
    private sealed class CompiledConsumers(string root, TestExecutor executor) : IDisposable
    {
        /// <summary>Runs the tests in the copy.</summary>
        public TestExecutor Executor { get; } = executor;

        /// <summary>Test projects that did not build with the mutant.</summary>
        public List<ProjectNode> NotBuilt { get; } = [];

        /// <summary>Deletes the copy.</summary>
        public void Dispose() => WorkerCopies.Remove(root);
    }

    /// <summary>True when every project evaluated now was evaluated when <paramref name="stored"/> was recorded.</summary>
    /// <param name="stored">A stored outcome.</param>
    /// <param name="projects">Test projects evaluated now.</param>
    private static bool EvaluatedAll(MutantOutcome stored, List<ProjectNode> projects) =>
        stored.Evaluated is { } evaluated && projects.TrueForAll(p => evaluated.Contains(Path.GetFileNameWithoutExtension(p.Path), StringComparer.Ordinal));

    /// <summary>One mutant's state while it is evaluated.</summary>
    /// <param name="Tests">TestDetta's decision per affected test project.</param>
    /// <param name="Reached">Test projects the mutant can reach.</param>
    /// <param name="Index">Number of the mutant, for log files.</param>
    /// <param name="Detected">Whether TestDetta saw the change.</param>
    /// <param name="Share">Share of recorded classes TestDetta selected.</param>
    private sealed record MutantRun(IReadOnlyList<TestProjectSelection> Tests, List<ProjectNode> Reached, int Index, bool Detected, double Share)
    {
        /// <summary>What each other selector selected; empty without drift modes or dotnet-tia.</summary>
        public IReadOnlyDictionary<OtherSelector, OtherSelection> Others { get; init; } = new Dictionary<OtherSelector, OtherSelection>();
    }

    /// <summary>The mutant a miss is confirmed for.</summary>
    /// <param name="Reached">Test projects the mutant can reach.</param>
    /// <param name="Index">Number of the mutant, for log files.</param>
    /// <param name="Mutation">The applied mutant, lifted for control runs.</param>
    /// <param name="Executor">Runs the tests, in the worker's copy of the clone.</param>
    private sealed record MutantScope(List<ProjectNode> Reached, int Index, ActiveMutant Mutation, TestExecutor Executor)
    {
        /// <summary>Runs the control runs without the mutant: the worker's clone when the mutant was compiled into a copy of it.</summary>
        public TestExecutor? Control { get; init; }
    }

    /// <summary>
    /// Applies the target's source edits, lets TestDetta select against the commit's maps (and, with
    /// <paramref name="mapCommit"/>, against the older maps in every drift mode), and restores the files.
    /// </summary>
    /// <param name="sha">Commit the maps were recorded at and the base of the diff.</param>
    /// <param name="target">Member to change.</param>
    /// <param name="mapCommit">Older commit whose maps the drift modes use; null for none.</param>
    /// <param name="cancel">Stops it between its steps after the timeout; see <see cref="AnalysisStop"/>.</param>
    private (AnalysisResult Analysis, Dictionary<OtherSelector, OtherSelection> Others) Analyze(string sha, SyntheticTarget target, string? mapCommit, AnalysisStop cancel)
    {
        // Bytes, not text: a rewrite through File.WriteAllText drops a UTF-8 byte order mark, and the
        // clone would keep a change in that file for every later mutant.
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var stop = cancel.Stop;
        try
        {
            stop.ThrowIfCancellationRequested();
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

            var pipeline = new AnalysisPipeline(workspace.LoggerFactory, workspace.Evaluator);
            _steps.PlaceMaps(sha);
            // As td test does: a change that reaches a generator is built, to compare what it generates.
            var analysis = pipeline.Run(workspace.RepoRoot, sha, new AnalysisOptions { GeneratedCode = _generators?.For(target, originals), Cancellation = stop });
            var others = new Dictionary<OtherSelector, OtherSelection>
            {
                [OtherSelector.ProjectLevel] = new([.. analysis.Impact.Tests.Select(static t => new TestProjectSelection(t.Project, TestRunMode.All, [], "affected test project"))], 0),
            };
            if (mapCommit is not null)
            {
                // The measured age may exceed TestDetta's default limit; the bridge is judged on what it does with it.
                stop.ThrowIfCancellationRequested();
                _steps.PlaceMaps(mapCommit);
                others[OtherSelector.Bridge] = new(pipeline.Run(workspace.RepoRoot, sha, new AnalysisOptions { MaxMapAge = int.MaxValue, Cancellation = stop }).Tests, 0);
                others[OtherSelector.Naive] = new(pipeline.Run(workspace.RepoRoot, sha, new AnalysisOptions { Maps = MapUse.AsIfCurrent, Cancellation = stop }).Tests, 0);
                others[OtherSelector.NoMap] = new(pipeline.Run(workspace.RepoRoot, sha, new AnalysisOptions { Maps = MapUse.None, Cancellation = stop }).Tests, 0);
            }

            stop.ThrowIfCancellationRequested();
            if (settings.TiaCli is not null || settings.AffectedCli is not null)
            {
                // Maps are TestDetta's own data; the other tools see the same tree TestDetta saw, without them.
                _steps.ClearMaps();
            }

            if (settings.TiaCli is { } tia)
            {
                others[OtherSelector.Tia] = SelectWithTia(tia, sha, analysis.Graph);
            }

            if (settings.AffectedCli is { } affected)
            {
                others[OtherSelector.DotnetAffected] = SelectWithAffected(affected, analysis.Graph);
            }

            return (analysis, others);
        }
        finally
        {
            // Abandoned: the evaluation has moved on (or is deleting the clone), so nothing is written any more.
            if (!cancel.Abandon.IsCancellationRequested)
            {
                _steps.ClearMaps();
                foreach (var (path, bytes) in originals)
                {
                    File.WriteAllBytes(path, bytes);
                }
            }
        }
    }

    /// <summary>
    /// dotnet-tia's selection for the change in the clone. A failure of the tool counts as a full run
    /// with the failure as its reason, as dotnet-tia itself treats its own errors: never as a miss.
    /// </summary>
    /// <param name="tia">Path of <c>Tia.Cli.dll</c>.</param>
    /// <param name="sha">Commit the change is made on.</param>
    /// <param name="graph">TestDetta's project graph of the clone.</param>
    private OtherSelection SelectWithTia(string tia, string sha, ProjectGraph graph)
    {
        using var log = File.AppendText(Path.Combine(workspace.Executor.LogDirectory, "dotnet-tia.log"));
        try
        {
            var result = TiaSelector.Analyze(tia, workspace.RepoRoot, sha, settings.TiaSolution, graph, log);
            return new OtherSelection(result.Tests, 0) { FullRun = result.Reason ?? (result.FullRun ? "full run" : null), Seconds = result.Seconds };
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine(ex.Message);
            var all = graph.Projects.Where(static p => p.IsTestProject)
                .Select(p => new TestProjectSelection(p, TestRunMode.All, [], "dotnet-tia failed")).ToList();
            return new OtherSelection(all, 0) { FullRun = $"dotnet-tia failed: {ex.Message}" };
        }
    }

    /// <summary>dotnet-affected's selection for the change in the clone; a failure counts as a full run, never as a miss.</summary>
    /// <param name="affected">The <c>dotnet-affected</c> executable.</param>
    /// <param name="graph">TestDetta's project graph of the clone.</param>
    private OtherSelection SelectWithAffected(string affected, ProjectGraph graph)
    {
        using var log = File.AppendText(Path.Combine(workspace.Executor.LogDirectory, "dotnet-affected.log"));
        try
        {
            return new OtherSelection(AffectedSelector.Analyze(affected, workspace.RepoRoot, graph, log), 0);
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine(ex.Message);
            var all = graph.Projects.Where(static p => p.IsTestProject)
                .Select(p => new TestProjectSelection(p, TestRunMode.All, [], "dotnet-affected failed")).ToList();
            return new OtherSelection(all, 0) { FullRun = $"dotnet-affected failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Share of recorded test classes that TestDetta's selection runs: 1 when every test project runs
    /// completely, 0 when nothing runs. Projects without a map are not counted.
    /// </summary>
    /// <param name="analysis">TestDetta's analysis.</param>
    /// <param name="maps">Maps by normalized test project path.</param>
    private static double SelectedShare(AnalysisResult analysis, Dictionary<string, CoverageMap> maps) => SelectedShare(analysis.Tests, maps);

    /// <summary>See <see cref="SelectedShare(AnalysisResult, Dictionary{string, CoverageMap})"/>, for any selector's decisions.</summary>
    /// <param name="tests">Decisions per test project.</param>
    /// <param name="maps">Maps by normalized test project path.</param>
    private static double SelectedShare(IReadOnlyList<TestProjectSelection> tests, Dictionary<string, CoverageMap> maps) => Selections.SelectedShare(tests, maps);

    /// <summary>Every member that ran in any test class, fixture or start-up of any map.</summary>
    /// <param name="maps">Maps of the commit.</param>
    private static HashSet<CoveredMethod> CoveredMembers(Dictionary<string, CoverageMap> maps) => Selections.CoveredMembers(maps.Values);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Mutant {Index} {TypeName}.{MemberName}: a part TestDetta ran crashed or hung only with the mutant, twice; caught")]
    private partial void LogCaughtByCrash(int index, string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mutant {Index}: inconclusive: {Note}")]
    private partial void LogInconclusive(int index, string note);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sampled {Count} member(s), {Covered} of them covered")]
    private partial void LogSampled(int count, int covered);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Mutant {Index} {TypeName}.{MemberName}: {Killed} class(es) failed, {Missed} not selected, {Share:P0} of classes selected")]
    private partial void LogMutant(int index, string typeName, string memberName, int killed, int missed, double share);



    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName}.{MemberName} is in no test output; not run")]
    private partial void LogNotFound(string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Sha} could not be evaluated synthetically")]
    private partial void LogFailed(string sha, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reusing the stored run without mutants of {Count} test project(s); the build waits until a mutant has to run")]
    private partial void LogBaselineReused(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reusing {Count} stored mutant outcome(s); only the selection is computed for them")]
    private partial void LogCacheOpened(int count);


    [LoggerMessage(Level = LogLevel.Information, Message = "Mutant {Index}: test outcome reused from the cache")]
    private partial void LogReused(int index);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema: {Count} mutant(s) written in behind a switch; the full suite behaves the same with it")]
    private partial void LogSchema(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schema: the full suite fails differently with the switches written in; every mutant is written in alone instead")]
    private partial void LogSchemaChangedBehaviour();

    [LoggerMessage(Level = LogLevel.Error, Message = "TestDetta's analysis of a change to {TypeName}.{MemberName} did not finish within {Minutes} minute(s)")]
    private partial void LogAnalysisHung(string typeName, string memberName, double minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} mutants run at once: that many runs of the full suite at the same time fail as the baseline does")]
    private partial void LogParallel(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The clone could not be copied for each worker; mutants run one at a time")]
    private partial void LogNoCopies(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} runs of the full suite at the same time fail differently from the baseline; mutants run one at a time")]
    private partial void LogParallelInterferes(int count);
}
