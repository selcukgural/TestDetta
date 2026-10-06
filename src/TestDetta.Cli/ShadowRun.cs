using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Cli;

/// <summary>
/// <c>td test --shadow</c> (docs/design/shadow-mode.md): every test project runs completely, as without
/// TestDetta, and the run is compared with what the selection would have run. A failed class the selection
/// left out is rerun before it is called a miss: alone with the change (passing there makes it flaky or
/// order-dependent), then alone on the base commit in a separate worktree (failing there too makes it a
/// failure the change did not cause). The exit code is the full run's alone, and nothing TestDetta does
/// beyond that run can fail the job.
/// </summary>
/// <param name="loggerFactory">Creates the loggers.</param>
/// <param name="stdout">Receives the test output and the report.</param>
internal sealed partial class ShadowRun(ILoggerFactory loggerFactory, TextWriter stdout)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ShadowRun>();
    private readonly GitClient _git = new(loggerFactory.CreateLogger<GitClient>());

    /// <summary>Runs the full suite, compares, classifies and reports.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="analysis">The selection, or null when the analysis failed.</param>
    /// <param name="analysisError">Why the analysis failed, or null.</param>
    /// <param name="analysisSeconds">How long the analysis took.</param>
    /// <returns>The full run's exit code: 0, or 3 when a test project failed.</returns>
    public int Run(CliOptions options, AnalysisResult? analysis, string? analysisError, double analysisSeconds)
    {
        var userArguments = options.Test.DotnetTestArguments;
        var root = analysis?.RepoRoot ?? Try(() => _git.TopLevel(options.Analysis.RepoPath));
        var graph = analysis?.Graph ?? (root is null ? null : Try(() => new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>(), new MsBuildProjectEvaluator(loggerFactory)).Load(root)));
        var runner = new TestRunner(loggerFactory.CreateLogger<TestRunner>());
        if (root is null || graph is null)
        {
            // Nothing is known about the projects: the full run is the plain dotnet test the team would run.
            stdout.Flush();
            var exitCode = runner.RunCommand(["test", .. userArguments], options.Analysis.RepoPath, stdout);
            var result = new ShadowResult(false, analysisError ?? "the repository's projects could not be read", [], Reproduce(options, root, graph), analysisSeconds);
            Report(options, result, root ?? options.Analysis.RepoPath);
            return exitCode == 0 ? 0 : 3;
        }

        var work = Path.Combine(Path.GetTempPath(), "testdetta-shadow-" + Guid.NewGuid().ToString("N"));
        string? baseTree = null;
        try
        {
            var runs = RunEverything(root, graph, userArguments, runner, work);
            var compared = analysis is not null;
            var projects = runs.Select(r => Compare(root, analysis, r)).ToList();
            ShadowClassification? classification = null;
            if (compared && options.Test.ShadowClassify)
            {
                // The budget starts after the full run: it limits what TestDetta adds, never the team's own run.
                var limits = options.Test.ShadowLimits ?? ShadowLimits.Default;
                var budget = new ShadowBudget(limits.Budget);
                var context = new Classification(root, graph, userArguments, work, Try(() => _git.MergeBase(root, options.Analysis.BaseRef)), limits, budget);
                for (var i = 0; i < projects.Count; i++)
                {
                    projects[i] = Classify(context, runs[i], projects[i], ref baseTree);
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

            var shadow = new ShadowResult(compared, compared ? null : analysisError, projects, Reproduce(options, root, graph), analysisSeconds)
            {
                ClassificationOff = !options.Test.ShadowClassify,
                Classification = classification,
            };
            Report(options, shadow, root);
            return runs.Any(static r => r.Result.ExitCode != 0) ? 3 : 0;
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

    /// <summary>One test project's full run: the invocation, its result and the report it wrote.</summary>
    /// <param name="Project">The test project.</param>
    /// <param name="Location">Where it ran and with which runner.</param>
    /// <param name="Result">Exit code and duration.</param>
    /// <param name="Outcome">The report, or null when none could be asked for or found.</param>
    /// <param name="OutputDirectory">Its build output, when it was looked up (testing platform), to ask the reruns for the same report.</param>
    private sealed record FullRun(ProjectNode Project, DotnetTestLocation Location, TestRunResult Result, TrxOutcome? Outcome, string? OutputDirectory);

    /// <summary>Runs every test project completely with the user's arguments and one report each.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="userArguments">The user's <c>dotnet test</c> arguments.</param>
    /// <param name="runner">Runs the invocations.</param>
    /// <param name="work">Folder for the reports.</param>
    private List<FullRun> RunEverything(string root, ProjectGraph graph, IReadOnlyList<string> userArguments, TestRunner runner, string work)
    {
        var testProjects = graph.Projects.Where(static p => p.IsTestProject).OrderBy(static p => p.Path, StringComparer.Ordinal).ToList();
        var selections = testProjects.Select(static p => new TestProjectSelection(p, TestRunMode.All, [], "shadow mode runs every test")).ToList();
        var runs = new List<FullRun>(selections.Count);
        var index = 0;
        foreach (var invocation in TestInvocationBuilder.Build(selections, root, userArguments, graph))
        {
            var results = Path.Combine(work, "full-" + index++);
            var reportName = $"testdetta-shadow-{Guid.NewGuid():N}.trx";
            var legacy = invocation.LegacyConfiguration is not null;
            var mode = legacy ? DotnetTestMode.VSTest : invocation.Location.Mode;
            var outputDirectory = mode == DotnetTestMode.TestingPlatform ? OutputDirectory(invocation, root, userArguments) : null;
            var (report, userResults) = ReportArguments(mode, outputDirectory, results, reportName, userArguments, invocation.Location.FullDirectory(root));
            var before = Snapshot(userResults);
            stdout.Flush();
            var result = runner.RunAll([invocation with { Arguments = Inserted(invocation.Arguments, report ?? []) }], root, stdout).Single();
            if (result.NotATestProject)
            {
                continue;
            }

            var outcome = report is null ? null : Collect(mode, root, reportName, results, userResults, before);
            runs.Add(new FullRun(invocation.Selection.Project, invocation.Location with { Mode = mode }, result, outcome, outputDirectory));
        }

        return runs;
    }

    /// <summary>The full run of one project next to the selection's decisions for it.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="analysis">The selection, or null without one.</param>
    /// <param name="run">The project's full run.</param>
    private static ShadowProject Compare(string root, AnalysisResult? analysis, FullRun run)
    {
        var path = Path.GetRelativePath(root, run.Project.Path).Replace('\\', '/');
        // The full run's projects come from the analysis's own graph, so their paths match exactly.
        var selection = analysis?.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, run.Project.Path, StringComparison.Ordinal));
        var mode = (analysis, selection?.Mode) switch
        {
            (null, _) => "unknown",
            (_, TestRunMode.All) => "all",
            (_, TestRunMode.Filtered) => "filtered",
            (_, TestRunMode.Skipped) => "skipped",
            _ => "not-affected",
        };
        var reason = analysis is null ? "no selection" : selection?.Reason ?? "the change does not reach this project";
        var decisions = selection?.Decisions.ToDictionary(static d => d.TestClass, StringComparer.Ordinal) ?? [];
        var selectedClasses = new HashSet<string>(selection?.TestClasses ?? [], StringComparer.Ordinal);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        names.UnionWith(run.Outcome?.ReportedClasses ?? new HashSet<string>());
        names.UnionWith(decisions.Keys);

        var classes = new List<ShadowClass>(names.Count);
        foreach (var name in names)
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
            var failed = run.Outcome?.FailedClasses.Contains(name) == true;
            var seconds = run.Outcome is { } outcome ? outcome.ClassSeconds.GetValueOrDefault(name) : (double?)null;
            ShadowVerdict? verdict = !failed || analysis is null ? null : selected ? ShadowVerdict.Caught : ShadowVerdict.Miss;
            classes.Add(new ShadowClass(name, selected, said.Kind, said.Evidence, seconds, failed) { Verdict = verdict });
        }

        return new ShadowProject(path, mode, reason, run.Result.ExitCode, run.Result.Elapsed.TotalSeconds, run.Outcome is not null, classes)
        {
            Estimate = selection?.Estimate,
        };
    }

    /// <summary>What every classification step of one shadow run shares.</summary>
    /// <param name="Root">Repository root.</param>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="UserArguments">The user's <c>dotnet test</c> arguments.</param>
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
        var (report, userResults) = ReportArguments(location.Mode, run.OutputDirectory, results, reportName, arguments, location.FullDirectory(treeRoot));
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
    /// The arguments that make a run write a report TestDetta can read (null when it can write none), and the
    /// user's own results directory if they name one. Nothing the user asked for is replaced: a VSTest run with the user's own TRX logger or
    /// results directory keeps them and the report is found there; a testing-platform run that already asks
    /// for a TRX report gets none from TestDetta (its file name is the user's to choose), and is not read.
    /// </summary>
    /// <param name="mode">Runner.</param>
    /// <param name="outputDirectory">Build output of a testing-platform project, or null.</param>
    /// <param name="results">TestDetta's folder for the report.</param>
    /// <param name="reportName">Unique report file name (testing platform).</param>
    /// <param name="userArguments">The user's arguments.</param>
    /// <param name="runDirectory">Directory the run starts in, for a relative results directory.</param>
    private static (List<string>? Arguments, string? UserResults) ReportArguments(DotnetTestMode mode, string? outputDirectory, string results, string reportName, IReadOnlyList<string> userArguments, string runDirectory)
    {
        var options = BeforeRunSettings(userArguments);
        if (mode == DotnetTestMode.VSTest)
        {
            var userResults = ValueOf(options, "--results-directory");
            var hasTrx = options.Select((a, i) => (a, i)).Any(p => p.a == "--logger" && p.i + 1 < options.Count && options[p.i + 1].StartsWith("trx", StringComparison.OrdinalIgnoreCase));
            List<string> arguments = hasTrx ? [] : ["--logger", "trx"];
            if (userResults is null)
            {
                arguments.AddRange(["--results-directory", results]);
                return (arguments, null);
            }

            // The user's folder stays; the report is found there among the files the run added.
            return (arguments, Path.GetFullPath(userResults, runDirectory));
        }

        if (outputDirectory is null || options.Any(static a => a.StartsWith("--report-trx", StringComparison.Ordinal) || a.StartsWith("--report-xunit-trx", StringComparison.Ordinal)))
        {
            return (null, null);
        }

        return TestReports.Arguments(mode, outputDirectory, results, reportName) is { Count: > 0 } platform ? (platform, null) : (null, null);
    }

    /// <summary>Report files already in the user's results directory before a run, so only the run's own are read.</summary>
    /// <param name="userResults">The user's results directory, or null.</param>
    private static HashSet<string> Snapshot(string? userResults) =>
        userResults is not null && Directory.Exists(userResults)
            ? Directory.GetFiles(userResults, "*.trx", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal)
            : [];

    /// <summary>Reads the report of a run, wherever it went.</summary>
    /// <param name="mode">Runner.</param>
    /// <param name="treeRoot">Root searched for a testing-platform report.</param>
    /// <param name="reportName">Its unique file name.</param>
    /// <param name="results">TestDetta's folder for the report.</param>
    /// <param name="userResults">The user's results directory, or null.</param>
    /// <param name="before">Reports that were in the user's directory before the run.</param>
    private static TrxOutcome? Collect(DotnetTestMode mode, string treeRoot, string reportName, string results, string? userResults, HashSet<string> before)
    {
        TestReports.Collect(mode, treeRoot, reportName, results);
        if (userResults is not null && Directory.Exists(userResults))
        {
            Directory.CreateDirectory(results);
            foreach (var file in Directory.GetFiles(userResults, "*.trx", SearchOption.AllDirectories).Where(f => !before.Contains(f)))
            {
                File.Copy(file, Path.Combine(results, Guid.NewGuid().ToString("N") + ".trx"));
            }
        }

        return TrxReport.Read(results);
    }

    /// <summary>
    /// The build output of a testing-platform test project, to see which report extension it has (an unknown
    /// option would fail the run). Null when it cannot be found; the project then runs without a report.
    /// </summary>
    /// <param name="invocation">The project's full-run invocation.</param>
    /// <param name="root">Repository root.</param>
    /// <param name="userArguments">The user's arguments, for <c>-c</c> and <c>-f</c>.</param>
    private string? OutputDirectory(TestInvocation invocation, string root, IReadOnlyList<string> userArguments)
    {
        var options = BeforeRunSettings(userArguments);
        var configuration = ValueOf(options, "-c") ?? ValueOf(options, "--configuration") ?? "Debug";
        List<string> query = ["msbuild", invocation.Selection.Project.Path, $"-p:Configuration={configuration}", "-getProperty:TargetPath", "-getProperty:TargetFrameworks"];
        if ((ValueOf(options, "-f") ?? ValueOf(options, "--framework")) is { } framework)
        {
            query.Add($"-p:TargetFramework={framework}");
        }

        return Try(() =>
        {
            var dotnet = new DotnetProcess(_logger);
            using var properties = System.Text.Json.JsonDocument.Parse(dotnet.Capture(query, invocation.Location.FullDirectory(root)));
            var values = properties.RootElement.GetProperty("Properties");
            if (values.GetProperty("TargetPath").GetString() is not { Length: > 0 } targetPath)
            {
                var first = values.GetProperty("TargetFrameworks").GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is null)
                {
                    return null;
                }

                targetPath = dotnet.Capture([.. query.Where(static q => !q.StartsWith("-getProperty", StringComparison.Ordinal)), $"-p:TargetFramework={first}", "-getProperty:TargetPath"], invocation.Location.FullDirectory(root)).Trim();
            }

            return Path.GetDirectoryName(targetPath);
        });
    }

    /// <summary>The user's arguments before a <c>--</c>; those after it are run settings.</summary>
    /// <param name="userArguments">The user's arguments.</param>
    private static List<string> BeforeRunSettings(IReadOnlyList<string> userArguments) => [.. userArguments.TakeWhile(static a => a != "--")];

    /// <summary>The value after <paramref name="option"/> (or in its <c>=</c> or <c>:</c> form), or null.</summary>
    /// <param name="options">Arguments.</param>
    /// <param name="option">Option name.</param>
    private static string? ValueOf(List<string> options, string option)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i] == option && i + 1 < options.Count)
            {
                return options[i + 1];
            }

            foreach (var separator in (ReadOnlySpan<char>)['=', ':'])
            {
                if (options[i].StartsWith(option + separator, StringComparison.Ordinal))
                {
                    return options[i][(option.Length + 1)..];
                }
            }
        }

        return null;
    }

    /// <summary><paramref name="extra"/> placed before a <c>--</c> in <paramref name="arguments"/>, or at the end.</summary>
    /// <param name="arguments">A command line.</param>
    /// <param name="extra">Options to add.</param>
    private static List<string> Inserted(IReadOnlyList<string> arguments, IReadOnlyList<string> extra)
    {
        var separator = arguments.ToList().IndexOf("--");
        List<string> result = [.. arguments];
        result.InsertRange(separator < 0 ? result.Count : separator, extra);
        return result;
    }

    /// <summary>What a reader needs to check the result: versions, commits, maps and the command.</summary>
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
        List<string> shaping = [$"--base {options.Analysis.BaseRef}", $"--max-map-age {options.Analysis.MaxMapAge}", $"--drift-fallback {drift}"];
        if (options.Analysis.MapsDir is { } mapsDir)
        {
            shaping.Add($"--maps-dir {mapsDir}");
        }

        // The classification's limits decide which failures could be classified in time, so they are inputs too.
        shaping.AddRange(options.Test.ShadowClassify ? (options.Test.ShadowLimits ?? ShadowLimits.Default).Options : ["--shadow-no-classify"]);
        if (options.Test.DotnetTestArguments.Count > 0)
        {
            shaping.Add("-- " + string.Join(' ', options.Test.DotnetTestArguments));
        }

        var command = $"td affected --base {mergeBase ?? options.Analysis.BaseRef} --max-map-age {options.Analysis.MaxMapAge} --drift-fallback {drift} --format json";
        return new ShadowReproduction(version, binary, options.Analysis.BaseRef, mergeBase, head, maps, shaping, command);
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

    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    /// <param name="path">File.</param>
    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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
