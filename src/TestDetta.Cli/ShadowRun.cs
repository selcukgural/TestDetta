using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage;
using static TestDetta.Cli.TestRunArguments;

namespace TestDetta.Cli;

/// <summary>TestDetta's analysis for a shadow run, as <see cref="CliApp"/> made it before the full run.</summary>
/// <param name="Result">The selection, or null when the analysis failed or ran out of time.</param>
/// <param name="Error">Why there is no selection, or null.</param>
/// <param name="Seconds">How long the analysis took.</param>
/// <param name="Log">
/// What the analysis logged, held back until the full run has finished, so the team's test output comes first and in
/// one piece.
/// </param>
internal sealed record ShadowAnalysis(AnalysisResult? Result, string? Error, double Seconds, string Log);

/// <summary>Test hooks of <see cref="ShadowRun"/>.</summary>
internal sealed class ShadowHooks
{
    /// <summary>
    /// Runs a <c>dotnet test</c> in place of the real process (arguments, working directory; returns the exit code):
    /// the team's command, a project of the per-project run, or a rerun. With it, no build output is looked up.
    /// </summary>
    public Func<IReadOnlyList<string>, string, int>? Dotnet { get; init; }
}

/// <summary>
/// <c>td test --shadow</c> (docs/design/shadow-mode.md): every test the team's command runs, runs, as without TestDetta,
/// and the run is compared with what the selection would have run. The full run is the team's own <c>dotnet test</c>
/// command as given, with only a report option added, whenever its reports can be matched to test projects; otherwise
/// one <c>dotnet test</c> per test project that command runs, and the report says why. Reports are read where the run
/// wrote them and copied; nothing of the team's in its results location is moved, redirected or deleted, and the reports
/// only TestDetta's added report option made are deleted once read. A failed class the
/// selection left out is rerun before it is called a miss: alone with the change, then in its project's full-run
/// context, then on the base commit in a separate worktree. The exit code is the full run's alone: any TestDetta error
/// before that run leaves the team's plain command to run, and any error after it only costs the comparison. TestDetta
/// sends nothing anywhere. The report holds every decision: compared for the classes this job ran, listed for the
/// others.
/// </summary>
/// <param name="loggerFactory">Creates the loggers.</param>
/// <param name="stdout">Receives the test output and the report.</param>
/// <param name="stderr">Receives the analysis's held log once the full run has finished.</param>
/// <param name="hooks">Test hooks.</param>
/// <param name="cancel">Ctrl+C or SIGTERM: the running tests are killed, and the temp folder and the base worktree removed.</param>
internal sealed partial class ShadowRun(ILoggerFactory loggerFactory, TextWriter stdout, TextWriter stderr, ShadowHooks hooks, CancellationToken cancel)
{
    /// <summary>
    /// Longest one build-output lookup (<c>dotnet msbuild -getProperty</c>) of the per-project run may take, before the
    /// project it is for runs without a report of TestDetta's: a query that hangs must not keep the tests from running.
    /// </summary>
    private static readonly TimeSpan QueryLimit = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger = loggerFactory.CreateLogger<ShadowRun>();
    private readonly GitClient _git = new(loggerFactory.CreateLogger<GitClient>());
    private readonly TestRunner _runner = new(loggerFactory.CreateLogger<TestRunner>());

    /// <summary>Runs the full suite, compares, classifies and reports.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="input">The analysis made before the full run.</param>
    /// <returns>The full run's exit code: 0, or 3 when it failed; 1 when cancelled before the tests ran.</returns>
    public int Run(CliOptions options, ShadowAnalysis input)
    {
        if (cancel.IsCancellationRequested)
        {
            stdout.WriteLine("td shadow: cancelled before the tests ran.");
            return 1;
        }

        var directory = Path.GetFullPath(options.Analysis.RepoPath);
        ShadowPlan? plan = null;
        string? planError = null;
        try
        {
            plan = Plan(options, input, directory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Before any test ran: whatever failed, the team's own command still runs, as given.
            LogFailedStep(ex);
            planError = $"shadow mode could not plan the run ({ex.GetType().Name}: {ex.Message})";
        }

        if (plan is null)
        {
            return RunPlain(options, input, directory, input.Error ?? planError ?? "the repository's projects could not be read");
        }

        var state = new RunState(Path.Combine(Path.GetTempPath(), "testdetta-shadow-" + Guid.NewGuid().ToString("N")));
        try
        {
            return RunPlanned(options, input, plan, state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailedStep(ex);
            var reason = $"shadow mode failed after the tests ran ({ex.GetType().Name}: {ex.Message})";
            if (state.ExitCode is not { } exitCode)
            {
                // Nothing of the team's command has run, or not all of it: it runs now, as given.
                return RunPlain(options, input, directory, $"shadow mode failed before the tests ran ({ex.GetType().Name}: {ex.Message})");
            }

            ReleaseLog(input, state);
            var result = new ShadowResult(false, reason, [], Reproduce(options, plan.Root, plan.Graph), input.Seconds)
            {
                Run = state.Info,
                AnalysisTimeout = (options.Test.ShadowLimits ?? ShadowLimits.Default).Analysis,
            };
            TryDo(() => Report(options, result, plan.Root));
            return exitCode == 0 ? 0 : 3;
        }
        finally
        {
            CleanUp(plan.Root, state);
        }
    }

    /// <summary>What the run has done so far: what a failure or a cancellation has to keep and clean up.</summary>
    /// <param name="work">TestDetta's own temp folder: the reports it copied and the reruns' results.</param>
    private sealed class RunState(string work)
    {
        /// <summary>TestDetta's own temp folder, deleted at the end.</summary>
        public string Work { get; } = work;

        /// <summary>True once the first test process of the full run has started.</summary>
        public bool Started { get; set; }

        /// <summary>The full run's exit code, once all of it has run.</summary>
        public int? ExitCode { get; set; }

        /// <summary>How the full run was made, once it has run.</summary>
        public ShadowRunInfo? Info { get; set; }

        /// <summary>The base commit's worktree, from the moment its checkout starts, so even a half-made one is removed.</summary>
        public string? BaseTree { get; set; }

        /// <summary>Why the base commit could not be checked out; it is then not tried again for the next project.</summary>
        public string? BaseError { get; set; }

        /// <summary>True once the analysis's log was written or declined, so it is not written twice.</summary>
        public bool LogDone { get; set; }
    }

    /// <summary>
    /// Removes what TestDetta made: the base worktree (and, when git cannot remove it, its folder and its entry under
    /// <c>.git/worktrees</c>) and the temp folder. Runs after a failure and after Ctrl+C or SIGTERM too.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="state">What was made.</param>
    private void CleanUp(string root, RunState state)
    {
        if (state.BaseTree is { } tree)
        {
            _git.DiscardWorktree(root, tree);
        }

        if (Directory.Exists(state.Work))
        {
            TryDo(() => Directory.Delete(state.Work, recursive: true));
        }
    }

    /// <summary>
    /// The team's command as given, without any report, when shadow mode cannot plan the run (the projects cannot be
    /// read, a step before the tests failed): every test runs, the job's result is that run's, and the report says why
    /// there is no shadow result.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="input">The analysis.</param>
    /// <param name="directory">The team's working directory.</param>
    /// <param name="reason">Why there is no shadow result.</param>
    private int RunPlain(CliOptions options, ShadowAnalysis input, string directory, string reason)
    {
        var userArguments = options.Test.DotnetTestArguments;
        stdout.Flush();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int exitCode;
        try
        {
            exitCode = RunTeam(["test", .. userArguments], directory);
        }
        catch (InvalidOperationException ex)
        {
            // dotnet itself could not start: the team's own step would have failed the same way.
            stdout.WriteLine($"td shadow: {ex.Message}");
            exitCode = 1;
        }

        var root = Try(() => _git.TopLevel(options.Analysis.RepoPath));
        ReleaseLog(input, new RunState(string.Empty));
        var run = new ShadowRunInfo(ShadowRunKind.Plain, "dotnet " + CommandLine(["test", .. userArguments]), null, stopwatch.Elapsed.TotalSeconds, exitCode);
        var result = new ShadowResult(false, reason, [], Reproduce(options, root, null), input.Seconds)
        {
            Run = run,
            AnalysisTimeout = (options.Test.ShadowLimits ?? ShadowLimits.Default).Analysis,
        };
        TryDo(() => Report(options, result, root ?? directory));
        return exitCode == 0 ? 0 : 3;
    }

    /// <summary>The full run, the comparison, the classification and the report, once the run could be planned.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="input">The analysis.</param>
    /// <param name="plan">How the full run is made.</param>
    /// <param name="state">What has been done; read by the caller after a failure.</param>
    private int RunPlanned(CliOptions options, ShadowAnalysis input, ShadowPlan plan, RunState state)
    {
        var analysis = input.Result;
        var full = plan.Verbatim.Reason is { } reason ? RunPerProject(plan, state, reason) : RunVerbatim(plan, state);
        var compared = analysis is not null && !cancel.IsCancellationRequested;
        var projects = full.Runs.Select(r => Compare(plan.Root, analysis, r, full.Info.ExitCode)).ToList();

        ReleaseLog(input, state);
        ShadowClassification? classification = null;
        if (compared && options.Test.ShadowClassify)
        {
            // The budget starts after the full run: it limits what TestDetta adds, never the team's own run.
            var limits = options.Test.ShadowLimits ?? ShadowLimits.Default;
            var budget = new ShadowBudget(limits.Budget);
            var context = new Classification(plan, state, Try(() => _git.MergeBase(plan.Root, options.Analysis.BaseRef)), limits, budget);
            for (var i = 0; i < projects.Count; i++)
            {
                var project = projects[i];
                projects[i] = Try(() => Classify(context, full.Runs[i], project))
                              ?? Apply(project, project.Classes.Where(static c => c.Verdict == ShadowVerdict.Miss).ToDictionary(static c => c.Name, static _ => (ShadowVerdict.Unclassified, "its reruns failed in TestDetta"), StringComparer.Ordinal));
            }

            classification = new ShadowClassification(limits, budget.Spent.TotalSeconds, budget.CutShort);
        }
        else if (compared)
        {
            projects = [.. projects.Select(static p => p with
            {
                Classes = [.. p.Classes.Select(static c => c.Verdict == ShadowVerdict.Miss ? c with { Verdict = ShadowVerdict.Unclassified, Note = "classification switched off" } : c)],
            })];
        }

        var noResult = compared ? null : cancel.IsCancellationRequested ? "the run was cancelled" : input.Error;
        var shadow = new ShadowResult(compared, noResult, projects, Reproduce(options, plan.Root, plan.Graph), input.Seconds)
        {
            ClassificationOff = !options.Test.ShadowClassify,
            Classification = classification,
            Run = full.Info,
            AnalysisTimeout = (options.Test.ShadowLimits ?? ShadowLimits.Default).Analysis,
        };
        Report(options, shadow, plan.Root);
        return full.Info.ExitCode == 0 ? 0 : 3;
    }

    /// <summary>
    /// Writes the analysis's held log to stderr, once. It is held while the team's tests run so that their output comes
    /// first and in one piece, and the analysis's lines follow next to the report.
    /// </summary>
    /// <param name="input">The analysis.</param>
    /// <param name="state">Records that it was done.</param>
    private void ReleaseLog(ShadowAnalysis input, RunState state)
    {
        if (state.LogDone || input.Log.Length == 0)
        {
            return;
        }

        state.LogDone = true;
        stderr.Write(input.Log);
    }

    /// <summary>Runs the team's command, or one <c>dotnet test</c> of the full run, in <paramref name="directory"/>.</summary>
    /// <param name="command">The arguments for dotnet.</param>
    /// <param name="directory">Working directory.</param>
    /// <exception cref="InvalidOperationException">dotnet could not be started.</exception>
    private int RunTeam(List<string> command, string directory)
    {
        stdout.Flush();
        return hooks.Dotnet is { } fake ? fake(command, directory) : _runner.RunCommand(command, directory, stdout, cancel);
    }

    /// <summary>The team's working directory, and the runner its <c>global.json</c> selects, relative to a tree's root.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="directory">The team's working directory.</param>
    /// <exception cref="InvalidDataException">The <c>global.json</c> in effect there is not valid JSON.</exception>
    private static DotnetTestLocation TeamLocation(string root, string directory)
    {
        var relative = Path.GetRelativePath(Paths.ResolveLinks(root), Paths.ResolveLinks(directory)).Replace('\\', '/');
        var inside = relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        return new DotnetTestLocation(inside ? relative : string.Empty, DotnetTestModeDetector.Detect(directory));
    }

    /// <summary>How the full run is made, decided before any test runs.</summary>
    /// <param name="Root">Repository root.</param>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="UserArguments">The user's <c>dotnet test</c> arguments.</param>
    /// <param name="RerunArguments">The user's arguments for a run of one project: without the solution or project they named.</param>
    /// <param name="Directory">The team's working directory.</param>
    /// <param name="Team">That directory relative to the root, and its runner: every run TestDetta makes starts there.</param>
    /// <param name="Scope">The test projects the team's command runs.</param>
    /// <param name="Verbatim">Whether the command runs as given, and with which report option.</param>
    private sealed record ShadowPlan(
        string Root,
        ProjectGraph Graph,
        IReadOnlyList<string> UserArguments,
        IReadOnlyList<string> RerunArguments,
        string Directory,
        DotnetTestLocation Team,
        ShadowScope Scope,
        VerbatimPlan Verbatim);

    /// <summary>The test projects the team's command runs.</summary>
    /// <param name="Projects">Those projects, in path order.</param>
    /// <param name="Known">False when the command's projects could not be told, so every test project under <paramref name="Under"/> counts.</param>
    /// <param name="Under">The folder whose test projects count when the scope is not known.</param>
    private sealed record ShadowScope(List<ProjectNode> Projects, bool Known, string Under);

    /// <summary>
    /// Plans the full run before any test runs: the repository, its projects, what the team's command names, the
    /// projects it runs, and whether it can run as given. Any exception here makes the caller run the team's plain
    /// command instead.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="input">The analysis.</param>
    /// <param name="directory">The team's working directory.</param>
    private ShadowPlan? Plan(CliOptions options, ShadowAnalysis input, string directory)
    {
        var analysis = input.Result;
        var root = analysis?.RepoRoot ?? _git.TopLevel(options.Analysis.RepoPath);
        var graph = analysis?.Graph ?? new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>(), new MsBuildProjectEvaluator(loggerFactory)).Load(root);
        var userArguments = options.Test.DotnetTestArguments;
        var target = ShadowCommand.Target(userArguments, directory);
        var scope = Scope(graph, target, directory, root);
        var team = TeamLocation(root, directory);

        // The lookups of build output before the run share the analysis's time limit, so neither keeps the tests waiting.
        var limit = (options.Test.ShadowLimits ?? ShadowLimits.Default).Analysis is { } analysisLimit
            ? analysisLimit - TimeSpan.FromSeconds(input.Seconds)
            : (TimeSpan?)null;
        var verbatim = Verbatim(new VerbatimRequest(root, graph, userArguments, directory, team, target, scope), limit);
        return new ShadowPlan(root, graph, userArguments, target.Without(userArguments), directory, team, scope, verbatim);
    }

    /// <summary>What <see cref="Verbatim"/> decides on.</summary>
    /// <param name="Root">Repository root.</param>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="UserArguments">The user's arguments.</param>
    /// <param name="Directory">The team's working directory.</param>
    /// <param name="Team">Its location and runner.</param>
    /// <param name="Target">What the command names.</param>
    /// <param name="Scope">The test projects it runs.</param>
    private sealed record VerbatimRequest(string Root, ProjectGraph Graph, IReadOnlyList<string> UserArguments, string Directory, DotnetTestLocation Team, ShadowTarget Target, ShadowScope Scope);

    /// <summary>How the team's command can run as given, or why it cannot.</summary>
    /// <param name="Reason">Why it runs per project instead; null when it can run as given.</param>
    /// <param name="Mode">Runner of the command's directory.</param>
    /// <param name="Report">What is added to the command for a report per project.</param>
    /// <param name="UserResults">The user's own results directory, searched for reports too.</param>
    /// <param name="OutputDirectories">Build output of each testing-platform test project looked up.</param>
    private sealed record VerbatimPlan(string? Reason, DotnetTestMode Mode, IReadOnlyList<string> Report, string? UserResults, Dictionary<string, string> OutputDirectories);

    /// <summary>
    /// Decides whether the team's command can run as given. It cannot when it names test assemblies or modules; when it
    /// names no project and its directory has no single solution or project (<c>dotnet test</c> would stop with an
    /// MSBuild error; the Action passes only options); when a test project it runs needs the full MSBuild (legacy);
    /// when two of its test projects build assemblies of the same name (their reports could not be told apart); or,
    /// under the testing platform, when its test projects do not all take the same report option: one command passes
    /// the same options to every test project, and an option one of them does not know fails it. VSTest gets only
    /// <c>--logger trx</c>, unless the user names a TRX logger; its results stay where the team's run puts them.
    /// </summary>
    /// <param name="request">What the decision needs.</param>
    /// <param name="limit">Time left for build-output lookups; null for no limit.</param>
    private VerbatimPlan Verbatim(VerbatimRequest request, TimeSpan? limit)
    {
        static VerbatimPlan Fallback(string reason) => new(reason, DotnetTestMode.VSTest, [], null, []);
        var target = request.Target;
        if (target.Kind == ShadowTargetKind.Other)
        {
            return Fallback($"your arguments name {target.Describe()}, which shadow mode does not run as one command");
        }

        if (target.Kind is ShadowTargetKind.None or ShadowTargetKind.Directory && !ShadowCommand.FindsOne(target.Path))
        {
            return Fallback($"there is no single solution or project file in {target.Path} for dotnet test without a project argument to run");
        }

        var scope = request.Scope.Projects;
        if (scope.FirstOrDefault(request.Graph.NeedsFullMsBuild) is { } legacy)
        {
            return Fallback($"{Path.GetFileName(legacy.Path)} is a legacy project or references one, which TestDetta builds with the full MSBuild and runs on its own");
        }

        if (scope.FirstOrDefault(static p => p.AssemblyNameUnresolved) is { } unresolved)
        {
            return Fallback($"the assembly name of {Path.GetFileName(unresolved.Path)} is known only to MSBuild, so its reports could not be matched to it");
        }

        if (scope.GroupBy(static p => p.AssemblyName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static g => g.Count() > 1) is { } same)
        {
            return Fallback($"{string.Join(" and ", same.Select(static p => Path.GetFileName(p.Path)))} both build {same.Key}, so their reports could not be told apart");
        }

        var options = BeforeRunSettings(request.UserArguments);
        var results = ValueOf(options, "--results-directory") is { } given ? Path.GetFullPath(given, request.Directory) : null;
        var mode = request.Team.Mode;
        if (mode == DotnetTestMode.VSTest)
        {
            return new VerbatimPlan(null, mode, HasTrxLogger(options) ? [] : ["--logger", "trx"], results, []);
        }

        if (HasPlatformReport(options))
        {
            // The user's own reports are read where they land; nothing is added.
            return new VerbatimPlan(null, mode, [], results, []);
        }

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var chosen = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in scope)
        {
            var (output, timedOut) = OutputDirectory(project.Path, request.Directory, request.UserArguments, limit);
            if (timedOut)
            {
                return Fallback($"looking up the build output of {Path.GetFileName(project.Path)} ran past the analysis time limit");
            }

            if (output is null || !Directory.Exists(output))
            {
                // The report option a project takes is read from its build output, which a fresh checkout does not have yet.
                return Fallback($"{Path.GetFileName(project.Path)} runs on the testing platform and has no build output yet to tell which report option it takes");
            }

            if (TestReports.PlatformReportOption(output) is not { } option)
            {
                return Fallback($"{Path.GetFileName(project.Path)} runs on the testing platform without xUnit v3 or the TRX extension in its build output, so one command cannot ask every project for a report");
            }

            outputs[project.Path] = output;
            (chosen.TryGetValue(option, out var named) ? named : chosen[option] = []).Add(Path.GetFileName(project.Path));
        }

        if (chosen.Count > 1)
        {
            return Fallback($"its test projects need different report options ({string.Join("; ", chosen.Select(static c => $"{c.Key} for {string.Join(", ", c.Value)}"))}), and one command passes the same options to all");
        }

        return new VerbatimPlan(null, mode, [.. chosen.Keys], results, outputs);
    }

    /// <summary>True when the arguments already ask the testing platform for a TRX report, in any spelling.</summary>
    /// <param name="options">The user's arguments before a <c>--</c>.</param>
    private static bool HasPlatformReport(List<string> options) =>
        options.Any(static a => a.StartsWith("--report-trx", StringComparison.Ordinal) || a.StartsWith("--report-xunit-trx", StringComparison.Ordinal));

    /// <summary>
    /// The test projects the team's command runs: those of the solution or project it names, or of the one it finds
    /// in its directory. When that cannot be told (several candidates, a solution file that cannot be read, a
    /// traversal project), every test project under the directory; when the command names test assemblies or modules,
    /// every test project of the repository. Either way the scope is marked unknown and the report says so.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="target">What the command names.</param>
    /// <param name="directory">The team's working directory.</param>
    /// <param name="root">Repository root.</param>
    private static ShadowScope Scope(ProjectGraph graph, ShadowTarget target, string directory, string root)
    {
        var testProjects = graph.Projects.Where(static p => p.IsTestProject).OrderBy(static p => p.Path, StringComparer.Ordinal).ToList();
        if (target.Kind == ShadowTargetKind.Other)
        {
            return new ShadowScope(testProjects, false, root);
        }

        // Resolved, because the graph spells paths as git reports the root (the temp folder on macOS is a link).
        var named = ShadowCommand.Projects(target)?.Select(Paths.ResolveLinks).ToHashSet(Paths.Comparer);
        var inScope = named is null ? [] : testProjects.Where(p => named.Contains(Paths.ResolveLinks(p.Path))).ToList();
        if (inScope.Count > 0 || named is not null && target.Kind != ShadowTargetKind.Project)
        {
            return new ShadowScope(inScope, true, directory);
        }

        // A project that is no test project (a traversal project), a solution that cannot be read, or a directory with
        // several candidates.
        var under = target.Kind is ShadowTargetKind.Project or ShadowTargetKind.Solution ? Path.GetDirectoryName(target.Path)! : target.Path;
        var resolved = Paths.ResolveLinks(under);
        return new ShadowScope([.. testProjects.Where(p => Paths.IsUnder(Paths.ResolveLinks(p.Path), resolved))], false, under);
    }

    /// <summary>One test project in the full run: how its run ended and the report it wrote.</summary>
    /// <param name="Project">The test project.</param>
    /// <param name="Mode">The runner it ran with.</param>
    /// <param name="ExitCode">Its exit code, from its own run or its report; null when unknown (no report from one command).</param>
    /// <param name="WallSeconds">Its time (<see cref="ShadowProject.WallSeconds"/>).</param>
    /// <param name="Outcome">The report, or null when none could be asked for, found or read.</param>
    /// <param name="OutputDirectory">Its build output, when it was looked up (testing platform), to ask the reruns for the same report.</param>
    private sealed record FullRun(ProjectNode Project, DotnetTestMode Mode, int? ExitCode, double WallSeconds, TrxOutcome? Outcome, string? OutputDirectory)
    {
        /// <summary>True when the team's command runs it (<see cref="ShadowScope"/>).</summary>
        public bool InScope { get; init; } = true;

        /// <summary>True for a legacy project, which ran as <c>td test</c> runs it and is not rerun.</summary>
        public bool Legacy { get; init; }

        /// <summary>Why its report could not be read, or null.</summary>
        public string? ReadError { get; init; }

        /// <summary>
        /// Under the testing platform, the build output folder that did not exist when the project's run started, or
        /// null: the report option a project takes is read from its build output, so a project not built before the
        /// run (a fresh checkout without a build step) runs without a report and its failures cannot be compared.
        /// </summary>
        public string? NotBuiltOutput { get; init; }
    }

    /// <summary>The full run of every test project and the run as a whole.</summary>
    /// <param name="Runs">One entry per test project, in path order.</param>
    /// <param name="Info">How it was made, its time and exit code.</param>
    private sealed record FullRunOutcome(List<FullRun> Runs, ShadowRunInfo Info);

    /// <summary>
    /// Runs the team's command as given, with only the report option added, then matches each report to its test
    /// project by the test assembly it names (<see cref="TrxReport.Facts"/>). The reports are the <c>.trx</c> files
    /// the run wrote or rewrote anywhere under the repository or in the user's results directory; they are copied
    /// into TestDetta's temp folder and left where they are. A report that names no test is skipped; one whose
    /// assembly matches no single test project is listed as unmapped and not compared.
    /// </summary>
    /// <param name="plan">How the full run is made.</param>
    /// <param name="state">Receives the exit code as soon as the command has run.</param>
    private FullRunOutcome RunVerbatim(ShadowPlan plan, RunState state)
    {
        var verbatim = plan.Verbatim;
        List<string> command = ["test", .. Inserted(plan.UserArguments, verbatim.Report)];
        List<string> searched = [plan.Root, .. verbatim.UserResults is { } user ? [user] : Array.Empty<string>()];
        var before = ReportTimes(searched);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        state.Started = true;
        var exitCode = RunTeam(command, plan.Directory);
        state.ExitCode = exitCode;
        var info = new ShadowRunInfo(ShadowRunKind.Verbatim, "dotnet " + CommandLine(command), null, stopwatch.Elapsed.TotalSeconds, exitCode)
        {
            Scope = plan.Scope.Known ? null : plan.Scope.Under,
        };
        state.Info = info;

        var testProjects = plan.Graph.Projects.Where(static p => p.IsTestProject).OrderBy(static p => p.Path, StringComparer.Ordinal).ToList();
        var facts = new Dictionary<ProjectNode, List<TrxFileFacts>>();
        var unmapped = new SortedSet<string>(StringComparer.Ordinal);
        var own = new List<string>();
        foreach (var file in Changed(before, ReportTimes(searched)))
        {
            var parsed = Try(() => TrxReport.Facts(file));
            if (verbatim.Report.Count > 0 && parsed is not null && IsOwnReport(file, before, verbatim.UserResults))
            {
                own.Add(file);
            }

            if (parsed is not { Assemblies.Count: > 0 } report)
            {
                continue;
            }

            if (Owner(report.Assemblies, testProjects) is not { } owner)
            {
                unmapped.UnionWith(report.Assemblies.Select(Path.GetFileName).OfType<string>());
                continue;
            }

            // Read in place and copied: the team's reports stay where the run put them; TestDetta's own are deleted below.
            var folder = Path.Combine(state.Work, "project-" + testProjects.IndexOf(owner));
            if (TryDo(() =>
                {
                    Directory.CreateDirectory(folder);
                    File.Copy(file, Path.Combine(folder, Guid.NewGuid().ToString("N") + ".trx"));
                }))
            {
                (facts.TryGetValue(owner, out var list) ? list : facts[owner] = []).Add(report);
            }
        }

        var scope = plan.Scope.Projects.ToHashSet();
        var runs = new List<FullRun>(testProjects.Count);
        for (var i = 0; i < testProjects.Count; i++)
        {
            var project = testProjects[i];
            var output = verbatim.OutputDirectories.GetValueOrDefault(project.Path);
            if (!facts.TryGetValue(project, out var reports))
            {
                runs.Add(new FullRun(project, verbatim.Mode, null, 0, null, output) { InScope = scope.Contains(project) });
                continue;
            }

            var outcome = Try(() => TrxReport.Read(Path.Combine(state.Work, "project-" + i)));
            var failed = reports.Any(static r => r.Failed) || outcome?.FailedClasses.Count > 0;
            var starts = reports.Select(static r => r.Start).OfType<DateTimeOffset>().ToList();
            var finishes = reports.Select(static r => r.Finish).OfType<DateTimeOffset>().ToList();
            var seconds = starts.Count > 0 && finishes.Count > 0 ? Math.Max(0, (finishes.Max() - starts.Min()).TotalSeconds) : 0;
            runs.Add(new FullRun(project, verbatim.Mode, failed ? 1 : 0, seconds, outcome, output)
            {
                InScope = scope.Contains(project),
                ReadError = outcome is null ? "its test report could not be read" : null,
            });
        }

        DeleteOwnReports(own);
        info = info with { UnmappedReports = [.. unmapped] };
        state.Info = info;
        return new FullRunOutcome(runs, info);
    }

    /// <summary>
    /// True for a report the run wrote only because TestDetta asked for one (<see cref="DeleteOwnReports"/>): the caller
    /// added its own report option, the file did not exist before the run, and it lies where that option puts a report,
    /// directly in the user's results directory or in a <c>TestResults</c> folder (VSTest's beside the project, the testing
    /// platform's in the build output). A file the team's tests write elsewhere is never taken for one.
    /// </summary>
    /// <param name="file">A <c>.trx</c> file the run wrote, already read as a TRX report.</param>
    /// <param name="before">Report times before the run: a file listed there is not new.</param>
    /// <param name="userResults">The user's results directory, or null.</param>
    internal static bool IsOwnReport(string file, IReadOnlyDictionary<string, DateTime> before, string? userResults)
    {
        if (before.ContainsKey(file) || Path.GetDirectoryName(file) is not { } folder)
        {
            return false;
        }

        return string.Equals(Path.GetFileName(folder), "TestResults", StringComparison.OrdinalIgnoreCase)
               || userResults is not null && Paths.Comparer.Equals(Paths.Normalize(folder), Paths.Normalize(userResults));
    }

    /// <summary>
    /// Deletes the reports TestDetta asked the team's run for, once they are read and copied: when the team has no TRX
    /// report of its own, every new one from the report option TestDetta added is TestDetta's, and leaving them would
    /// fill the team's results folders with files they did not ask for. With the team's own TRX logger or report option
    /// TestDetta adds none, and nothing is deleted. Nothing else is touched: the folders, other loggers' files and
    /// attachments stay. A file that cannot be deleted is left.
    /// </summary>
    /// <param name="files">The reports that <see cref="IsOwnReport"/> found to be TestDetta's.</param>
    private void DeleteOwnReports(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            TryDo(() => File.Delete(file));
        }
    }

    /// <summary>
    /// The one test project whose assembly a report's tests ran from, matched by assembly name; when two projects
    /// build that name, the one whose folder holds the assembly. Null when there is none, or more than one.
    /// </summary>
    /// <param name="assemblies">Full paths of the report's test assemblies.</param>
    /// <param name="testProjects">The repository's test projects.</param>
    private static ProjectNode? Owner(IReadOnlySet<string> assemblies, IReadOnlyList<ProjectNode> testProjects)
    {
        ProjectNode? owner = null;
        foreach (var assembly in assemblies)
        {
            var name = Path.GetFileNameWithoutExtension(assembly.Replace('\\', '/'));
            var candidates = testProjects.Where(p => string.Equals(p.AssemblyName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count > 1)
            {
                var resolved = Paths.ResolveLinks(assembly);
                candidates = [.. candidates.Where(p => Paths.IsUnder(resolved, Paths.ResolveLinks(p.Directory)))];
            }

            if (candidates.Count != 1 || owner is not null && owner != candidates[0])
            {
                return null;
            }

            owner = candidates[0];
        }

        return owner;
    }

    /// <summary>
    /// Runs each test project the team's command runs on its own, one after another, from the team's working
    /// directory, with the user's arguments and a report. A step that fails for one project (its location, its report
    /// option, reading its report) costs only that project's report: its tests still run.
    /// </summary>
    /// <param name="plan">How the full run is made.</param>
    /// <param name="state">Receives the exit code once every project has run.</param>
    /// <param name="reason">Why the team's command could not run as given.</param>
    private FullRunOutcome RunPerProject(ShadowPlan plan, RunState state, string reason)
    {
        var runs = new List<FullRun>(plan.Scope.Projects.Count);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        state.Started = true;
        var index = 0;
        foreach (var project in plan.Scope.Projects)
        {
            if (cancel.IsCancellationRequested)
            {
                break;
            }

            if (RunOne(plan, state, project, index++) is { } run)
            {
                runs.Add(run);
            }
        }

        var userArguments = plan.RerunArguments;
        var command = $"dotnet test <test project> {CommandLine(userArguments)}".TrimEnd() + ", once per test project";
        var exitCode = cancel.IsCancellationRequested || runs.Any(static r => r.ExitCode != 0) ? 1 : 0;
        state.ExitCode = exitCode;
        var info = new ShadowRunInfo(ShadowRunKind.PerProject, command, reason, stopwatch.Elapsed.TotalSeconds, exitCode)
        {
            Scope = plan.Scope.Known ? null : plan.Scope.Under,
        };
        state.Info = info;
        return new FullRunOutcome(runs, info);
    }

    /// <summary>One project of the per-project run; null for a project <c>dotnet test</c> says is not a test project.</summary>
    /// <param name="plan">How the full run is made.</param>
    /// <param name="state">Holds the temp folder.</param>
    /// <param name="project">The test project.</param>
    /// <param name="index">Its position, for its report folder.</param>
    private FullRun? RunOne(ShadowPlan plan, RunState state, ProjectNode project, int index)
    {
        var userArguments = plan.RerunArguments;
        var legacy = plan.Graph.NeedsFullMsBuild(project);
        var mode = legacy ? DotnetTestMode.VSTest : plan.Team.Mode;
        var location = plan.Team with { Mode = mode };
        var selection = new TestProjectSelection(project, TestRunMode.All, [], "shadow mode runs every test");

        // A legacy project is built with the full MSBuild first, as td test does; its location comes from that path.
        var invocation = (legacy ? Try(() => TestInvocationBuilder.Build([selection], plan.Root, userArguments, plan.Graph).Single()) : null) is { } built
            ? built with { Location = location }
            : new TestInvocation(selection, [.. location.TestArguments(plan.Root, project.Path), .. userArguments], location);

        var results = Path.Combine(state.Work, "full-" + index);
        var reportName = $"testdetta-shadow-{Guid.NewGuid():N}.trx";
        var options = BeforeRunSettings(userArguments);
        var userResults = ValueOf(options, "--results-directory") is { } given ? Path.GetFullPath(given, plan.Directory) : null;
        string? outputDirectory = null;
        string? notBuilt = null;
        var report = Try<List<string>>(() =>
        {
            if (mode == DotnetTestMode.VSTest)
            {
                return HasTrxLogger(options) ? [] : ["--logger", "trx"];
            }

            outputDirectory = OutputDirectory(project.Path, plan.Directory, userArguments, QueryLimit).Directory;
            if (outputDirectory is null || HasPlatformReport(options))
            {
                return [];
            }

            // Which report option it takes is known only from its build output; report options are never guessed from
            // package references, since an option the project does not know fails its run.
            if (!Directory.Exists(outputDirectory))
            {
                notBuilt = outputDirectory;
                return [];
            }

            // NUnit's result XML goes through run settings, which would replace the user's own.
            return TestReports.UsesNUnitXml(mode, outputDirectory) && options.Any(static a => a is "-s" or "--settings" || a.StartsWith("--settings", StringComparison.Ordinal))
                ? []
                : TestReports.Arguments(mode, outputDirectory, results, reportName);
        }) ?? [];

        List<string> searched = [plan.Root, .. userResults is { } user ? [user] : Array.Empty<string>()];
        var before = Try(() => ReportTimes(searched)) ?? [];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int exitCode;
        try
        {
            var arguments = Inserted(invocation.Arguments, report);
            if (hooks.Dotnet is { } fake)
            {
                stdout.Flush();
                exitCode = fake(arguments, location.FullDirectory(plan.Root));
            }
            else
            {
                stdout.Flush();
                var result = _runner.RunAll([invocation with { Arguments = arguments }], plan.Root, stdout, cancel).Single();
                if (result.NotATestProject)
                {
                    return null;
                }

                exitCode = result.ExitCode;
            }
        }
        catch (InvalidOperationException ex)
        {
            // dotnet itself could not start; the team's own step would have failed as well.
            stdout.WriteLine($"td shadow: {ex.Message}");
            exitCode = 1;
        }

        var elapsed = stopwatch.Elapsed.TotalSeconds;

        // Read in place and copied: the team's reports stay where the run wrote them, TestDetta's own are deleted once
        // copied. False for "read" means an error.
        var own = new List<string>();
        var (outcome, read) = Try(() =>
        {
            foreach (var file in Changed(before, ReportTimes(searched)))
            {
                if (Try(() => TrxReport.Facts(file)) is not { } facts)
                {
                    continue;
                }

                var ours = report.Count > 0 && IsOwnReport(file, before, userResults);
                if (facts.Assemblies.Count > 0 && Owner(facts.Assemblies, [project]) is not null)
                {
                    Directory.CreateDirectory(results);
                    File.Copy(file, Path.Combine(results, Guid.NewGuid().ToString("N") + ".trx"));
                }
                else if (ours && facts.Assemblies.Count > 0)
                {
                    // Another project's report, not read here: not ours to delete either.
                    continue;
                }

                if (ours)
                {
                    own.Add(file);
                }
            }

            return (TrxReport.Read(results), true);
        });
        DeleteOwnReports(own);
        return new FullRun(project, mode, exitCode, elapsed, outcome, outputDirectory)
        {
            Legacy = legacy,
            ReadError = read ? null : "its test report could not be read",
            NotBuiltOutput = notBuilt,
        };
    }

    /// <summary>
    /// The full run of one project next to the selection's decisions for it: the classes in the project's report are
    /// compared with the selection; the classes the selection knows that did not run are listed with their decisions
    /// but not compared, since no test of them ran here. The project's mode, reason and estimate are the selection's
    /// own, whatever ran.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="analysis">The selection, or null without one.</param>
    /// <param name="run">The project's full run.</param>
    /// <param name="commandExitCode">The full run's exit code.</param>
    private static ShadowProject Compare(string root, AnalysisResult? analysis, FullRun run, int commandExitCode)
    {
        var path = Path.GetRelativePath(root, run.Project.Path).Replace('\\', '/');

        // The full run's projects come from the analysis's own graph, so their paths match exactly.
        var selection = analysis?.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, run.Project.Path, StringComparison.Ordinal));
        var decisions = selection?.Decisions.ToDictionary(static d => d.TestClass, StringComparer.Ordinal) ?? [];
        var reported = run.Outcome?.ReportedClasses ?? new HashSet<string>(StringComparer.Ordinal);
        var mode = (analysis, selection?.Mode) switch
        {
            (null, _) => "unknown",
            (_, TestRunMode.All) => "all",
            (_, TestRunMode.Filtered) => "filtered",
            (_, TestRunMode.Skipped) => "skipped",
            _ => "not-affected",
        };
        var reason = analysis is null ? "no selection" : selection?.Reason ?? "the change does not reach this project";
        var selectedClasses = new HashSet<string>(selection?.TestClasses ?? [], StringComparer.Ordinal);

        // A class without a decision of its own (the selection decided the project as a whole) takes the project's.
        ShadowClass Decided(string name)
        {
            var selected = mode == "all" || mode == "filtered" && selectedClasses.Contains(name);
            var (kind, evidence) = decisions.TryGetValue(name, out var decision)
                ? (decision.Kind.ToString(), decision.Evidence)
                : mode switch
                {
                    "all" => (nameof(ClassDecisionKind.WholeProject), reason),
                    "not-affected" => ("not-affected", reason),
                    "unknown" => ("unknown", reason),
                    _ => (nameof(ClassDecisionKind.Skipped), reason),
                };
            return new ShadowClass(name, selected, kind, evidence, null, false);
        }

        List<ShadowClass> notRun = [.. decisions.Keys.Where(k => !reported.Contains(k)).Order(StringComparer.Ordinal).Select(Decided)];
        if (reported.Count == 0)
        {
            var why = run.NotBuiltOutput is { } output ? ShadowProject.NotBuiltReason(Path.GetRelativePath(root, output).Replace('\\', '/'))
                : run.Outcome is not null ? "no test of it ran in this job"
                : run.ReadError is { } error ? error
                : run.ExitCode is not null ? "it wrote no test report that could be read"
                : run.InScope ? "it wrote no test report in this run"
                : "your command does not run it";
            return new ShadowProject(path, mode, reason, run.ExitCode, run.WallSeconds, run.Outcome is not null, [])
            {
                Estimate = selection?.Estimate,
                NotRun = notRun,
                NotCompared = why,
                MissingReport = run.ExitCode is null && run.InScope && commandExitCode != 0,
                NotBuilt = run.NotBuiltOutput is not null,
            };
        }

        var classes = new List<ShadowClass>(reported.Count);
        foreach (var name in reported.Order(StringComparer.Ordinal))
        {
            var failed = run.Outcome!.FailedClasses.Contains(name);
            var decided = Decided(name);
            ShadowVerdict? verdict = !failed || analysis is null ? null : decided.Selected ? ShadowVerdict.Caught : ShadowVerdict.Miss;
            classes.Add(decided with
            {
                Seconds = run.Outcome.ClassSeconds.GetValueOrDefault(name),
                Failed = failed,
                Verdict = verdict,
                FailedTests = [.. run.Outcome.FailedTestsOf(name).Order(StringComparer.Ordinal)],
            });
        }

        return new ShadowProject(path, mode, reason, run.ExitCode, run.WallSeconds, run.Outcome is not null, classes)
        {
            Estimate = selection?.Estimate,
            NotRun = notRun,
        };
    }

    /// <summary>What every classification step of one shadow run shares.</summary>
    /// <param name="Plan">How the full run was made: the root, the graph, the team's directory and the rerun arguments.</param>
    /// <param name="State">The temp folder and the base worktree.</param>
    /// <param name="MergeBase">Commit to rerun on, or null when unknown.</param>
    /// <param name="Limits">How long each rerun may take.</param>
    /// <param name="Budget">How long all of them together may take.</param>
    private sealed record Classification(ShadowPlan Plan, RunState State, string? MergeBase, ShadowLimits Limits, ShadowBudget Budget);

    /// <summary>
    /// Reruns the failed classes of <paramref name="project"/> the selection left out, and compares failing tests, not
    /// classes. First alone with the change: a class none of whose full-run failures fail again goes on to its project's
    /// full-run context with the change (the project under the user's own arguments, the classes the full run ran):
    /// passing there makes it flaky; failing again makes it order-dependent. A class that fails alone, and an
    /// order-dependent one, then run the same way on the base commit: when every test that fails with the change fails
    /// there too, the failure is pre-existing; otherwise the tests that fail only with the change make it a miss.
    /// A step that cannot run, or that the budget or a cancellation leaves no time for, leaves the class unclassified,
    /// which counts as a miss until checked.
    /// </summary>
    /// <param name="context">What the classification steps share.</param>
    /// <param name="run">The project's full run.</param>
    /// <param name="project">The comparison; candidates carry <see cref="ShadowVerdict.Miss"/>.</param>
    private ShadowProject Classify(Classification context, FullRun run, ShadowProject project)
    {
        var candidates = project.Classes.Where(static c => c.Verdict == ShadowVerdict.Miss).Select(static c => c.Name).ToList();
        if (candidates.Count == 0)
        {
            return project;
        }

        var verdicts = new Dictionary<string, (ShadowVerdict Verdict, string Note)>(StringComparer.Ordinal);
        if (run.Legacy)
        {
            candidates.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, "a legacy project is not rerun class by class"));
            return Apply(project, verdicts);
        }

        if (Stopped(context) is { } stop)
        {
            candidates.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, stop));
            return Apply(project, verdicts);
        }

        LogClassifying(project.Path, candidates.Count);
        var full = run.Outcome!;
        var alone = Rerun(context, new RerunRequest(run, OnBase: false, candidates, "alone with the change"));
        if (alone.Error is { } error)
        {
            var note = alone.Stop ?? $"the rerun with the change {error}";
            candidates.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, note));
            return Apply(project, verdicts);
        }

        // Failing tests per class: those of the full run that failed again; a failure under another test name (a
        // theory whose cases are named differently each run) still counts, which leans towards a miss.
        var failedAlone = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var passedAlone = new List<string>();
        foreach (var name in candidates)
        {
            if (!alone.Outcome!.ExecutedClasses.Contains(name))
            {
                verdicts[name] = (ShadowVerdict.Unclassified, "it did not run when rerun alone with the change");
                continue;
            }

            if (Reproduced(full.FailedTestsOf(name), alone.Outcome.FailedTestsOf(name)) is { Count: > 0 } again)
            {
                failedAlone[name] = again;
            }
            else
            {
                passedAlone.Add(name);
            }
        }

        var orderDependent = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        if (passedAlone.Count > 0)
        {
            var inContext = Stopped(context) is { } cut ? new RerunResult(null, cut, cut) : Rerun(context, new RerunRequest(run, OnBase: false, null, "in its full-run context with the change"));
            foreach (var name in passedAlone)
            {
                if (inContext.Error is { } failure)
                {
                    verdicts[name] = (ShadowVerdict.Unclassified, $"it passed alone with the change; {inContext.Stop ?? $"the rerun in its project's full-run context {failure}"}");
                    continue;
                }

                if (!inContext.Outcome!.ExecutedClasses.Contains(name))
                {
                    verdicts[name] = (ShadowVerdict.Unclassified, "it passed alone with the change and did not run in its project's full-run context");
                    continue;
                }

                if (Reproduced(full.FailedTestsOf(name), inContext.Outcome.FailedTestsOf(name)) is { Count: > 0 } again)
                {
                    orderDependent[name] = again;
                }
                else
                {
                    verdicts[name] = (ShadowVerdict.Flaky, "passed alone and in its project's full-run context with the change; it failed only once");
                }
            }
        }

        foreach (var (name, verdict) in OnBase(context, run, failedAlone, inContext: false))
        {
            verdicts[name] = verdict;
        }

        foreach (var (name, verdict) in OnBase(context, run, orderDependent, inContext: true))
        {
            verdicts[name] = verdict;
        }

        return Apply(project, verdicts);
    }

    /// <summary>
    /// The tests of <paramref name="rerun"/> that failed in the full run too; when none match by name but some failed,
    /// all of those, since test names may differ between runs and a failure must not be explained away by a name.
    /// </summary>
    /// <param name="full">Failed tests of the class in the full run.</param>
    /// <param name="rerun">Failed tests of the class in the rerun.</param>
    private static IReadOnlySet<string> Reproduced(IReadOnlySet<string> full, IReadOnlySet<string> rerun)
    {
        var both = rerun.Where(full.Contains).ToHashSet(StringComparer.Ordinal);
        return both.Count > 0 ? both : rerun;
    }

    /// <summary>Why the classification cannot go on (cancelled, or the budget is spent), or null.</summary>
    /// <param name="context">What the classification steps share.</param>
    private string? Stopped(Classification context) =>
        cancel.IsCancellationRequested ? "the run was cancelled" : context.Budget.RanOut ? context.Budget.Exhausted() : null;

    /// <summary>
    /// Reruns <paramref name="classes"/> on the base commit, in a worktree beside the working tree: alone, or for
    /// order-dependent classes in their project's full-run context. A class all of whose failing tests fail there too
    /// is pre-existing; any test that fails only with the change makes it a miss.
    /// </summary>
    /// <param name="context">What the classification steps share.</param>
    /// <param name="run">The project's full run.</param>
    /// <param name="classes">Classes and the tests that fail with the change.</param>
    /// <param name="inContext">True for order-dependent classes, which rerun in their full-run context.</param>
    private Dictionary<string, (ShadowVerdict Verdict, string Note)> OnBase(Classification context, FullRun run, Dictionary<string, IReadOnlySet<string>> classes, bool inContext)
    {
        var verdicts = new Dictionary<string, (ShadowVerdict, string)>(StringComparer.Ordinal);
        if (classes.Count == 0)
        {
            return verdicts;
        }

        var how = inContext ? "it passed alone but failed again in its project's full-run context with the change" : "it fails alone with the change";
        var where = inContext ? "in that context" : "alone";
        void All(ShadowVerdict verdict, string note)
        {
            foreach (var name in classes.Keys)
            {
                verdicts[name] = (verdict, note);
            }
        }

        if (context.MergeBase is not { } mergeBase)
        {
            All(ShadowVerdict.Unclassified, $"{how}; the base commit to rerun it on is unknown");
            return verdicts;
        }

        if (Stopped(context) is { } stop)
        {
            All(ShadowVerdict.Unclassified, $"{how}; {stop} before the rerun on the base commit");
            return verdicts;
        }

        if (BaseTree(context, mergeBase) is not { } baseTree)
        {
            All(ShadowVerdict.Unclassified, $"{how}; {context.State.BaseError}");
            return verdicts;
        }

        // Checked again after the checkout, which draws from the budget too.
        if (Stopped(context) is { } later)
        {
            All(ShadowVerdict.Unclassified, $"{how}; {later} before the rerun on the base commit");
            return verdicts;
        }

        if (!File.Exists(Path.Combine(baseTree, Path.GetRelativePath(context.Plan.Root, run.Project.Path))))
        {
            All(ShadowVerdict.Miss, $"{how}; its test project does not exist on the base commit");
            return verdicts;
        }

        var commit = mergeBase[..Math.Min(12, mergeBase.Length)];
        var onBase = Rerun(context, new RerunRequest(run, OnBase: true, inContext ? null : [.. classes.Keys], inContext ? "in its full-run context on the base commit" : "alone on the base commit"));
        foreach (var (name, failing) in classes)
        {
            if (onBase.Error is { } error)
            {
                verdicts[name] = (ShadowVerdict.Unclassified, onBase.Stop is { } budget ? $"{how}; {budget} during the rerun on the base commit {commit}" : $"{how}; the rerun on the base commit {commit} {error}");
                continue;
            }

            var outcome = onBase.Outcome!;
            var failedThere = outcome.FailedTestsOf(name);
            var onlyWithChange = failing.Where(t => !failedThere.Contains(t)).Order(StringComparer.Ordinal).ToList();
            verdicts[name] = onlyWithChange.Count == 0 && failedThere.Count > 0
                ? (ShadowVerdict.PreExisting, $"{how}, and every test of it that fails with the change fails {where} on the base commit {commit} too")
                : !outcome.ReportedClasses.Contains(name)
                    ? (ShadowVerdict.Miss, $"{how}, and it has no test {where} on the base commit {commit}")
                    : (ShadowVerdict.Miss, $"{(inContext ? "order-dependent, caused by the change: " : string.Empty)}{how}, and {Tests(onlyWithChange.Count == 0 ? [.. failing] : onlyWithChange)} pass{(onlyWithChange.Count == 1 ? "es" : string.Empty)} {where} on the base commit {commit}");
        }

        return verdicts;
    }

    /// <summary>A short list of test names for a note: the first three, and how many more.</summary>
    /// <param name="tests">Test names.</param>
    private static string Tests(List<string> tests) =>
        tests.Count <= 3 ? string.Join(", ", tests) : $"{string.Join(", ", tests.Take(3))} and {tests.Count - 3} more";

    /// <summary>
    /// The base commit's worktree, checked out on first use under the temp folder and shared by every project. The
    /// checkout is cut to what is left of the budget and stops on cancellation; a failed one is not tried again.
    /// </summary>
    /// <param name="context">What the classification steps share.</param>
    /// <param name="mergeBase">The commit.</param>
    private string? BaseTree(Classification context, string mergeBase)
    {
        var state = context.State;
        if (state.BaseError is not null)
        {
            return null;
        }

        if (state.BaseTree is { } existing)
        {
            return existing;
        }

        var path = Path.Combine(state.Work, "base");
        state.BaseTree = path;
        var (limits, capped) = context.Budget.For(null, null);
        try
        {
            _git.AddWorktree(context.Plan.Root, path, mergeBase, limits.Timeout, cancel);
            return path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            LogFailedStep(ex);
            state.BaseError = ex switch
            {
                OperationCanceledException => "the run was cancelled during the checkout of the base commit",
                TimeoutException when capped => $"{context.Budget.Exhausted()} during the checkout of the base commit",
                _ => "the base commit could not be checked out",
            };

            // Half made: removed now, so a later step does not mistake it for a checkout.
            _git.DiscardWorktree(context.Plan.Root, path);
            state.BaseTree = null;
            return null;
        }
    }

    /// <summary>What a rerun showed: its report, or why it has none.</summary>
    /// <param name="Outcome">The report.</param>
    /// <param name="Error">Why there is no report (it timed out, did not start, wrote none), or null.</param>
    /// <param name="Stop">The budget's or the cancellation's reason when that stopped it, which then replaces the note.</param>
    private sealed record RerunResult(TrxOutcome? Outcome, string? Error, string? Stop = null);

    /// <summary>One rerun of a project.</summary>
    /// <param name="Run">The project's full run.</param>
    /// <param name="OnBase">True to run on the base commit's worktree, built there; false for the working tree, with <c>--no-build</c>.</param>
    /// <param name="Classes">Classes to run, inside the user's own filter; null for the project's full-run context (the user's arguments alone).</param>
    /// <param name="Label">How the progress line names it.</param>
    private sealed record RerunRequest(FullRun Run, bool OnBase, IReadOnlyList<string>? Classes, string Label);

    /// <summary>
    /// Reruns a test project, from the team's working directory in the tree, with the user's arguments and only
    /// <see cref="RerunRequest.Classes"/> when given. Every rerun writes its report into TestDetta's own folder: the
    /// user's results directory and TRX logger are replaced for it. A rerun in the working tree uses the full run's
    /// build and the rerun limits; one on the base commit builds there (without <c>--no-build</c>, <c>--no-restore</c>
    /// or the user's output paths, which would make it read or overwrite the working tree's build) and has the base
    /// limits. Either is cut to what is left of the budget, and killed on cancellation.
    /// </summary>
    /// <param name="context">What the classification steps share.</param>
    /// <param name="request">What to rerun.</param>
    private RerunResult Rerun(Classification context, RerunRequest request)
    {
        var plan = context.Plan;
        var run = request.Run;
        var treeRoot = request.OnBase ? context.State.BaseTree! : plan.Root;
        var projectPath = request.OnBase ? Path.Combine(treeRoot, Path.GetRelativePath(plan.Root, run.Project.Path)) : run.Project.Path;
        var directory = plan.Team.FullDirectory(treeRoot);
        if (!Directory.Exists(directory))
        {
            directory = treeRoot;
        }

        var mode = request.OnBase ? Try(() => (DotnetTestMode?)DotnetTestModeDetector.Detect(directory)) ?? plan.Team.Mode : plan.Team.Mode;
        var location = new DotnetTestLocation(Path.GetRelativePath(treeRoot, directory).Replace('\\', '/') is var relative && relative != "." ? relative : string.Empty, mode);
        var userOptions = BeforeRunSettings(plan.RerunArguments);
        var arguments = RerunArguments(plan.RerunArguments, mode, request.OnBase);
        List<string> filter = [];
        var others = arguments;
        if (request.Classes is { } classes)
        {
            if (TestFilter.WithinUserFilter([.. classes], TestFilter.SyntaxFor(run.Project, mode), TestFilter.WithTeamFilter(arguments, run.Project, mode)) is not var (joined, rest))
            {
                return new RerunResult(null, "could not run: your own filter cannot be joined with a class filter");
            }

            (filter, others) = (joined, rest);
        }

        var results = Path.Combine(context.State.Work, "rerun-" + Guid.NewGuid().ToString("N"));
        var reportName = $"testdetta-shadow-{Guid.NewGuid():N}.trx";
        List<string>? report;
        if (mode == DotnetTestMode.VSTest)
        {
            report = ["--logger", "trx", "--results-directory", results];
        }
        else
        {
            // A project that ran inside one command over a solution had its build output looked up only when that command needed it.
            var outputDirectory = run.OutputDirectory ?? OutputDirectory(run.Project.Path, plan.Directory, plan.RerunArguments, context.Budget.For(QueryLimit, null).Limits.Timeout).Directory;
            report = outputDirectory is null || TestReports.UsesNUnitXml(mode, outputDirectory) && userOptions.Any(static a => a is "-s" or "--settings" || a.StartsWith("--settings", StringComparison.Ordinal))
                ? null
                : TestReports.Arguments(mode, outputDirectory, results, reportName) is { Count: > 0 } platform ? platform : null;
            if (report is not null && ValueOf(userOptions, "--results-directory") is not null)
            {
                // The user's results directory was taken out; TestDetta's takes its place.
                report = [.. report, "--results-directory", results];
            }
        }

        if (report is null)
        {
            return new RerunResult(null, "could not run: no test report can be asked for");
        }

        List<string> command = [.. location.TestArguments(treeRoot, projectPath), .. filter, .. others];
        if (!request.OnBase && !command.Contains("--no-build"))
        {
            command = Inserted(command, ["--no-build"]);
        }

        var (limits, capped) = request.OnBase
            ? context.Budget.For(context.Limits.BaseTimeout, context.Limits.BaseIdle)
            : context.Budget.For(context.Limits.RerunTimeout, context.Limits.RerunIdle);
        stdout.WriteLine($"td shadow: rerunning {(request.Classes is { } some ? $"{some.Count} class(es)" : "the project")} of {Path.GetFileName(projectPath)} {request.Label}");
        stdout.Flush();
        int exitCode;
        try
        {
            var full = Inserted(command, report);
            exitCode = hooks.Dotnet is { } fake ? fake(full, directory) : new DotnetProcess(_logger).Run(full, directory, new Dictionary<string, string>(), stdout, limits, cancel);
        }
        catch (InvalidOperationException ex)
        {
            return new RerunResult(null, $"could not start: {ex.Message}");
        }

        if (exitCode == DotnetProcess.Cancelled || cancel.IsCancellationRequested)
        {
            return new RerunResult(null, "was stopped: the run was cancelled", "the run was cancelled");
        }

        if (exitCode == DotnetProcess.TimedOut)
        {
            // Killed at the budget's end rather than at its own limit or for sitting idle.
            return capped && context.Budget.RanOut
                ? new RerunResult(null, "was stopped: the classification time budget ran out", context.Budget.Exhausted())
                : new RerunResult(null, "timed out");
        }

        var outcome = Try(() =>
        {
            // A testing-platform report has TestDetta's unique name; it is moved out of wherever the run wrote it.
            TestReports.Collect(mode, treeRoot, reportName, results);
            return TrxReport.Read(results);
        });
        return outcome is null ? new RerunResult(null, $"wrote no test report that could be read (exit code {exitCode})") : new RerunResult(outcome, null);
    }

    /// <summary>
    /// The user's arguments for a rerun: without their results directory, and without the report options TestDetta
    /// sets for itself (the user's TRX logger, the testing platform's report options), so the rerun's report goes into
    /// TestDetta's folder only; on the base commit also without <c>--no-build</c>, <c>--no-restore</c> and the output
    /// paths (<c>-o</c>, <c>--output</c>, <c>--artifacts-path</c>), so the base worktree restores and builds into its
    /// own folders and never over the working tree's build.
    /// </summary>
    /// <param name="userArguments">The user's arguments for a run of one project.</param>
    /// <param name="mode">The rerun's runner.</param>
    /// <param name="onBase">True for a rerun on the base commit.</param>
    internal static List<string> RerunArguments(IReadOnlyList<string> userArguments, DotnetTestMode mode, bool onBase)
    {
        List<string> withValue = ["--results-directory"];
        List<string> flags = [];
        if (mode == DotnetTestMode.TestingPlatform)
        {
            withValue.AddRange(["--report-trx-filename", "--report-xunit-trx-filename"]);
            flags.AddRange(["--report-trx", "--report-xunit-trx"]);
        }

        if (onBase)
        {
            withValue.AddRange(["-o", "--output", "--artifacts-path"]);
            flags.AddRange(["--no-build", "--no-restore"]);
        }

        var arguments = WithoutOptions(userArguments, withValue, flags);
        if (mode != DotnetTestMode.VSTest)
        {
            return arguments;
        }

        // The user's TRX logger may name a file of its own, anywhere; TestDetta's own takes its place.
        var result = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "--")
            {
                result.AddRange(arguments.Skip(i));
                break;
            }

            if (arguments[i] is "--logger" or "-l" && i + 1 < arguments.Count && HasTrxLogger([arguments[i], arguments[i + 1]]))
            {
                i++;
                continue;
            }

            if (arguments[i].StartsWith('-') && HasTrxLogger([arguments[i]]))
            {
                continue;
            }

            result.Add(arguments[i]);
        }

        return result;
    }

    /// <summary>Sets the verdicts of the classified classes.</summary>
    /// <param name="project">The comparison.</param>
    /// <param name="verdicts">Verdict and note per class name.</param>
    private static ShadowProject Apply(ShadowProject project, Dictionary<string, (ShadowVerdict Verdict, string Note)> verdicts) => project with
    {
        Classes = [.. project.Classes.Select(c => verdicts.TryGetValue(c.Name, out var v) ? c with { Verdict = v.Verdict, Note = v.Note } : c)],
    };

    /// <summary>
    /// The build output of a testing-platform test project, to see which report extension it has (an unknown
    /// option would fail the run). Null when it cannot be found; the project then runs without a report.
    /// </summary>
    /// <param name="project">The test project file.</param>
    /// <param name="runDirectory">Directory to ask MSBuild in (the team's working directory).</param>
    /// <param name="userArguments">The user's arguments, for <c>-c</c> and <c>-f</c>.</param>
    /// <param name="limit">Longest each query may take; null for no limit.</param>
    /// <returns>The folder, or null; and true when a query ran past the limit.</returns>
    private (string? Directory, bool TimedOut) OutputDirectory(string project, string runDirectory, IReadOnlyList<string> userArguments, TimeSpan? limit)
    {
        if (hooks.Dotnet is not null || limit is { } left && left <= TimeSpan.Zero)
        {
            return (null, limit is { } none && none <= TimeSpan.Zero);
        }

        var options = BeforeRunSettings(userArguments);
        var configuration = ValueOf(options, "-c") ?? ValueOf(options, "--configuration") ?? "Debug";
        List<string> query = ["msbuild", project, $"-p:Configuration={configuration}", "-getProperty:TargetPath", "-getProperty:TargetFrameworks"];
        if ((ValueOf(options, "-f") ?? ValueOf(options, "--framework")) is { } framework)
        {
            query.Add($"-p:TargetFramework={framework}");
        }

        var timedOut = false;
        var directory = Try(() =>
        {
            try
            {
                var dotnet = new DotnetProcess(_logger);
                using var properties = System.Text.Json.JsonDocument.Parse(dotnet.Capture(query, runDirectory, limit));
                var values = properties.RootElement.GetProperty("Properties");
                if (values.GetProperty("TargetPath").GetString() is not { Length: > 0 } targetPath)
                {
                    var first = values.GetProperty("TargetFrameworks").GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (first is null)
                    {
                        return null;
                    }

                    targetPath = dotnet.Capture([.. query.Where(static q => !q.StartsWith("-getProperty", StringComparison.Ordinal)), $"-p:TargetFramework={first}", "-getProperty:TargetPath"], runDirectory, limit).Trim();
                }

                return Path.GetDirectoryName(targetPath);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                return null;
            }
        });
        return (directory, timedOut);
    }

    /// <summary>
    /// What a reader needs to check the result: versions, commits, maps and two commands. The first is
    /// the same <c>td test --shadow</c> at the head commit, compared with the merge base itself, with the same maps
    /// (their hashes are listed to compare), makes the same decisions and shows them for the classes it runs. The
    /// second lists every decision without running tests.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="root">Repository root, or null.</param>
    /// <param name="graph">Projects, or null.</param>
    private ShadowReproduction Reproduce(CliOptions options, string? root, ProjectGraph? graph)
    {
        var assembly = typeof(ShadowRun).Assembly;
        var version = CliApp.Version;
        var binary = Try(() => Sha256(assembly.Location));
        var mergeBase = root is null ? null : Try(() => _git.MergeBase(root, options.Analysis.BaseRef));
        var head = root is null ? null : Try(() => _git.Head(root));
        var maps = new List<(string, string, string)>();
        foreach (var project in graph?.Projects.Where(static p => p.IsTestProject) ?? [])
        {
            if (Try(() => CoverageMapStore.PathFor(root!, project.Path)) is { } path && File.Exists(path)
                && Try(() => CoverageMapStore.Read(path)) is { } map && Try(() => Sha256(path)) is { } sha)
            {
                maps.Add((Path.GetRelativePath(root!, project.Path).Replace('\\', '/'), map.Commit, sha));
            }
        }

        var drift = options.Analysis.DriftFallback == DriftFallback.RunAll ? "all" : "name";
        List<string> selecting = [$"--max-map-age {options.Analysis.MaxMapAge}", $"--drift-fallback {drift}"];
        if (options.Analysis.NonCodeFiles != AnalysisOptions.DefaultNonCodeFiles)
        {
            selecting.Add($"--non-code-files {(options.Analysis.NonCodeFiles == NonCodeFileRule.Readers ? "readers" : "project")}");
        }

        if (options.Analysis.NotBuildInputs.Count > 0)
        {
            selecting.Add($"--not-build-inputs {Quoted(string.Join(';', options.Analysis.NotBuildInputs))}");
        }

        if (options.Analysis.MapsDir is { } mapsDir)
        {
            selecting.Add($"--maps-dir {Quoted(mapsDir)}");
        }

        // The classification's limits decide which failures could be classified in time, so they are inputs too.
        var limits = options.Test.ShadowLimits ?? ShadowLimits.Default;
        List<string> classifying = options.Test.ShadowClassify ? [.. limits.Options] : [$"--shadow-analysis-timeout {Durations.Limit(limits.Analysis)}", "--shadow-no-classify"];
        List<string> testArguments = options.Test.DotnetTestArguments.Count > 0 ? ["-- " + CommandLine(options.Test.DotnetTestArguments)] : [];
        var compareWith = $"--base {mergeBase ?? options.Analysis.BaseRef}";
        var command = string.Join(' ', ["td test --shadow", compareWith, .. selecting, .. classifying, .. testArguments]);
        var explain = string.Join(' ', ["td affected", compareWith, .. selecting, "--format json"]);
        return new ShadowReproduction(version, binary, options.Analysis.BaseRef, mergeBase, head, maps, [$"--base {options.Analysis.BaseRef}", .. selecting, .. classifying, .. testArguments], command, explain);
    }

    /// <summary>Writes the report to the console, the JSON file and, when asked, the job summary.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="result">What to report.</param>
    /// <param name="root">Repository root, for the default report path.</param>
    private void Report(CliOptions options, ShadowResult result, string root)
    {
        TryDo(() => ShadowReport.WriteConsole(result, stdout));
        var path = options.Test.ShadowReport is { } given ? Path.GetFullPath(given) : Path.Combine(root, ".testdetta", "testdetta-shadow.json");
        if (!TryDo(() => ShadowReport.WriteJson(result, path)))
        {
            stdout.WriteLine($"td shadow: could not write {path}");
        }
        else
        {
            stdout.WriteLine($"td shadow: report written to {path}");
        }

        if (options.Test.SummaryFile is { } summary)
        {
            TryDo(() =>
            {
                using var writer = File.AppendText(summary);
                ShadowReport.WriteMarkdown(result, writer);
            });
        }
    }

    /// <summary>
    /// The value of <paramref name="action"/>, or null when it throws: in shadow mode nothing beyond the full run
    /// may fail the job. The exception is logged.
    /// </summary>
    /// <param name="action">Work that may fail.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Shadow mode is fail-open by design: a TestDetta error must never fail the team's job.")]
    private T? Try<T>(Func<T?> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailedStep(ex);
            return default;
        }
    }

    /// <summary>Runs <paramref name="action"/>; false when it throws (see <see cref="Try{T}"/>).</summary>
    /// <param name="action">Work that may fail.</param>
    private bool TryDo(Action action) => Try<object>(() =>
    {
        action();
        return action;
    }) is not null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Shadow: rerunning {Count} failed class(es) of {Project} the selection left out")]
    private partial void LogClassifying(string project, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shadow: a step failed and was skipped")]
    private partial void LogFailedStep(Exception exception);
}
