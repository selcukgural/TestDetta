using System.Reflection;
using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage;
using static TestDetta.Cli.TestRunArguments;

namespace TestDetta.Cli;

/// <summary>TestDetta's analysis for a shadow run, as <see cref="CliApp"/> made it before the full run.</summary>
/// <param name="Result">The selection, or null when the analysis failed.</param>
/// <param name="Error">Why the analysis failed, or null.</param>
/// <param name="Seconds">How long the analysis took.</param>
/// <param name="Log">
/// What the analysis logged, held back until the full run has finished: its debug lines name the decisions, and
/// nothing that reveals the selection may appear before the run that pays for it is over.
/// </param>
internal sealed record ShadowAnalysis(AnalysisResult? Result, string? Error, double Seconds, string Log);

/// <summary>
/// <c>td test --shadow</c> (docs/design/shadow-mode.md): every test runs, as without TestDetta, and the run is
/// compared with what the selection would have run. The full run is the team's own <c>dotnet test</c> command as
/// given, with only a report option added, whenever its reports can be matched to test projects; otherwise one
/// <c>dotnet test</c> per test project, and the report says why. A failed class the selection left out is rerun
/// before it is called a miss: alone with the change (passing there makes it flaky or order-dependent), then alone
/// on the base commit in a separate worktree (failing there too makes it a failure the change did not cause). The
/// exit code is the full run's alone, and nothing TestDetta does beyond that run can fail the job. No license is
/// read and nothing is sent anywhere; in return, decisions are shown only for the classes this job ran.
/// </summary>
/// <param name="loggerFactory">Creates the loggers.</param>
/// <param name="stdout">Receives the test output and the report.</param>
/// <param name="stderr">Receives the analysis's held log once the full run has finished.</param>
internal sealed partial class ShadowRun(ILoggerFactory loggerFactory, TextWriter stdout, TextWriter stderr)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ShadowRun>();
    private readonly GitClient _git = new(loggerFactory.CreateLogger<GitClient>());

    /// <summary>Runs the full suite, compares, classifies and reports.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="input">The analysis made before the full run.</param>
    /// <returns>The full run's exit code: 0, or 3 when it failed.</returns>
    public int Run(CliOptions options, ShadowAnalysis input)
    {
        var analysis = input.Result;
        var userArguments = options.Test.DotnetTestArguments;
        var directory = Path.GetFullPath(options.Analysis.RepoPath);
        var root = analysis?.RepoRoot ?? Try(() => _git.TopLevel(options.Analysis.RepoPath));
        var graph = analysis?.Graph ?? (root is null ? null : Try(() => new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>(), new MsBuildProjectEvaluator(loggerFactory)).Load(root)));
        var runner = new TestRunner(loggerFactory.CreateLogger<TestRunner>());
        if (root is null || graph is null)
        {
            // Nothing is known about the projects: the full run is the plain dotnet test the team would run.
            stdout.Flush();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var exitCode = runner.RunCommand(["test", .. userArguments], directory, stdout);
            ReleaseLog(input, everythingRan: true);
            var run = new ShadowRunInfo(ShadowRunKind.Plain, "dotnet " + CommandLine(["test", .. userArguments]), null, stopwatch.Elapsed.TotalSeconds, exitCode);
            var result = new ShadowResult(false, input.Error ?? "the repository's projects could not be read", [], Reproduce(options, root, graph), input.Seconds) { Run = run };
            Report(options, result, root ?? options.Analysis.RepoPath);
            return exitCode == 0 ? 0 : 3;
        }

        var work = Path.Combine(Path.GetTempPath(), "testdetta-shadow-" + Guid.NewGuid().ToString("N"));
        string? baseTree = null;
        try
        {
            var full = RunEverything(new FullRunRequest(root, graph, userArguments, directory, work), runner);
            var compared = analysis is not null;
            var projects = full.Runs.Select(r => Compare(root, analysis, r)).ToList();

            // The analysis's log names decisions; it is shown only when this job ran every class it decided about.
            ReleaseLog(input, everythingRan: !compared || projects.All(static p => p.Shown && p.NotRun.Count == 0));
            ShadowClassification? classification = null;
            if (compared && options.Test.ShadowClassify)
            {
                // The budget starts after the full run: it limits what TestDetta adds, never the team's own run.
                var limits = options.Test.ShadowLimits ?? ShadowLimits.Default;
                var budget = new ShadowBudget(limits.Budget);
                var context = new Classification(root, graph, full.RerunArguments, work, Try(() => _git.MergeBase(root, options.Analysis.BaseRef)), limits, budget);
                for (var i = 0; i < projects.Count; i++)
                {
                    projects[i] = Classify(context, full.Runs[i], projects[i], ref baseTree);
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

            var shadow = new ShadowResult(compared, compared ? null : input.Error, projects, Reproduce(options, root, graph), input.Seconds)
            {
                ClassificationOff = !options.Test.ShadowClassify,
                Classification = classification,
                Run = full.Info,
            };
            Report(options, shadow, root);
            return full.Info.ExitCode == 0 ? 0 : 3;
        }
        finally
        {
            if (baseTree is { } tree)
            {
                TryDo(() => _git.RemoveWorktree(root, tree));
            }

            if (Directory.Exists(work))
            {
                TryDo(() => Directory.Delete(work, recursive: true));
            }
        }
    }

    /// <summary>Writes the analysis's held log to stderr, or one line saying why it is not shown.</summary>
    /// <param name="input">The analysis.</param>
    /// <param name="everythingRan">True when it can reveal nothing beyond what the report shows.</param>
    private void ReleaseLog(ShadowAnalysis input, bool everythingRan)
    {
        if (input.Log.Length == 0)
        {
            return;
        }

        if (everythingRan)
        {
            stderr.Write(input.Log);
            return;
        }

        stderr.WriteLine("td shadow: the analysis's log is not shown, because it names decisions for tests that did not run in this job.");
    }

    /// <summary>What the full run needs to know.</summary>
    /// <param name="Root">Repository root.</param>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="UserArguments">The user's <c>dotnet test</c> arguments.</param>
    /// <param name="Directory">Directory the user's command runs in.</param>
    /// <param name="Work">Folder for the reports.</param>
    private sealed record FullRunRequest(string Root, ProjectGraph Graph, IReadOnlyList<string> UserArguments, string Directory, string Work);

    /// <summary>One test project in the full run: where it reruns, how its run ended and the report it wrote.</summary>
    /// <param name="Project">The test project.</param>
    /// <param name="Location">Where it runs on its own (the reruns), and with which runner.</param>
    /// <param name="ExitCode">Its exit code, from its own run or its report; null when unknown (no report from one command).</param>
    /// <param name="WallSeconds">Its time (<see cref="ShadowProject.WallSeconds"/>).</param>
    /// <param name="Outcome">The report, or null when none could be asked for or found.</param>
    /// <param name="OutputDirectory">Its build output, when it was looked up (testing platform), to ask the reruns for the same report.</param>
    private sealed record FullRun(ProjectNode Project, DotnetTestLocation Location, int? ExitCode, double WallSeconds, TrxOutcome? Outcome, string? OutputDirectory);

    /// <summary>The full run of every test project and the run as a whole.</summary>
    /// <param name="Runs">One entry per test project, in path order.</param>
    /// <param name="Info">How it was made, its time and exit code.</param>
    /// <param name="RerunArguments">The user's arguments for a run of one project: without the solution or project they named.</param>
    private sealed record FullRunOutcome(List<FullRun> Runs, ShadowRunInfo Info, IReadOnlyList<string> RerunArguments);

    /// <summary>
    /// Runs every test the team's command runs: that command as given when its reports can be matched to test
    /// projects (<see cref="Verbatim"/>), otherwise one <c>dotnet test</c> per test project with the reason.
    /// </summary>
    /// <param name="request">What the full run needs.</param>
    /// <param name="runner">Runs the commands.</param>
    private FullRunOutcome RunEverything(FullRunRequest request, TestRunner runner)
    {
        var target = ShadowCommand.Target(request.UserArguments, request.Directory);
        var rerunArguments = target.Without(request.UserArguments);
        var plan = Verbatim(request, target);
        return plan.Reason is { } reason
            ? RunPerProject(request with { UserArguments = rerunArguments }, runner, reason)
            : RunVerbatim(request, runner, plan) with { RerunArguments = rerunArguments };
    }

    /// <summary>How the team's command can run as given, or why it cannot.</summary>
    /// <param name="Reason">Why it runs per project instead; null when it can run as given.</param>
    /// <param name="Mode">Runner of the command's directory.</param>
    /// <param name="Report">What is added to the command for a report per project.</param>
    /// <param name="UserResults">The user's own results directory, where VSTest's reports then go.</param>
    /// <param name="OutputDirectories">Build output of each testing-platform test project looked up.</param>
    private sealed record VerbatimPlan(string? Reason, DotnetTestMode Mode, IReadOnlyList<string> Report, string? UserResults, Dictionary<string, string> OutputDirectories);

    /// <summary>
    /// Decides whether the team's command can run as given. It cannot when it names test assemblies or modules; when it
    /// names no project and its directory has no single solution or project (<c>dotnet test</c> would stop with an
    /// MSBuild error; the Action passes only options); when a test project it runs needs the full MSBuild (legacy);
    /// when two of its test projects build assemblies of the same name (their reports could not be told apart); or,
    /// under the testing platform, when its test projects do not all take the same report option: one command passes
    /// the same options to every test project, and an option one of them does not know fails it.
    /// </summary>
    /// <param name="request">What the full run needs.</param>
    /// <param name="target">What the command names.</param>
    private VerbatimPlan Verbatim(FullRunRequest request, ShadowTarget target)
    {
        static VerbatimPlan Fallback(string reason) => new(reason, DotnetTestMode.VSTest, [], null, []);
        if (target.Kind == ShadowTargetKind.Other)
        {
            return Fallback($"your arguments name {target.Describe()}, which shadow mode does not run as one command");
        }

        if (target.Kind is ShadowTargetKind.None or ShadowTargetKind.Directory && !ShadowCommand.FindsOne(target.Path))
        {
            return Fallback($"there is no single solution or project file in {target.Path} for dotnet test without a project argument to run");
        }

        DotnetTestMode mode;
        try
        {
            mode = DotnetTestModeDetector.Detect(request.Directory);
        }
        catch (InvalidDataException ex)
        {
            return Fallback(ex.Message);
        }

        var scope = Scope(request, target);
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
        if (mode == DotnetTestMode.VSTest)
        {
            var (report, userResults) = ReportArguments(mode, null, Path.Combine(request.Work, "full"), string.Empty, request.UserArguments, request.Directory);
            return new VerbatimPlan(null, mode, report ?? [], userResults, []);
        }

        var results = ValueOf(options, "--results-directory") is { } given ? Path.GetFullPath(given, request.Directory) : null;
        if (options.Any(static a => a.StartsWith("--report-trx", StringComparison.Ordinal) || a.StartsWith("--report-xunit-trx", StringComparison.Ordinal)))
        {
            // The user's own reports are read where they land; nothing is added.
            return new VerbatimPlan(null, mode, [], results, []);
        }

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var chosen = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in scope)
        {
            var output = OutputDirectory(project.Path, DotnetTestLocation.For(request.Root, project.Path).FullDirectory(request.Root), request.UserArguments);
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

    /// <summary>
    /// The test projects the team's command runs: those of the solution or project it names, or of the one it finds
    /// in its directory; when that cannot be told, every test project under the directory.
    /// </summary>
    /// <param name="request">What the full run needs.</param>
    /// <param name="target">What the command names.</param>
    private static List<ProjectNode> Scope(FullRunRequest request, ShadowTarget target)
    {
        var testProjects = request.Graph.Projects.Where(static p => p.IsTestProject).OrderBy(static p => p.Path, StringComparer.Ordinal).ToList();

        // Resolved, because the graph spells paths as git reports the root (the temp folder on macOS is a link).
        var named = ShadowCommand.Projects(target)?.Select(Paths.ResolveLinks).ToHashSet(Paths.Comparer);
        var inScope = named is null ? [] : testProjects.Where(p => named.Contains(Paths.ResolveLinks(p.Path))).ToList();
        if (inScope.Count > 0 || named is not null && target.Kind != ShadowTargetKind.Project)
        {
            return inScope;
        }

        // A project that is no test project (a traversal project) or a directory with several candidates.
        var under = Paths.ResolveLinks(target.Kind == ShadowTargetKind.Project ? Path.GetDirectoryName(target.Path)! : target.Path);
        return [.. testProjects.Where(p => Paths.IsUnder(Paths.ResolveLinks(p.Path), under))];
    }

    /// <summary>
    /// Runs the team's command as given, with only the report option added, then matches each report to its test
    /// project by the test assembly it names (<see cref="TrxReport.Facts"/>). A report that names no test is skipped;
    /// one whose assembly matches no single test project is listed as unmapped and not compared.
    /// </summary>
    /// <param name="request">What the full run needs.</param>
    /// <param name="runner">Runs the command.</param>
    /// <param name="plan">The report option and where the reports land.</param>
    private FullRunOutcome RunVerbatim(FullRunRequest request, TestRunner runner, VerbatimPlan plan)
    {
        var results = Path.Combine(request.Work, "full");
        List<string> command = ["test", .. Inserted(request.UserArguments, plan.Report)];

        // VSTest writes every report into one results directory; the testing platform next to each test output
        // (or where the repository's build or the user says), so the whole repository is searched for new reports.
        List<string> searched = plan.Mode == DotnetTestMode.VSTest ? [plan.UserResults ?? results] : [request.Root, .. plan.UserResults is { } user ? [user] : Array.Empty<string>()];
        var before = ReportTimes(searched);
        stdout.Flush();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exitCode = runner.RunCommand(command, request.Directory, stdout);
        var elapsed = stopwatch.Elapsed.TotalSeconds;

        var testProjects = request.Graph.Projects.Where(static p => p.IsTestProject).OrderBy(static p => p.Path, StringComparer.Ordinal).ToList();
        var facts = new Dictionary<ProjectNode, List<TrxFileFacts>>();
        var unmapped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ReportTimes(searched).Where(f => !before.TryGetValue(f.Key, out var time) || time != f.Value).Select(static f => f.Key).Order(StringComparer.Ordinal))
        {
            if (Try(() => TrxReport.Facts(file)) is not { Assemblies.Count: > 0 } report)
            {
                continue;
            }

            if (Owner(report.Assemblies, testProjects) is not { } owner)
            {
                unmapped.UnionWith(report.Assemblies.Select(Path.GetFileName).OfType<string>());
                continue;
            }

            var folder = Path.Combine(request.Work, "project-" + testProjects.IndexOf(owner));
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".trx");

            // A report the run wrote only because TestDetta asked for it is moved out of the tree; the user's own stay.
            var theirs = plan.UserResults is { } own && Paths.IsUnder(Paths.Normalize(file), Paths.Normalize(own)) || plan.Mode == DotnetTestMode.TestingPlatform && plan.Report.Count == 0;
            if (!TryDo(() =>
                {
                    if (theirs)
                    {
                        File.Copy(file, destination);
                    }
                    else
                    {
                        File.Move(file, destination);
                    }
                }))
            {
                continue;
            }

            (facts.TryGetValue(owner, out var list) ? list : facts[owner] = []).Add(report);
        }

        var runs = new List<FullRun>(testProjects.Count);
        for (var i = 0; i < testProjects.Count; i++)
        {
            var project = testProjects[i];
            var location = DotnetTestLocation.For(request.Root, project.Path);
            if (!facts.TryGetValue(project, out var reports))
            {
                runs.Add(new FullRun(project, location, null, 0, null, plan.OutputDirectories.GetValueOrDefault(project.Path)));
                continue;
            }

            var outcome = TrxReport.Read(Path.Combine(request.Work, "project-" + i));
            var failed = reports.Any(static r => r.Failed) || outcome?.FailedClasses.Count > 0;
            var starts = reports.Select(static r => r.Start).OfType<DateTimeOffset>().ToList();
            var finishes = reports.Select(static r => r.Finish).OfType<DateTimeOffset>().ToList();
            var seconds = starts.Count > 0 && finishes.Count > 0 ? Math.Max(0, (finishes.Max() - starts.Min()).TotalSeconds) : 0;
            runs.Add(new FullRun(project, location, failed ? 1 : 0, seconds, outcome, plan.OutputDirectories.GetValueOrDefault(project.Path)));
        }

        var info = new ShadowRunInfo(ShadowRunKind.Verbatim, "dotnet " + CommandLine(command), null, elapsed, exitCode) { UnmappedReports = [.. unmapped] };
        return new FullRunOutcome(runs, info, request.UserArguments);
    }

    /// <summary>
    /// The one test project whose assembly a report's tests ran from, matched by assembly name; when two projects
    /// build that name, the one whose folder holds the assembly. Null when there is none, or more than one.
    /// </summary>
    /// <param name="assemblies">Full paths of the report's test assemblies.</param>
    /// <param name="testProjects">The repository's test projects.</param>
    private static ProjectNode? Owner(IReadOnlySet<string> assemblies, List<ProjectNode> testProjects)
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

    /// <summary>Every <c>.trx</c> file under the folders and when it was last written, to tell a run's new reports from older ones.</summary>
    /// <param name="folders">Folders to search, recursively; links are not followed.</param>
    private static Dictionary<string, DateTime> ReportTimes(IEnumerable<string> folders)
    {
        var found = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var times = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.trx", found))
            {
                times[file] = File.GetLastWriteTimeUtc(file);
            }
        }

        return times;
    }

    /// <summary>Runs every test project on its own, one after another, with the user's arguments and one report each.</summary>
    /// <param name="request">What the full run needs; its arguments no longer name a solution or project.</param>
    /// <param name="runner">Runs the invocations.</param>
    /// <param name="reason">Why the team's command could not run as given.</param>
    private FullRunOutcome RunPerProject(FullRunRequest request, TestRunner runner, string reason)
    {
        var (root, graph, userArguments, _, work) = request;
        var testProjects = graph.Projects.Where(static p => p.IsTestProject).OrderBy(static p => p.Path, StringComparer.Ordinal).ToList();
        var selections = testProjects.Select(static p => new TestProjectSelection(p, TestRunMode.All, [], "shadow mode runs every test")).ToList();
        var runs = new List<FullRun>(selections.Count);
        var index = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var invocation in TestInvocationBuilder.Build(selections, root, userArguments, graph))
        {
            var results = Path.Combine(work, "full-" + index++);
            var reportName = $"testdetta-shadow-{Guid.NewGuid():N}.trx";
            var legacy = invocation.LegacyConfiguration is not null;
            var mode = legacy ? DotnetTestMode.VSTest : invocation.Location.Mode;
            var outputDirectory = mode == DotnetTestMode.TestingPlatform ? OutputDirectory(invocation.Selection.Project.Path, invocation.Location.FullDirectory(root), userArguments) : null;
            var (report, userResults) = ReportArguments(mode, outputDirectory, results, reportName, userArguments, invocation.Location.FullDirectory(root));
            var before = Snapshot(userResults);
            stdout.Flush();
            var result = runner.RunAll([invocation with { Arguments = Inserted(invocation.Arguments, report ?? []) }], root, stdout).Single();
            if (result.NotATestProject)
            {
                continue;
            }

            var outcome = report is null ? null : Collect(mode, root, reportName, results, userResults, before);
            runs.Add(new FullRun(invocation.Selection.Project, invocation.Location with { Mode = mode }, result.ExitCode, result.Elapsed.TotalSeconds, outcome, outputDirectory));
        }

        var command = $"dotnet test <test project> {CommandLine(userArguments)}".TrimEnd() + ", once per test project";
        var info = new ShadowRunInfo(ShadowRunKind.PerProject, command, reason, stopwatch.Elapsed.TotalSeconds, runs.Any(static r => r.ExitCode != 0) ? 1 : 0);
        return new FullRunOutcome(runs, info, userArguments);
    }

    /// <summary>
    /// The full run of one project next to the selection's decisions for it. Decisions are given only for the classes
    /// in the project's report: those the selection knows that did not run are listed by name, and a project none of
    /// whose tests ran shows no decision at all (see <see cref="ShadowResult"/>).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="analysis">The selection, or null without one.</param>
    /// <param name="run">The project's full run.</param>
    private static ShadowProject Compare(string root, AnalysisResult? analysis, FullRun run)
    {
        var path = Path.GetRelativePath(root, run.Project.Path).Replace('\\', '/');
        // The full run's projects come from the analysis's own graph, so their paths match exactly.
        var selection = analysis?.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, run.Project.Path, StringComparison.Ordinal));
        var decisions = selection?.Decisions.ToDictionary(static d => d.TestClass, StringComparer.Ordinal) ?? [];
        var reported = run.Outcome?.ReportedClasses ?? new HashSet<string>(StringComparer.Ordinal);
        if (analysis is not null && reported.Count == 0)
        {
            var why = run.Outcome is not null ? "no test of it ran in this job"
                : run.ExitCode is null ? "this run has no test report of it (your command does not run it, or it wrote none)"
                : "it wrote no test report that could be read";
            return new ShadowProject(path, ShadowProject.NotShown, why, run.ExitCode, run.WallSeconds, run.Outcome is not null, [])
            {
                NotRun = [.. decisions.Keys.Order(StringComparer.Ordinal)],
            };
        }

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
        var classes = new List<ShadowClass>(reported.Count);
        foreach (var name in reported.Order(StringComparer.Ordinal))
        {
            var selected = mode == "all" || mode == "filtered" && selectedClasses.Contains(name);
            (string Kind, string Evidence) said = decisions.TryGetValue(name, out var decision)
                ? (decision.Kind.ToString(), decision.Evidence)
                : mode switch
                {
                    "all" => (nameof(ClassDecisionKind.WholeProject), reason),
                    "not-affected" => ("not-affected", reason),
                    "unknown" => ("unknown", reason),
                    _ => (nameof(ClassDecisionKind.Skipped), reason),
                };
            var failed = run.Outcome!.FailedClasses.Contains(name);
            ShadowVerdict? verdict = !failed || analysis is null ? null : selected ? ShadowVerdict.Caught : ShadowVerdict.Miss;
            classes.Add(new ShadowClass(name, selected, said.Kind, said.Evidence, run.Outcome.ClassSeconds.GetValueOrDefault(name), failed) { Verdict = verdict });
        }

        List<string> notRun = [.. decisions.Keys.Where(k => !reported.Contains(k)).Order(StringComparer.Ordinal)];
        return new ShadowProject(path, mode, reason, run.ExitCode, run.WallSeconds, run.Outcome is not null, classes)
        {
            // The estimate covers every class, so it would say something about those that did not run.
            Estimate = notRun.Count == 0 ? selection?.Estimate : null,
            NotRun = notRun,
        };
    }

    /// <summary>What every classification step of one shadow run shares.</summary>
    /// <param name="Root">Repository root.</param>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="UserArguments">The user's <c>dotnet test</c> arguments for a run of one project: without the solution or project they name.</param>
    /// <param name="Work">Folder for the reports and the base commit's worktree.</param>
    /// <param name="MergeBase">Commit to rerun on, or null when unknown.</param>
    /// <param name="Limits">How long each rerun may take.</param>
    /// <param name="Budget">How long all of them together may take.</param>
    private sealed record Classification(string Root, ProjectGraph Graph, IReadOnlyList<string> UserArguments, string Work, string? MergeBase, ShadowLimits Limits, ShadowBudget Budget);

    /// <summary>
    /// Reruns the failed classes of <paramref name="project"/> the selection left out: alone with the change,
    /// twice for those that pass the first time, and alone on the base commit for those that fail. Until a
    /// rerun shows otherwise, such a class is a miss; a rerun that cannot run, or that the budget leaves no
    /// time for, leaves it unclassified.
    /// </summary>
    /// <param name="context">What the classification steps share.</param>
    /// <param name="run">The project's full run.</param>
    /// <param name="project">The comparison; candidates carry <see cref="ShadowVerdict.Miss"/>.</param>
    /// <param name="baseTree">The base commit's worktree, made on first use and shared by every project.</param>
    private ShadowProject Classify(Classification context, FullRun run, ShadowProject project, ref string? baseTree)
    {
        var candidates = project.Classes.Where(static c => c.Verdict == ShadowVerdict.Miss).Select(static c => c.Name).ToList();
        if (candidates.Count == 0)
        {
            return project;
        }

        var verdicts = new Dictionary<string, (ShadowVerdict Verdict, string Note)>(StringComparer.Ordinal);
        if (run.Location.Mode == DotnetTestMode.VSTest && context.Graph.NeedsFullMsBuild(run.Project))
        {
            candidates.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, "a legacy project is not rerun class by class"));
            return Apply(project, verdicts);
        }

        if (context.Budget.RanOut)
        {
            var reason = context.Budget.Exhausted();
            candidates.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, reason));
            return Apply(project, verdicts);
        }

        LogClassifying(project.Path, candidates.Count);
        var withChange = Rerun(context, run, run.Project.Path, context.Root, candidates, Path.Combine(context.Work, "alone-1-" + project.Path.GetHashCode(StringComparison.Ordinal)));
        if (withChange.Error is { } error)
        {
            var note = withChange.OutOfBudget ? context.Budget.Exhausted() : $"the rerun with the change {error}";
            candidates.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, note));
            return Apply(project, verdicts);
        }

        var passed = candidates.Where(c => withChange.Outcome!.ExecutedClasses.Contains(c) && !withChange.Outcome.FailedClasses.Contains(c)).ToList();
        var failedAlone = candidates.Where(c => withChange.Outcome!.FailedClasses.Contains(c)).ToList();
        foreach (var missing in candidates.Except(passed).Except(failedAlone))
        {
            verdicts[missing] = (ShadowVerdict.Unclassified, "it did not run when rerun alone with the change");
        }

        if (passed.Count > 0)
        {
            // Passing alone once is already flaky; the second rerun only says more, so the budget may skip it.
            var again = context.Budget.RanOut
                ? null
                : Rerun(context, run, run.Project.Path, context.Root, passed, Path.Combine(context.Work, "alone-2-" + project.Path.GetHashCode(StringComparison.Ordinal)));
            var cut = again is null || again.OutOfBudget ? $"; {context.Budget.Exhausted()} before the second rerun" : string.Empty;
            foreach (var name in passed)
            {
                verdicts[name] = again?.Outcome?.FailedClasses.Contains(name) == true
                    ? (ShadowVerdict.Flaky, "passed once and failed once when rerun alone with the change")
                    : (ShadowVerdict.Flaky, again?.Outcome?.ExecutedClasses.Contains(name) == true
                        ? "passed twice when rerun alone with the change; it failed only next to the other tests"
                        : "passed when rerun alone with the change" + cut);
            }
        }

        if (failedAlone.Count > 0)
        {
            foreach (var (name, verdict) in OnBase(context, run, failedAlone, ref baseTree))
            {
                verdicts[name] = verdict;
            }
        }

        return Apply(project, verdicts);
    }

    /// <summary>
    /// Reruns <paramref name="classes"/> (which failed alone with the change) on the base commit, in a worktree
    /// beside the working tree: failing there too makes a class pre-existing, passing makes it a miss.
    /// </summary>
    /// <param name="context">What the classification steps share.</param>
    /// <param name="run">The project's full run.</param>
    /// <param name="classes">Classes that failed alone with the change.</param>
    /// <param name="baseTree">The base commit's worktree, made on first use and shared by every project.</param>
    private Dictionary<string, (ShadowVerdict Verdict, string Note)> OnBase(Classification context, FullRun run, List<string> classes, ref string? baseTree)
    {
        var verdicts = new Dictionary<string, (ShadowVerdict, string)>(StringComparer.Ordinal);
        if (context.MergeBase is not { } mergeBase)
        {
            classes.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, "it fails alone with the change; the base commit to rerun it on is unknown"));
            return verdicts;
        }

        if (baseTree is null && !context.Budget.RanOut)
        {
            var path = Path.Combine(context.Work, "base");
            if (!TryDo(() => _git.AddWorktree(context.Root, path, mergeBase)))
            {
                classes.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, "it fails alone with the change; the base commit could not be checked out"));
                return verdicts;
            }

            baseTree = path;
        }

        // Checked again after the checkout, which draws from the budget too.
        if (context.Budget.RanOut)
        {
            var reason = $"it fails alone with the change; {context.Budget.Exhausted()} before the rerun on the base commit";
            classes.ForEach(c => verdicts[c] = (ShadowVerdict.Unclassified, reason));
            return verdicts;
        }

        var project = Path.Combine(baseTree!, Path.GetRelativePath(context.Root, run.Project.Path));
        if (!File.Exists(project))
        {
            classes.ForEach(c => verdicts[c] = (ShadowVerdict.Miss, "it fails alone with the change; its test project does not exist on the base commit"));
            return verdicts;
        }

        var commit = mergeBase[..Math.Min(12, mergeBase.Length)];
        var onBase = Rerun(context, run, project, baseTree!, classes, Path.Combine(context.Work, "base-" + run.Project.Path.GetHashCode(StringComparison.Ordinal)));
        var stopped = onBase.OutOfBudget ? $"it fails alone with the change; {context.Budget.Exhausted()} during the rerun on the base commit {commit}" : null;
        foreach (var name in classes)
        {
            verdicts[name] = onBase switch
            {
                _ when stopped is not null => (ShadowVerdict.Unclassified, stopped),
                { Outcome: { } outcome } when outcome.FailedClasses.Contains(name) => (ShadowVerdict.PreExisting, $"it fails alone on the base commit {commit} too"),
                { Outcome: { } outcome } when outcome.ExecutedClasses.Contains(name) => (ShadowVerdict.Miss, $"it fails alone with the change and passes alone on the base commit {commit}"),
                { Outcome: not null } or { Error: null } => (ShadowVerdict.Miss, $"it fails alone with the change and has no test on the base commit {commit}"),
                _ => (ShadowVerdict.Unclassified, $"it fails alone with the change; the rerun on the base commit {commit} {onBase.Error}"),
            };
        }

        return verdicts;
    }

    /// <summary>What a rerun showed: its report, or why it has none.</summary>
    /// <param name="Outcome">The report.</param>
    /// <param name="Error">Why there is no report (it timed out, did not start, wrote none), or null.</param>
    /// <param name="OutOfBudget">True when it was stopped because the classification's budget ran out.</param>
    private sealed record RerunResult(TrxOutcome? Outcome, string? Error, bool OutOfBudget = false);

    /// <summary>
    /// Runs only <paramref name="classes"/> of a test project, inside the user's own filter, with the same kind
    /// of report as the full run. A rerun in the working tree uses the full run's build and the rerun limits;
    /// one in the base worktree builds there and has the base limits. Either is cut to what is left of the budget.
    /// </summary>
    /// <param name="context">What the classification steps share.</param>
    /// <param name="run">The project's full run (its runner, project and output).</param>
    /// <param name="projectPath">The project file to run: the working tree's, or the base worktree's.</param>
    /// <param name="treeRoot">Root of the tree the project file is in.</param>
    /// <param name="classes">Runtime class names.</param>
    /// <param name="results">Folder for the report.</param>
    private RerunResult Rerun(Classification context, FullRun run, string projectPath, string treeRoot, List<string> classes, string results)
    {
        var noBuild = string.Equals(treeRoot, context.Root, StringComparison.Ordinal);
        var userArguments = context.UserArguments;
        var location = DotnetTestLocation.For(treeRoot, projectPath);
        var syntax = TestFilter.SyntaxFor(run.Project, location.Mode);
        var arguments = noBuild || !userArguments.Contains("--no-build") ? userArguments : [.. userArguments.Where(static a => a != "--no-build")];
        if (TestFilter.WithinUserFilter(classes, syntax, arguments) is not var (filter, others))
        {
            return new RerunResult(null, "could not run: your own filter cannot be joined with a class filter");
        }

        var reportName = $"testdetta-shadow-{Guid.NewGuid():N}.trx";
        // A project that ran inside one command over a solution had its build output looked up only when that command needed it.
        var outputDirectory = run.OutputDirectory ?? (location.Mode == DotnetTestMode.TestingPlatform ? OutputDirectory(run.Project.Path, DotnetTestLocation.For(context.Root, run.Project.Path).FullDirectory(context.Root), userArguments) : null);
        var (report, userResults) = ReportArguments(location.Mode, outputDirectory, results, reportName, arguments, location.FullDirectory(treeRoot));
        if (report is null)
        {
            return new RerunResult(null, "could not run: no test report can be asked for");
        }

        List<string> command = [.. location.TestArguments(treeRoot, projectPath), .. filter, .. others];
        if (noBuild && !command.Contains("--no-build"))
        {
            command = Inserted(command, ["--no-build"]);
        }

        var before = Snapshot(userResults);
        var (limits, capped) = noBuild
            ? context.Budget.For(context.Limits.RerunTimeout, context.Limits.RerunIdle)
            : context.Budget.For(context.Limits.BaseTimeout, context.Limits.BaseIdle);
        stdout.WriteLine($"td shadow: rerunning {classes.Count} class(es) of {Path.GetFileName(projectPath)} {(noBuild ? "alone with the change" : "alone on the base commit")}");
        stdout.Flush();
        int exitCode;
        try
        {
            exitCode = new DotnetProcess(_logger).Run(Inserted(command, report), location.FullDirectory(treeRoot), new Dictionary<string, string>(), stdout, limits);
        }
        catch (InvalidOperationException ex)
        {
            return new RerunResult(null, $"could not start: {ex.Message}");
        }

        if (exitCode == DotnetProcess.TimedOut)
        {
            // Killed at the budget's end rather than at its own limit or for sitting idle.
            var outOfBudget = capped && context.Budget.RanOut;
            return new RerunResult(null, outOfBudget ? "was stopped: the classification time budget ran out" : "timed out", outOfBudget);
        }

        var outcome = Collect(location.Mode, treeRoot, reportName, results, userResults, before);
        return outcome is null ? new RerunResult(null, $"wrote no test report (exit code {exitCode})") : new RerunResult(outcome, null);
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
    /// <param name="runDirectory">Directory to ask MSBuild in (the project's <c>global.json</c> location).</param>
    /// <param name="userArguments">The user's arguments, for <c>-c</c> and <c>-f</c>.</param>
    private string? OutputDirectory(string project, string runDirectory, IReadOnlyList<string> userArguments)
    {
        var options = BeforeRunSettings(userArguments);
        var configuration = ValueOf(options, "-c") ?? ValueOf(options, "--configuration") ?? "Debug";
        List<string> query = ["msbuild", project, $"-p:Configuration={configuration}", "-getProperty:TargetPath", "-getProperty:TargetFrameworks"];
        if ((ValueOf(options, "-f") ?? ValueOf(options, "--framework")) is { } framework)
        {
            query.Add($"-p:TargetFramework={framework}");
        }

        return Try(() =>
        {
            var dotnet = new DotnetProcess(_logger);
            using var properties = System.Text.Json.JsonDocument.Parse(dotnet.Capture(query, runDirectory));
            var values = properties.RootElement.GetProperty("Properties");
            if (values.GetProperty("TargetPath").GetString() is not { Length: > 0 } targetPath)
            {
                var first = values.GetProperty("TargetFrameworks").GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is null)
                {
                    return null;
                }

                targetPath = dotnet.Capture([.. query.Where(static q => !q.StartsWith("-getProperty", StringComparison.Ordinal)), $"-p:TargetFramework={first}", "-getProperty:TargetPath"], runDirectory).Trim();
            }

            return Path.GetDirectoryName(targetPath);
        });
    }

    /// <summary>
    /// What a reader needs to check the result: versions, commits, maps and two commands. The first needs no license:
    /// the same <c>td test --shadow</c> at the head commit, compared with the merge base itself, with the same maps
    /// (their hashes are listed to compare), makes the same decisions and shows them for the classes it runs. The
    /// second lists every decision without running tests, which is the paid selection, so it needs a license.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="root">Repository root, or null.</param>
    /// <param name="graph">Projects, or null.</param>
    private ShadowReproduction Reproduce(CliOptions options, string? root, ProjectGraph? graph)
    {
        var assembly = typeof(ShadowRun).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var binary = Try(() => Sha256(assembly.Location));
        var mergeBase = root is null ? null : Try(() => _git.MergeBase(root, options.Analysis.BaseRef));
        var head = root is null ? null : Try(() => _git.Head(root));
        var maps = new List<(string, string, string)>();
        foreach (var project in graph?.Projects.Where(static p => p.IsTestProject) ?? [])
        {
            var path = CoverageMapStore.PathFor(root!, project.Path);
            if (File.Exists(path) && Try(() => CoverageMapStore.Read(path)) is { } map && Try(() => Sha256(path)) is { } sha)
            {
                maps.Add((Path.GetRelativePath(root!, project.Path).Replace('\\', '/'), map.Commit, sha));
            }
        }

        var drift = options.Analysis.DriftFallback == DriftFallback.RunAll ? "all" : "name";
        List<string> selecting = [$"--max-map-age {options.Analysis.MaxMapAge}", $"--drift-fallback {drift}"];
        if (options.Analysis.MapsDir is { } mapsDir)
        {
            selecting.Add($"--maps-dir {Quoted(mapsDir)}");
        }

        // The classification's limits decide which failures could be classified in time, so they are inputs too.
        List<string> classifying = [.. options.Test.ShadowClassify ? (options.Test.ShadowLimits ?? ShadowLimits.Default).Options : ["--shadow-no-classify"]];
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
        ShadowReport.WriteConsole(result, stdout);
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
        where T : class
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailedStep(ex);
            return null;
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
