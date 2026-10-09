using System.Reflection;
using Microsoft.Extensions.Logging;
using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>Entry point of the <c>td</c> command: parses arguments, runs the analysis, then prints or runs the result.</summary>
internal static partial class CliApp
{
    /// <summary>Exit code for success, including "no tests needed".</summary>
    private const int Success = 0;

    /// <summary>Exit code when git, the file system or a project file fails.</summary>
    private const int Failure = 1;

    /// <summary>Exit code for an invalid command line.</summary>
    private const int UsageError = 2;

    /// <summary>Exit code of <c>td test</c> when at least one <c>dotnet test</c> failed.</summary>
    private const int TestsFailed = 3;

    /// <summary>
    /// The tool's version as built: the package version and, after a <c>+</c>, the commit it was built from
    /// (the SDK adds it to the informational version). <c>td --version</c> prints it, and the shadow and audit
    /// reports record it, so a bug report names the exact build.
    /// </summary>
    internal static string Version { get; } =
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Runs the command and returns the process exit code.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="stdout">Receives the result.</param>
    /// <param name="stderr">Receives usage help, errors and diagnostic logs.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) => Run(args, stdout, stderr, CliHost.Default);

    /// <summary>Runs the command with the given view of the machine and returns the process exit code.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="stdout">Receives the result.</param>
    /// <param name="stderr">Receives usage help, errors and diagnostic logs.</param>
    /// <param name="host">The environment variables and home directory the license is read from.</param>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, CliHost host)
    {
        if (args is ["--version", ..])
        {
            stdout.WriteLine($"td {Version}");
            return Success;
        }

        if (args is ["-h" or "--help", ..])
        {
            stderr.WriteLine(CliOptions.Usage);
            return Success;
        }

        if (!CliOptions.TryParse(args, Directory.GetCurrentDirectory(), out var options, out var error))
        {
            stderr.WriteLine($"{error}\n\n{CliOptions.Usage}");
            return UsageError;
        }

        var outStyle = StyleFor(options.Display, host, stdout, error: false);
        using var loggerFactory = new StderrLoggerFactory(stderr, options.Analysis.LogLevel) { Style = StyleFor(options.Display, host, stderr, error: true) };
        var logger = loggerFactory.CreateLogger(typeof(CliApp).FullName!);
        LogOptions(logger, options);

        if (options.Command == CliCommand.Record)
        {
            return RecordCoverage(options, loggerFactory, stdout, stderr, host);
        }

        if (options.Command is CliCommand.LicenseStatus or CliCommand.LicenseInstall)
        {
            return LicenseCommand(options, loggerFactory, stdout, stderr, host);
        }

        // Shadow mode needs no license: it reads none, refreshes none and sends nothing (docs/design/shadow-mode.md).
        if (options.Command == CliCommand.Test && options.Test.Shadow)
        {
            return Shadow(options, loggerFactory, stdout, stderr);
        }

        // Nor does the audit, which skips nothing either (docs/design/audit.md).
        if (options.Command == CliCommand.Audit)
        {
            return Audit(options, loggerFactory, stdout);
        }

        // The license refresh waits on the network while the analysis works; the check below waits for it.
        var refresh = LicenseGate.Refresh(options.Analysis.RepoPath, loggerFactory, host.Environment, host.Home);

        AnalysisResult analysis;
        try
        {
            analysis = Analyze(options, loggerFactory, logger);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
                                       or UnauthorizedAccessException)
        {
            LogFailed(logger, ex);
            stderr.WriteLine($"td: {ex.Message}");
            return Failure;
        }

        var license = LicenseGate.Check(analysis.RepoRoot, loggerFactory, host.Environment, host.Home, refresh);
        analysis = LicenseGate.Apply(analysis, license, TestDetta.Core.Licensing.LicenseKeys.Enforced, logger);

        if (options.Command == CliCommand.Test)
        {
            return RunTests(options, analysis, loggerFactory, stdout, outStyle);
        }

        var stats = new RunStats(analysis.ChangedFiles.Count, analysis.Graph.Projects.Count);
        ResultWriter.Write(
            new ResultReport(options, analysis.RepoRoot, stats, analysis.Impact, analysis.Plan)
            {
                Drifts = analysis.Drifts,
                License = analysis.License,
                LicenseEnforced = analysis.LicenseEnforced,
                UnmappedFilesRead = analysis.UnmappedFilesRead,
                Style = outStyle,
            },
            stdout);
        return Success;
    }

    /// <summary>
    /// The console style of one of the command's streams: colour by <c>--color</c> and the environment, where the stream
    /// goes (a terminal, the process's stream redirected, or a writer of a caller such as a test), the theme, the
    /// characters its encoding can show, and the terminal's width.
    /// </summary>
    /// <param name="display">Display options from the command line.</param>
    /// <param name="host">Environment.</param>
    /// <param name="writer">The stream the command writes to.</param>
    /// <param name="error">True for stderr, false for stdout.</param>
    private static ConsoleStyle StyleFor(DisplaySettings display, CliHost host, TextWriter writer, bool error)
    {
        var console = error ? Console.Error : Console.Out;
        var kind = !ReferenceEquals(writer, console) ? StreamKind.Captured
            : (error ? Console.IsErrorRedirected : Console.IsOutputRedirected) ? StreamKind.Redirected
            : StreamKind.Terminal;
        int? width = null;
        if (kind == StreamKind.Terminal)
        {
            try
            {
                width = Console.WindowWidth;
            }
            catch (IOException)
            {
                // No console window after all: the default width.
            }
        }

        // A caller's writer (a StringWriter says UTF-16) holds text, not bytes for a console: it can take any character.
        return ConsoleStyle.For(display, host.Environment, kind, kind == StreamKind.Captured ? System.Text.Encoding.UTF8 : writer.Encoding, width);
    }

    /// <summary>Restores the maps (with <c>--maps-dir</c>) and runs the analysis.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="loggerFactory">Creates the loggers.</param>
    /// <param name="logger">Logger of the CLI.</param>
    /// <param name="cancel">Stops the analysis between its steps (shadow mode's time limit, Ctrl+C).</param>
    private static AnalysisResult Analyze(CliOptions options, ILoggerFactory loggerFactory, ILogger logger, CancellationToken cancel = default)
    {
        if (options.Analysis.MapsDir is { } mapsDir)
        {
            var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
            MapDirectory.Restore(Path.GetFullPath(mapsDir), git.TopLevel(options.Analysis.RepoPath), options.Analysis.BaseRef, options.Analysis.MaxMapAge, git, logger);
        }

        return new AnalysisPipeline(loggerFactory, new TestDetta.Coverage.MsBuildProjectEvaluator(loggerFactory)).Run(
            options.Analysis.RepoPath,
            options.Analysis.BaseRef,
            new AnalysisOptions
            {
                MaxMapAge = options.Analysis.MaxMapAge,
                DriftFallback = options.Analysis.DriftFallback,
                Cancellation = cancel,
                // td test builds anyway before it runs tests; td affected only when asked.
                GeneratedCode = new TestDetta.Coverage.BuildGeneratedCodeReader(
                    loggerFactory,
                    options.Command == CliCommand.Test
                        ? new TestDetta.Coverage.GeneratedCodeBuild(Build: true, options.Test.Configuration)
                        : new TestDetta.Coverage.GeneratedCodeBuild(options.Affected.Build)),
            });
    }

    /// <summary>
    /// Carries out <c>td test --shadow</c> (<see cref="ShadowRun"/>). No license is checked, refreshed or read, and
    /// TestDetta itself sends nothing anywhere. The analysis runs before the full run, on the tree as the pull request
    /// has it, but nothing of it is shown until that run has finished: its log is held, and the plan is printed with
    /// the report, for the classes that ran. An analysis that fails, or runs past <c>--shadow-analysis-timeout</c>,
    /// leaves the full run as it is and the report says why it has no shadow result. Ctrl+C and SIGTERM stop the
    /// analysis or the running tests, and the temp folder and the base worktree are removed before <c>td</c> exits.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="loggerFactory">Creates the loggers of the full run and the classification.</param>
    /// <param name="stdout">Receives the test output and the report.</param>
    /// <param name="stderr">Receives the held analysis log after the full run.</param>
    private static int Shadow(CliOptions options, ILoggerFactory loggerFactory, TextWriter stdout, TextWriter stderr)
    {
        using var cancel = new CancellationTokenSource();
        void Stop(System.Runtime.InteropServices.PosixSignalContext context)
        {
            // Shadow mode ends itself: the process must live on to remove its temp folder and the base worktree.
            context.Cancel = true;
            cancel.Cancel();
        }

        using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, Stop);
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, Stop);
        var analysis = ShadowAnalyze(options, stdout, cancel.Token);
        return new ShadowRun(loggerFactory, stdout, stderr, new ShadowHooks(), cancel.Token).Run(options, analysis);
    }

    /// <summary>
    /// How long an analysis that ran past its time limit is waited for after it was asked to stop: long enough for the
    /// step it is in (an MSBuild evaluation, the build of a generator's consumer) to end, so it does not build next to
    /// the team's run; one that does not stop by then is left behind and the tests start anyway.
    /// </summary>
    private static readonly TimeSpan AnalysisGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Shadow mode's analysis, within <see cref="ShadowLimits.Analysis"/>. Its log goes into memory (see
    /// <see cref="ShadowAnalysis.Log"/>); an analysis stopped at the limit leaves no log, since what it logged so far
    /// could name decisions.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="stdout">Receives one line when there is no selection.</param>
    /// <param name="cancel">Ctrl+C or SIGTERM.</param>
    /// <param name="analyze">The analysis (logger factory, logger, stop); null for <see cref="Analyze"/>. Tests pass one that hangs.</param>
    internal static ShadowAnalysis ShadowAnalyze(CliOptions options, TextWriter stdout, CancellationToken cancel, Func<ILoggerFactory, ILogger, CancellationToken, AnalysisResult>? analyze = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var limit = (options.Test.ShadowLimits ?? ShadowLimits.Default).Analysis;
        var held = new StringWriter();

        // Not disposed when the analysis is left behind: it may still log into it.
        var analysisLogs = new StderrLoggerFactory(held, options.Analysis.LogLevel);
        var analysisLogger = analysisLogs.CreateLogger(typeof(CliApp).FullName!);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        analyze ??= (factory, logger, token) => Analyze(options, factory, logger, token);
        var analyzing = Task.Run(() => analyze(analysisLogs, analysisLogger, stop.Token), CancellationToken.None);
        bool finished;
        try
        {
            finished = analyzing.Wait(limit is { } time ? (int)Math.Min(time.TotalMilliseconds, int.MaxValue) : Timeout.Infinite, cancel);
        }
        catch (OperationCanceledException)
        {
            finished = false;
        }
        catch (AggregateException)
        {
            finished = true;
        }

        if (!finished)
        {
            stop.Cancel();
            var ended = analyzing.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Wait((int)AnalysisGrace.TotalMilliseconds, CancellationToken.None);
            var error = cancel.IsCancellationRequested
                ? "the run was cancelled during the analysis"
                : $"the analysis did not finish within {Durations.DescribeLimit(limit)} (--shadow-analysis-timeout){(ended ? string.Empty : "; it did not stop and was left behind")}";
            if (!cancel.IsCancellationRequested)
            {
                stdout.WriteLine($"td: {error}; every test still runs.");
            }

            return new ShadowAnalysis(null, error, stopwatch.Elapsed.TotalSeconds, string.Empty);
        }

        using (analysisLogs)
        {
            if (analyzing.Exception?.InnerException is { } ex)
            {
                LogFailed(analysisLogger, ex);
                var error = $"the analysis failed ({ex.GetType().Name}: {ex.Message})";
                stdout.WriteLine($"td: {error}; every test still runs.");
                return new ShadowAnalysis(null, error, stopwatch.Elapsed.TotalSeconds, held.ToString());
            }
        }

        return new ShadowAnalysis(analyzing.Result, null, stopwatch.Elapsed.TotalSeconds, held.ToString());
    }

    /// <summary>
    /// Carries out <c>td audit</c> (<see cref="AuditRun"/>) without a license, cancelled by Ctrl+C or SIGTERM: the running
    /// tests are killed, the work folder with the working copy is deleted and the partial report is written.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="loggerFactory">Creates the loggers.</param>
    /// <param name="stdout">Receives progress and the report.</param>
    private static int Audit(CliOptions options, ILoggerFactory loggerFactory, TextWriter stdout)
    {
        using var cancel = new CancellationTokenSource();
        void Stop(System.Runtime.InteropServices.PosixSignalContext context)
        {
            // The audit ends itself: the process must live on to restore and delete the copy.
            context.Cancel = true;
            cancel.Cancel();
        }

        using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, Stop);
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, Stop);
        return new AuditRun(loggerFactory, stdout, new AuditHooks(), cancel.Token).Run(options);
    }

    /// <summary>
    /// Carries out <c>td license status</c> (what the check finds, without personal data) and
    /// <c>td license install</c> (stores a license that verifies in <c>~/.testdetta/license</c>).
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="loggerFactory">Creates the git client's logger.</param>
    /// <param name="stdout">Receives the result.</param>
    /// <param name="stderr">Receives errors.</param>
    /// <param name="host">The environment variables and home directory the license is read from.</param>
    private static int LicenseCommand(CliOptions options, ILoggerFactory loggerFactory, TextWriter stdout, TextWriter stderr, CliHost host)
    {
        var home = host.Home;
        if (options.Command == CliCommand.LicenseInstall)
        {
            var value = options.License.Install!;
            var text = TestDetta.Core.Licensing.License.Normalize(File.Exists(value) ? File.ReadAllText(value) : value);
            if (TestDetta.Core.Licensing.License.Verify(text, TestDetta.Core.Licensing.LicenseKeys.Trusted, out var error) is not { } claims)
            {
                stderr.WriteLine($"td: the license is not valid: {error}");
                return Failure;
            }

            var path = TestDetta.Core.Licensing.LicenseInputs.InstalledPath(home);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            stdout.WriteLine($"Installed license {claims.Id} ({claims.Licensee}, {claims.Seats} seats, until {claims.ValidUntil:yyyy-MM-dd}) in {path}.");
            return Success;
        }

        string root;
        try
        {
            root = new GitClient(loggerFactory.CreateLogger<GitClient>()).TopLevel(options.Analysis.RepoPath);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"td: {ex.Message}");
            return Failure;
        }

        var status = LicenseGate.Check(root, loggerFactory, host.Environment, home);
        stdout.WriteLine($"License: {status.State}");
        stdout.WriteLine($"  {status.Message}");
        if (status.Claims is { } license)
        {
            stdout.WriteLine($"  id {license.Id}, {license.Licensee}, plan {license.Plan}, {license.Kind}, {license.Seats} seats, valid {license.ValidFrom:yyyy-MM-dd} to {license.ValidUntil:yyyy-MM-dd}");
            stdout.WriteLine($"  scopes: {string.Join(", ", license.Scopes)}");
        }

        if (status.ActiveCommitters is { } active)
        {
            stdout.WriteLine($"  active committers in the last 30 days: {active} (bots excluded: {string.Join(", ", TestDetta.Core.Licensing.ActiveCommitters.BuiltInBots)}, plus .testdetta/license.json)");
        }

        stdout.WriteLine(status.AllowsSelection
            ? "  Tests are selected."
            : TestDetta.Core.Licensing.LicenseKeys.Enforced ? "  Every test runs until this is fixed." : "  Not enforced yet: tests are still selected, whatever the check above finds.");
        return Success;
    }

    /// <summary>Carries out <c>td test</c>: prints the plan, runs (or only prints) the invocations, prints a summary.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="loggerFactory">Creates the runner's logger.</param>
    /// <param name="stdout">Receives the plan and the summary.</param>
    /// <param name="style">Colour, characters and width of <paramref name="stdout"/>.</param>
    private static int RunTests(CliOptions options, AnalysisResult analysis, ILoggerFactory loggerFactory, TextWriter stdout, ConsoleStyle style)
    {
        var invocations = TestInvocationBuilder.Build(analysis.Tests, analysis.RepoRoot, options.Test.DotnetTestArguments, analysis.Graph);
        TextReport.WritePlan(analysis, stdout, style);

        if (invocations.Count == 0)
        {
            stdout.WriteLine("No tests need to run for these changes.");
            AppendSummary(options, analysis, results: null);
            return Success;
        }

        if (options.Test.DryRun)
        {
            foreach (var invocation in invocations)
            {
                stdout.WriteLine(ResultWriter.CommandLine(invocation));
            }

            AppendSummary(options, analysis, results: null);
            return Success;
        }

        stdout.Flush();
        var results = new TestRunner(loggerFactory.CreateLogger<TestRunner>()).RunAll(invocations, analysis.RepoRoot, stdout);
        TextReport.WriteRunSummary(new RunSummary(analysis.RepoRoot, analysis.Tests, results, analysis.Plan), stdout, style);
        AppendSummary(options, analysis, results);
        return results.Any(static r => r.ExitCode != 0) ? TestsFailed : Success;
    }

    /// <summary>Appends the Markdown summary when <c>--summary-file</c> was given.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="results">Test results, or null when nothing ran.</param>
    private static void AppendSummary(CliOptions options, AnalysisResult analysis, IReadOnlyList<TestRunResult>? results)
    {
        if (options.Test.SummaryFile is not { } path)
        {
            return;
        }

        using var writer = File.AppendText(path);
        MarkdownSummary.Write(new SummaryInput(analysis, options.Analysis.BaseRef, results), writer);
    }

    /// <summary>
    /// Carries out <c>td record</c>: records one coverage map per test project. A project whose
    /// tests fail is still recorded, because failing tests ran the code they cover. A project that
    /// does not build or run here (a mobile app that needs a workload the machine lacks, a recording
    /// that hangs) gets no map and the others are still recorded: selection decides a project without
    /// a map by name, which is wider but safe. The run fails only when no map was written at all.
    /// With <c>--incremental</c>, each project records only the classes the commits since its previous
    /// map can affect, or keeps that map, or records in full (docs/design/incremental-recording.md).
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="loggerFactory">Creates the recorder's loggers.</param>
    /// <param name="stdout">Receives build and test output and the summary.</param>
    /// <param name="stderr">Receives errors.</param>
    /// <param name="host">Environment and home directory, for the license an incremental recording's selection needs.</param>
    private static int RecordCoverage(
        CliOptions options,
        StderrLoggerFactory loggerFactory,
        TextWriter stdout,
        TextWriter stderr,
        CliHost host)
    {
        var logger = loggerFactory.CreateLogger(typeof(CliApp).FullName!);
        try
        {
            var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
            var root = git.TopLevel(options.Analysis.RepoPath);
            var evaluator = new TestDetta.Coverage.MsBuildProjectEvaluator(loggerFactory);
            var graph = new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>(), evaluator).Load(root);
            var testProjects = SelectTestProjects(graph, root, options.Record.Projects);
            if (testProjects.Count == 0)
            {
                stderr.WriteLine("td: no test project to record.");
                return Failure;
            }

            var head = git.Head(root);
            var now = DateTimeOffset.UtcNow;
            List<ProjectStep> steps;
            if (options.Record.Incremental)
            {
                if (options.Analysis.MapsDir is { } restoreFrom)
                {
                    MapDirectory.Restore(Path.GetFullPath(restoreFrom), root, "HEAD", options.Analysis.MaxMapAge, git, logger);
                }

                var license = LicenseGate.Check(root, loggerFactory, host.Environment, host.Home);
                var licensed = license.AllowsSelection || !TestDetta.Core.Licensing.LicenseKeys.Enforced;
                steps = new IncrementalRecording(loggerFactory, evaluator)
                    .Plan(new IncrementalContext(root, graph, options.Record, options.Analysis.MaxMapAge, licensed), testProjects, now);
            }
            else
            {
                steps = [.. testProjects.Select(static p => new ProjectStep(p, StepKind.Full, string.Empty, null, []))];
            }

            var recorder = new TestDetta.Coverage.CoverageRecorder(loggerFactory);
            var allPassed = true;
            var notRecorded = 0;
            var rows = new List<RecordRow>();
            var saved = (string?)null;
            var written = new List<string>();

            foreach (var step in steps)
            {
                var project = step.Project;
                var path = CoverageMapStore.PathFor(root, project.Path);
                var row = new RecordRow(TextReport.Display(root, project.Path), options.Record.Incremental ? step.Kind.ToString().ToLowerInvariant() : null);
                rows.Add(row);
                if (options.Record.Incremental)
                {
                    row.Notes.Add(new Cell(step.Reason, Role.Muted));
                }

                if (step.Kind == StepKind.Current)
                {
                    row.Result = new Cell("kept", Role.Good);
                    row.Classes = step.Previous?.Classes.Count;
                    written.Add(path);
                    continue;
                }

                if (step.Kind == StepKind.Carry)
                {
                    var previous = step.Previous!;
                    var carried = previous with
                    {
                        Header = previous.Header with { Commit = head, RecordedAt = now },
                        Unobserved = [.. new SortedSet<int>([.. previous.Unobserved ?? [], .. Ids(previous.Methods, CoverageMapMerge.UnobservedBy(step.Changes, previous.Methods))])],
                        Lineage = previous.Lineage is { } lineage
                            ? lineage with { Steps = lineage.Steps + 1 }
                            : new MapLineage(previous.Commit, previous.Header.RecordedAt, 1),
                    };
                    CoverageMapStore.Write(path, carried);
                    row.Result = new Cell("carried over", Role.Good);
                    row.Classes = carried.Classes.Count;
                    row.Methods = carried.Methods.Count;
                    written.Add(path);
                    continue;
                }

                var recordOptions = new TestDetta.Coverage.RecordOptions(options.Record.Configuration, options.Record.Framework, options.Record.Parallelism)
                {
                    NoBuild = options.Record.NoBuild,
                    Classes = step.Kind == StepKind.Partial ? step.Classes : null,
                };
                CoverageMap map;
                bool passed;
                try
                {
                    var request = new TestDetta.Coverage.RecordRequest(root, graph, project, recordOptions);
                    if (step.Kind == StepKind.Partial)
                    {
                        var partial = recorder.RecordClasses(request, stdout);
                        passed = partial.TestsPassed;
                        map = partial.Complete
                            ? partial.Map
                            : CoverageMapMerge.Merge(step.Previous!, partial.Map, new MapMergeScope(
                                step.Classes.ToHashSet(StringComparer.Ordinal),
                                partial.AssemblyClasses,
                                CoverageMapMerge.UnobservedBy(step.Changes, partial.Map.Methods)));
                    }
                    else
                    {
                        (map, passed) = recorder.Record(request, stdout);
                    }
                }
                catch (TestDetta.Coverage.NoTestsRecordedException ex)
                {
                    // Not a reason to fail the run: selection decides for this project by name.
                    row.Result = new Cell("skipped", Role.Muted);
                    row.Notes.Add(new Cell(ex.Message, Role.Muted));
                    continue;
                }
                catch (InvalidOperationException ex)
                {
                    LogNotRecorded(logger, project.Path, ex.Message);
                    row.Result = new Cell("not recorded", Role.Bad);
                    row.Notes.Add(new Cell(ex.Message, Role.Bad));
                    row.Notes.Add(new Cell("selection decides this project by name until it has a map", Role.Muted));
                    notRecorded++;
                    continue;
                }

                CoverageMapStore.Write(path, map);
                written.Add(path);

                allPassed &= passed;
                row.Result = passed ? new Cell("recorded", Role.Good) : new Cell("recorded, tests FAILED", Role.Bad);
                row.Classes = map.Classes.Count;
                row.Methods = map.Methods.Count;
                switch (map.FailedClasses)
                {
                    case null:
                        row.Notes.Add(new Cell(
                            "tests failed and no TRX report says which, so this map will not be used; " +
                            "under Microsoft.Testing.Platform add Microsoft.Testing.Extensions.TrxReport to the test project", Role.Bad));
                        break;
                    case { Count: > 0 } failed:
                        row.Notes.Add(new Cell($"{failed.Count} test class(es) failed while recording; they run whenever the project is affected", Role.Notice));
                        break;
                }

                if (map.SkippedClasses is { Count: > 0 } skipped)
                {
                    row.Notes.Add(new Cell($"{skipped.Count} test class(es) had every test skipped on {map.Platform}; on other platforms they run whenever the project is affected", Role.Muted));
                }

                if (map.Lineage is null && step.Previous is { Lineage: not null } incremental)
                {
                    row.Notes.AddRange(Divergences(incremental, map));
                }
            }

            if (options.Analysis.MapsDir is { } mapsDir && written.Count > 0)
            {
                MapDirectory.Save(Path.GetFullPath(mapsDir), head, written, logger);
                saved = $"Saved {written.Count} map(s) to {Path.Combine(mapsDir, head)}";
            }

            TextReport.WriteRecordSummary(new RecordSummary(rows, options.Record.Incremental, CoverageMapStore.Directory, saved), stdout, StyleFor(options.Display, host, stdout, error: false));

            if (written.Count == 0 && notRecorded > 0)
            {
                stderr.WriteLine("td: no test project could be recorded.");
                return Failure;
            }

            return allPassed ? Success : TestsFailed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            LogFailed(logger, ex);
            stderr.WriteLine($"td: {ex.Message}");
            return Failure;
        }
    }

    /// <summary>The ids of <paramref name="members"/> in <paramref name="methods"/>.</summary>
    /// <param name="methods">A map's method list.</param>
    /// <param name="members">Members to look up; those the list lacks are left out.</param>
    private static IEnumerable<int> Ids(IReadOnlyList<CoveredMethod> methods, IReadOnlySet<CoveredMethod> members)
    {
        for (var i = 0; i < methods.Count; i++)
        {
            if (members.Contains(methods[i]))
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// Summary lines for a full recording that replaced an incremental map: the classes that ran members the incremental
    /// map did not credit them with (docs/design/incremental-recording.md), the first ones in detail.
    /// </summary>
    /// <param name="incremental">The map built by incremental steps.</param>
    /// <param name="full">The full recording that replaces it.</param>
    private static IEnumerable<Cell> Divergences(CoverageMap incremental, CoverageMap full)
    {
        var divergences = MapDivergence.Find(incremental, full);
        if (divergences.Count == 0)
        {
            yield return new Cell($"checked the incremental map ({incremental.Lineage!.Steps} step(s)) against this full recording: no class ran code it lacked", Role.Good);
            yield break;
        }

        yield return new Cell(
            $"{divergences.Count} class(es) ran code the incremental map ({incremental.Lineage!.Steps} step(s)) did not credit them with " +
            "(nondeterministic tests, or a selection gap worth reporting):", Role.Notice);
        foreach (var divergence in divergences.Take(10))
        {
            var members = string.Join(", ", divergence.Missing.Take(5)) + (divergence.Missing.Count > 5 ? $" and {divergence.Missing.Count - 5} more" : string.Empty);
            yield return new Cell($"  {divergence.TestClass}: {members}", Role.Notice);
        }
    }

    /// <summary>All test projects, or only those named on the command line.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="root">Repository root as git reports it; relative paths are resolved against it.</param>
    /// <param name="projects">Paths given with <c>--project</c>; empty means all.</param>
    /// <exception cref="InvalidOperationException">A named project is not a test project of the repository.</exception>
    /// <remarks>
    /// Relative paths are taken from the repository root, not the current directory: the graph uses
    /// git's spelling of the root, which differs from the current directory's when it is reached
    /// through a symlink (the temp folder on macOS).
    /// </remarks>
    private static List<ProjectNode> SelectTestProjects(ProjectGraph graph, string root, IReadOnlyList<string> projects)
    {
        var result = new List<ProjectNode>();
        if (projects.Count == 0)
        {
            foreach (var node in graph.Projects)
            {
                if (node.IsTestProject)
                {
                    result.Add(node);
                }
            }
        }

        foreach (var project in projects)
        {
            var wanted = Path.GetFullPath(Path.IsPathRooted(project) ? project : Path.Combine(root, project)).Replace('\\', '/');
            var node = graph.Projects.FirstOrDefault(n => n.IsTestProject && string.Equals(n.Path, wanted, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException($"{project} is not a test project of this repository.");
            result.Add(node);
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Options: {Options}")]
    private static partial void LogOptions(ILogger logger, CliOptions options);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analysis failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} was not recorded and gets no coverage map: {Reason}")]
    private static partial void LogNotRecorded(ILogger logger, string project, string reason);
}
