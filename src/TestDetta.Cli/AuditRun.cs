using System.Reflection;
using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Mutants;
using static TestDetta.Cli.TestRunArguments;

namespace TestDetta.Cli;

/// <summary>
/// <c>td audit</c> (docs/design/audit.md): the corpus evaluation's method on the team's own repository. Members of the
/// team's library code are broken one at a time by the evaluator's IL mutants, in a copy of the working tree (the
/// team's sources and build output are only read); every audited test project that can load a change runs completely;
/// and TestDetta's selection for that change, made as <c>td test</c> makes it from the team's maps, must contain every
/// class that broke. Needs no license and makes no network call: like shadow mode it skips nothing.
/// </summary>
/// <param name="loggerFactory">Creates the loggers.</param>
/// <param name="stdout">Receives progress and the report.</param>
/// <param name="hooks">Test hooks.</param>
/// <param name="cancel">Cancels the audit (Ctrl+C, SIGTERM): the running tests are killed and the work folder deleted.</param>
internal sealed partial class AuditRun(ILoggerFactory loggerFactory, TextWriter stdout, AuditHooks hooks, CancellationToken cancel)
{
    /// <summary>Exit code with no miss.</summary>
    private const int Success = 0;

    /// <summary>Exit code when the audit could not run, failed or was cancelled.</summary>
    private const int Failure = 1;

    /// <summary>Exit code with at least one miss.</summary>
    private const int Missed = 3;

    private readonly ILogger _logger = loggerFactory.CreateLogger<AuditRun>();
    private readonly GitClient _git = new(loggerFactory.CreateLogger<GitClient>());

    /// <summary>Audits and reports.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <returns>0 without a miss, 3 with one, 1 when the audit could not run, failed or was cancelled.</returns>
    public int Run(CliOptions options)
    {
        var budget = new ShadowBudget(options.Audit.Budget);
        string root;
        try
        {
            root = _git.TopLevel(options.Analysis.RepoPath);
        }
        catch (InvalidOperationException ex)
        {
            stdout.WriteLine($"td audit: {ex.Message}");
            return Failure;
        }

        var state = new State(root);
        try
        {
            Audit(options, state, budget);
        }
        catch (OperationCanceledException)
        {
            state.Stop ??= "cancelled";
        }
        catch (AuditRefused refused)
        {
            state.Stop = "error";
            state.Error = refused.Message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or System.ComponentModel.Win32Exception or KeyNotFoundException or AuditTreeChangedException)
        {
            LogFailed(ex);
            state.Stop = "error";
            state.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        // The report first: a cancelled CI job may be killed while the copy is deleted, which takes long for a big tree.
        var result = Result(options, state, budget);
        var path = ReportPath(options, root);
        WriteJson(options, result, path, announce: false);
        WriteSummary(options, result);
        state.Workspace?.Dispose();
        result = result with { LeftBehind = state.Workspace?.LeftBehind, WorkFolderRemoved = state.Workspace is not null && state.Workspace.LeftBehind is null };
        AuditReport.WriteConsole(result, options, stdout);
        WriteJson(options, result, path, announce: true);
        return result.Missed > 0 ? Missed : result.StopReason is "error" or "cancelled" ? Failure : Success;
    }

    /// <summary>The result of what the audit found, however it ended.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="state">What was found.</param>
    /// <param name="budget">The time budget, for the wall time.</param>
    private AuditResult Result(CliOptions options, State state, ShadowBudget budget) =>
        new(state.Root, state.Stop ?? "sample", state.Mutants, state.Projects, options.Audit, Reproduce(options, state))
        {
            Error = state.Error,
            Sample = state.Sample,
            TestArguments = options.Test.DotnetTestArguments,
            Seconds = budget.Spent.TotalSeconds,
            BaselineSeconds = state.BaselineSeconds,
            WorkFolder = state.Workspace?.Folder,
            Parallelism = options.Audit.Parallelism,
        };

    /// <summary>What the audit has found so far; the report is written from it however the audit ends.</summary>
    /// <param name="root">The team's repository root.</param>
    private sealed class State(string root)
    {
        /// <summary>The team's repository root.</summary>
        public string Root { get; } = root;

        /// <summary>The work folder, once made.</summary>
        public AuditWorkspace? Workspace { get; set; }

        /// <summary>The project graph, once loaded.</summary>
        public ProjectGraph? Graph { get; set; }

        /// <summary>The commit audited, recorded before anything else; every change is compared with it.</summary>
        public string? Head { get; set; }

        /// <summary>Why the audit stopped, once it did.</summary>
        public string? Stop { get; set; }

        /// <summary>Why it could not run or failed.</summary>
        public string? Error { get; set; }

        /// <summary>The sample, once made.</summary>
        public AuditSampleInfo? Sample { get; set; }

        /// <summary>Every test project and whether it is audited.</summary>
        public List<AuditProjectInfo> Projects { get; set; } = [];

        /// <summary>The changes judged so far.</summary>
        public List<AuditMutant> Mutants { get; set; } = [];

        /// <summary>Wall time of the baseline.</summary>
        public double BaselineSeconds { get; set; }
    }

    /// <summary>A reason the audit does not run, for the user.</summary>
    /// <param name="message">The reason.</param>
    private sealed class AuditRefused(string message) : Exception(message);

    /// <summary>The audit's steps: checks, sample, working copy, baseline, the changes.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="state">Receives what is found.</param>
    /// <param name="budget">The time budget.</param>
    private void Audit(CliOptions options, State state, ShadowBudget budget)
    {
        var root = state.Root;
        var audit = options.Audit;

        // The commit is fixed now: every analysis compares with it, and stops the audit when the repository moved.
        var head = _git.Head(root);
        state.Head = head;

        // Uncommitted changes would join every change's diff against HEAD and widen every selection.
        var dirty = _git.ChangedFiles(root, head).Where(static f => !f.StartsWith(".testdetta/", StringComparison.Ordinal)).ToList();
        if (dirty.Count > 0)
        {
            throw new AuditRefused(
                $"the working tree has changes ({string.Join(", ", dirty.Take(5))}{(dirty.Count > 5 ? ", …" : string.Empty)}); every change is compared with HEAD, " +
                "so commit or stash them first (files git ignores, such as bin/ and obj/, do not count)");
        }

        if (options.Analysis.MapsDir is { } mapsDir)
        {
            MapDirectory.Restore(Path.GetFullPath(mapsDir), root, "HEAD", options.Analysis.MaxMapAge, _git, _logger);
        }

        var graph = new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>(), new TestDetta.Coverage.MsBuildProjectEvaluator(loggerFactory)).Load(root);
        state.Graph = graph;
        var maps = new Dictionary<string, CoverageMap>(Paths.Comparer);
        foreach (var project in graph.Projects.Where(static p => p.IsTestProject))
        {
            if (TryReadMap(root, project) is { } map)
            {
                maps[project.Path] = map;
            }
        }

        var sample = Sample(graph, root, maps, audit, state);
        cancel.ThrowIfCancellationRequested();
        stdout.WriteLine($"td audit: {sample.Count} member(s) in the sample (seed {audit.Seed}); copying the working tree");
        stdout.Flush();
        state.Workspace = AuditWorkspace.Create(root, audit.WorkDirectory ?? AuditWorkspace.DefaultParent);
        var workspace = state.Workspace;
        var runner = new AuditTestRun(workspace, cancel);
        var copy = workspace.Copies[0];

        // Before anything builds or runs in the copy: everything it writes must stay inside it.
        var paths = WritePaths(graph, workspace, options.Test.DotnetTestArguments);
        foreach (var folder in paths.Where(static p => p.Kind is not ("TargetPath" or "DocumentationFile")).Select(static p => p.Path).Distinct(Paths.Comparer))
        {
            AuditWorkspace.MaterializeLinks(folder, copy);
        }

        var started = budget.Spent;
        var projects = Baseline(new BaselineInput(graph, maps, workspace, runner, options.Test.DotnetTestArguments, paths), state, budget);
        state.BaselineSeconds = (budget.Spent - started).TotalSeconds;
        if (budget.RanOut)
        {
            state.Stop = "budget";
            return;
        }

        if (projects.Count == 0)
        {
            throw new AuditRefused("no test project wrote a test report TestDetta can read in the run without changes; see the projects in the report");
        }

        // Only the repository's own builds of its assemblies, and only inside the copy: an output outside it would be the team's own.
        var outputs = projects.SelectMany(p => p.Outputs.Select(o => Path.Combine(copy, o))).Distinct(Paths.Comparer).ToList();
        var assemblies = RepositoryAssemblies.Find(new TestDetta.Coverage.ProjectAssemblies(_logger).FileNames(graph, root), outputs);
        List<string> relative = [.. assemblies.Select(a => Path.GetRelativePath(copy, a).Replace('\\', '/'))];
        Detach(copy, relative);
        var workers = new List<AuditWorker> { Worker(workspace, copy, 1, audit.Parallelism, hooks.Environment) };
        for (var k = 2; k <= Math.Min(audit.Parallelism, sample.Count); k++)
        {
            cancel.ThrowIfCancellationRequested();
            var added = workspace.AddCopy(copy);
            Detach(added, relative);
            workers.Add(Worker(workspace, added, k, audit.Parallelism, hooks.Environment));
        }

        var context = new AuditContext(workspace, projects, relative, options.Test.DotnetTestArguments, new AuditSelector(root, head, options.Analysis, loggerFactory, cancel), maps)
        {
            Parallelism = workers.Count,
        };
        var trial = new AuditTrial(context, hooks, cancel);
        stdout.WriteLine($"td audit: {projects.Count} test project(s) audited; testing changes until {audit.TargetBroken} broke tests" +
                         (audit.MaxMutants is { } max ? $", {max} ran" : string.Empty) + $" or {Durations.DescribeLimit(audit.Budget)} passed");
        stdout.Flush();
        var (mutants, stop, failure) = AuditLoop.Run(
            sample,
            workers.Count,
            new AuditStopRules(audit.TargetBroken, audit.MaxMutants, budget) { Healthy = workspace.LowSpace },
            (worker, index, target) => trial.Run(workers[worker], index, target),
            mutant => Progress(mutant, state),
            cancel);
        state.Mutants = mutants;
        state.Stop = stop;
        if (failure is not null)
        {
            LogFailed(failure);
            state.Error = $"{failure.GetType().Name}: {failure.Message}";
        }
    }

    /// <summary>
    /// Where every project of the copy writes, asked from MSBuild before anything builds or runs there; the audit is
    /// refused when any of it, or a path option of the user's arguments, lies outside the copy.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="workspace">The work folder.</param>
    /// <param name="userArguments">The user's <c>dotnet test</c> arguments.</param>
    /// <exception cref="AuditRefused">Something would be written outside the copy.</exception>
    private List<AuditOutputPath> WritePaths(ProjectGraph graph, AuditWorkspace workspace, IReadOnlyList<string> userArguments)
    {
        var copy = workspace.Copies[0];
        string Relative(string path) => Path.GetRelativePath(copy, path).Replace('\\', '/');
        foreach (var project in graph.Projects.Where(static p => p.IsTestProject))
        {
            var runDirectory = DotnetTestLocation.For(copy, workspace.InCopy(copy, project.Path)).FullDirectory(copy);
            if (AuditOutputPaths.OutsideOption(copy, runDirectory, userArguments) is { } option)
            {
                throw new AuditRefused(option);
            }
        }

        var projects = graph.Projects.Where(static p => p.SdkStyle).Select(p => workspace.InCopy(copy, p.Path)).ToList();
        var tests = graph.Projects.Where(static p => p.IsTestProject && p.SdkStyle).Select(p => workspace.InCopy(copy, p.Path)).ToHashSet(Paths.Comparer);
        var paths = AuditOutputPaths.Query(new AuditPathRequest(copy, projects, tests, userArguments), workspace.Scratch("paths"), cancel);
        return AuditOutputPaths.Outside(copy, projects, paths, Relative) is { } outside ? throw new AuditRefused(outside) : paths;
    }

    /// <summary>
    /// Makes every assembly the changes rewrite, and its symbol file, a file of the copy's own (<see cref="AuditWorkspace.Detach"/>)
    /// and checks it lies inside the copy once links are resolved: the mutant is written in place.
    /// </summary>
    /// <param name="copy">Root of a working copy.</param>
    /// <param name="assemblies">The assemblies, relative to the copy's root.</param>
    /// <exception cref="AuditRefused">One lies outside the copy.</exception>
    private static void Detach(string copy, IReadOnlyList<string> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var full = Path.Combine(copy, assembly);
            if (!Paths.IsUnder(Paths.ResolveLinks(Path.GetDirectoryName(full)!), copy))
            {
                throw new AuditRefused($"{full} lies outside the working copy once its links are resolved; td audit rewrites nothing outside it");
            }

            foreach (var file in (string[])[full, Path.ChangeExtension(full, ".pdb")])
            {
                if (File.Exists(file))
                {
                    AuditWorkspace.Detach(file);
                }
            }
        }
    }

    /// <summary>
    /// The sample: the evaluator's candidates in their uniform order (coverage not used), without members that a source
    /// generator or analyzer project compiles, and with <c>--member</c> only those.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="root">Repository root.</param>
    /// <param name="maps">Coverage maps, for the covered flag only.</param>
    /// <param name="audit">The audit's options.</param>
    /// <param name="state">Receives the sample's description.</param>
    private static List<SyntheticTarget> Sample(ProjectGraph graph, string root, Dictionary<string, CoverageMap> maps, AuditSettings audit, State state)
    {
        var all = TargetSampler.Sample(graph, root, Selections.CoveredMembers(maps.Values), new SampleRequest(int.MaxValue, audit.Seed) { Kinds = audit.Kinds, Uniform = true });
        var generators = graph.Projects.SelectMany(static p => p.GeneratorReferences).ToHashSet(Paths.Comparer);
        var sample = all.Where(t => !t.Edits.Any(e => graph.ResolveFile(Paths.Normalize(e.Path)).Any(o => o.Role == FileRole.Source && generators.Contains(o.Project.Path)))).ToList();
        state.Sample = new AuditSampleInfo(audit.Seed, audit.KindsText, sample.Count, sample.Count(static t => t.Covered), all.Count - sample.Count) { Members = audit.Members };
        if (audit.Members.Count == 0)
        {
            return sample;
        }

        var wanted = audit.Members.ToHashSet(StringComparer.Ordinal);
        var chosen = sample.FindAll(t => wanted.Contains($"{t.Member.TypeName}.{t.Member.MemberName}"));
        return chosen.Count > 0
            ? chosen
            : throw new AuditRefused($"no member of the sample is named {string.Join(" or ", audit.Members)} (with --kinds {audit.KindsText})");
    }

    /// <summary>What the baseline needs.</summary>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="Maps">Coverage maps, for the map commit of each project.</param>
    /// <param name="Workspace">The work folder.</param>
    /// <param name="Runner">Runs projects of the copy.</param>
    /// <param name="UserArguments">The <c>dotnet test</c> arguments.</param>
    /// <param name="Paths">Where each project of the copy writes (<see cref="WritePaths"/>).</param>
    private sealed record BaselineInput(
        ProjectGraph Graph,
        Dictionary<string, CoverageMap> Maps,
        AuditWorkspace Workspace,
        AuditTestRun Runner,
        IReadOnlyList<string> UserArguments,
        IReadOnlyList<AuditOutputPath> Paths);

    /// <summary>
    /// Runs every test project once in the copy without a change, as the team runs it (building there unless the
    /// arguments say <c>--no-build</c>), and keeps those that wrote a report of a complete run, with their failures, time
    /// and output folders. The others are listed with the reason: a legacy project that needs the full MSBuild, and one
    /// whose own filter cannot be joined with a class filter, are not run. Each build and run may take what is left of
    /// the budget; once it is spent, no further project runs.
    /// </summary>
    /// <param name="input">What the baseline needs.</param>
    /// <param name="state">Receives every project.</param>
    /// <param name="budget">The time budget.</param>
    private List<AuditProject> Baseline(BaselineInput input, State state, ShadowBudget budget)
    {
        var (graph, maps, workspace, runner, userArguments, paths) = input;
        var copy = workspace.Copies[0];
        var audited = new List<AuditProject>();
        var noBuild = BeforeRunSettings(userArguments).Contains("--no-build");
        foreach (var project in graph.Projects.Where(static p => p.IsTestProject).OrderBy(static p => p.Path, StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(workspace.Root, project.Path).Replace('\\', '/');
            var mapCommit = maps.GetValueOrDefault(project.Path)?.Commit;
            if (graph.NeedsFullMsBuild(project))
            {
                state.Projects.Add(new AuditProjectInfo(relative, false, "needs the full MSBuild (a legacy project); td audit does not run those yet") { MapCommit = mapCommit });
                continue;
            }

            var mode = DotnetTestLocation.For(copy, workspace.InCopy(copy, project.Path)).Mode;
            var syntax = TestFilter.SyntaxFor(project, mode);
            if (AuditTrial.Unjoinable(syntax, userArguments) is { } unjoinable)
            {
                state.Projects.Add(new AuditProjectInfo(relative, false, unjoinable) { MapCommit = mapCommit });
                continue;
            }

            if (budget.RanOut)
            {
                state.Projects.Add(new AuditProjectInfo(relative, false, "not run: the budget ran out before its run without changes") { MapCommit = mapCommit });
                continue;
            }

            stdout.WriteLine($"td audit: running {relative} without changes");
            stdout.Flush();
            var inCopy = workspace.InCopy(copy, project.Path);
            var outputs = paths.Where(p => p.Kind == "TargetPath" && Paths.Comparer.Equals(p.Project, inCopy))
                .Select(static p => Paths.Normalize(Path.GetDirectoryName(p.Path)!)).Distinct(Paths.Comparer).ToList();
            if (mode == DotnetTestMode.TestingPlatform && !noBuild && !runner.Build(copy, project, userArguments, budget.For(null, null).Limits.Timeout))
            {
                state.Projects.Add(new AuditProjectInfo(relative, false, budget.RanOut ? "not run: the budget ran out while it was built" : "does not build in the working copy") { MapCommit = mapCommit });
                continue;
            }

            var (limits, _) = budget.For(null, null);
            var run = runner.Run(new AuditRunRequest(copy, project, userArguments, "baseline") { NoBuild = noBuild, Timeout = limits.Timeout, CappedByBudget = limits.Timeout is not null }, outputs.FirstOrDefault());
            cancel.ThrowIfCancellationRequested();
            if (!run.Complete || run.Classes == 0)
            {
                var reason = run.OutOfBudget ? "not run: the budget ran out during its run without changes" : run.Complete ? "no test ran" : run.Problem;
                state.Projects.Add(new AuditProjectInfo(relative, false, reason) { MapCommit = mapCommit, BaselineSeconds = run.Seconds });
                continue;
            }

            // The output folders as they are now, links resolved, so they compare with the folders of rewritten assemblies.
            outputs = [.. outputs.Select(static o => Directory.Exists(o) ? Paths.Normalize(AuditWorkspace.RealPath(o)) : o)];
            if (outputs.FirstOrDefault(o => !Paths.IsUnder(o, copy)) is { } outside)
            {
                throw new AuditRefused($"the build output of {relative} ({outside}) lies outside the working copy, so the copy cannot keep it apart; td audit rewrites nothing outside it");
            }

            audited.Add(new AuditProject(project, relative, mode, syntax)
            {
                BaselineFailed = run.Failed,
                BaselineSeconds = run.Seconds,
                BaselineClasses = run.Classes,
                Outputs = [.. outputs.Select(o => Path.GetRelativePath(copy, o).Replace('\\', '/'))],
            });
            state.Projects.Add(new AuditProjectInfo(relative, true, null)
            {
                BaselineSeconds = run.Seconds,
                BaselineFailed = [.. run.Failed.Order(StringComparer.Ordinal)],
                Classes = run.Classes,
                MapCommit = mapCommit,
            });
        }

        return audited;
    }

    /// <summary>A worker over a copy: with several, each gets its own temporary directory, since many suites write fixed names there.</summary>
    /// <param name="workspace">The work folder.</param>
    /// <param name="copy">The worker's copy.</param>
    /// <param name="number">Its number, from 1.</param>
    /// <param name="parallelism">Workers wanted.</param>
    /// <param name="extra">Variables of every run besides those (<see cref="AuditHooks.Environment"/>).</param>
    private static AuditWorker Worker(AuditWorkspace workspace, string copy, int number, int parallelism, IReadOnlyDictionary<string, string> extra)
    {
        var backups = workspace.Scratch($"backup-{number}");
        var variables = new Dictionary<string, string>(extra, StringComparer.Ordinal);
        if (parallelism > 1)
        {
            var temporary = workspace.Scratch($"t{number}");
            variables["TMPDIR"] = temporary + Path.DirectorySeparatorChar;
            variables["TMP"] = temporary;
            variables["TEMP"] = temporary;
        }

        return new AuditWorker(copy, backups, variables);
    }

    /// <summary>Prints one finished change and keeps it for a report written after a failure.</summary>
    /// <param name="mutant">The change.</param>
    /// <param name="state">The state that collects them.</param>
    private void Progress(AuditMutant mutant, State state)
    {
        state.Mutants.Add(mutant);
        var broke = state.Mutants.Count(static m => m.Broke);
        stdout.WriteLine($"td audit: [{broke} broke] #{mutant.Index} {mutant.Member} ({MutantKinds.Name(mutant.Target.Kind)}): {AuditReport.Name(mutant.Outcome)}" +
                         (mutant.Outcome == AuditOutcome.Missed ? $", MISSED {string.Join(", ", mutant.Broken.Where(static c => !c.Selected).Select(static c => c.Name))}" : string.Empty) +
                         (mutant.Note is { } note ? $" ({note})" : string.Empty));
        stdout.Flush();
    }

    /// <summary>The coverage map of a test project, or null when it has none that can be read.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="project">A test project.</param>
    private static CoverageMap? TryReadMap(string root, ProjectNode project)
    {
        try
        {
            return CoverageMapStore.Read(CoverageMapStore.PathFor(root, project.Path));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>What a reader needs to repeat the audit: versions, the commit audited, the maps and the command.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="state">What was found: the root, the commit recorded at the start, the project graph.</param>
    private AuditReproduction Reproduce(CliOptions options, State state)
    {
        var (root, graph) = (state.Root, state.Graph);
        var assembly = typeof(AuditRun).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        string? binary = null;
        var head = state.Head;
        var maps = new List<(string, string, string)>();
        try
        {
            binary = Sha256(assembly.Location);
            head ??= _git.Head(root);
            foreach (var project in graph?.Projects.Where(static p => p.IsTestProject) ?? [])
            {
                var path = CoverageMapStore.PathFor(root, project.Path);
                if (File.Exists(path) && TryReadMap(root, project) is { } map)
                {
                    maps.Add((Path.GetRelativePath(root, project.Path).Replace('\\', '/'), map.Commit, Sha256(path)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            LogFailed(ex);
        }

        var command = string.Join(' ', ["td audit", .. AuditReport.Options(options, mutant: null)]);
        return new AuditReproduction(version, binary, head, maps, AuditReport.Options(options, mutant: null), command);
    }

    /// <summary>The JSON report's path: <c>--report</c>, or <c>.testdetta/testdetta-audit.json</c> in the repository.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="root">Repository root.</param>
    private static string ReportPath(CliOptions options, string root) =>
        options.Audit.Report is { } given ? Path.GetFullPath(given) : Path.Combine(root, ".testdetta", "testdetta-audit.json");

    /// <summary>Writes the JSON report; written once before the work folder is deleted and once after, with the outcome of that.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="result">The result.</param>
    /// <param name="path">The file.</param>
    /// <param name="announce">True to say on the console where it went (the last write).</param>
    private void WriteJson(CliOptions options, AuditResult result, string path, bool announce)
    {
        try
        {
            AuditReport.WriteJson(result, options, path);
            if (announce)
            {
                stdout.WriteLine($"td audit: report written to {path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (announce)
            {
                stdout.WriteLine($"td audit: could not write {path}: {ex.Message}");
            }
        }
    }

    /// <summary>Appends the Markdown summary when asked, before the work folder is deleted.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="result">The result.</param>
    private void WriteSummary(CliOptions options, AuditResult result)
    {
        if (options.Test.SummaryFile is not { } summary)
        {
            return;
        }

        try
        {
            using var writer = File.AppendText(summary);
            AuditReport.WriteMarkdown(result, options, writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stdout.WriteLine($"td audit: could not write {summary}: {ex.Message}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit: a step failed")]
    private partial void LogFailed(Exception exception);
}
