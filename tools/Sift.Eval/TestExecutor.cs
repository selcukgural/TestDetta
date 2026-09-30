using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Eval;

/// <summary>Result of running tests of one or more projects.</summary>
/// <param name="Seconds">Wall-clock time of all runs, including dotnet test start-up.</param>
/// <param name="Total">Tests that ran, according to the TRX reports.</param>
/// <param name="FailedByProject">Failed test classes per normalized test project path.</param>
/// <param name="MissingReports">Runs that produced no TRX report (for example NUnit under Microsoft.Testing.Platform).</param>
/// <param name="Aborted">Runs whose test host crashed twice or hung; their results are incomplete.</param>
internal sealed record TestRunOutcome(
    double Seconds,
    int Total,
    IReadOnlyDictionary<string, IReadOnlySet<string>> FailedByProject,
    int MissingReports,
    int Aborted);

/// <summary>What <see cref="TestExecutor"/> runs in.</summary>
/// <param name="RepoRoot">Clone directory.</param>
/// <param name="Mode">Runner <c>dotnet test</c> uses there.</param>
/// <param name="Settings">Evaluation settings, for the cleanup command.</param>
/// <param name="LogDirectory">Where each run's console output is written.</param>
internal sealed record ExecutionContext(string RepoRoot, DotnetTestMode Mode, RunSettings Settings, string LogDirectory);

/// <summary>
/// Runs already built test projects with a TRX report, so failures are known per test class, and
/// runs the cleanup command after every run.
/// </summary>
/// <param name="context">Where and how to run.</param>
/// <param name="logger">Receives every run.</param>
internal sealed partial class TestExecutor(ExecutionContext context, ILogger logger)
{
    /// <summary>Counter for unique results directories and log files.</summary>
    private int _runs;

    /// <summary>
    /// Longest run seen per test project, in seconds. A run may take five times as long (at least two
    /// minutes) before it counts as hung: a mutant can make a test wait forever, for a retry that never
    /// succeeds or a task that never completes.
    /// </summary>
    private readonly Dictionary<string, double> _longest = new(Paths.Comparer);

    /// <summary>Shortest time limit of a run, however fast the project ran before; tests lower it.</summary>
    internal static TimeSpan MinimumTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Directory for console logs of builds and runs.</summary>
    public string LogDirectory => context.LogDirectory;

    /// <summary>
    /// Output directory per normalized test project path of the commit being evaluated, from MSBuild
    /// (projects may use the artifacts layout rather than <c>bin/Debug</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Outputs { get; set; } = new Dictionary<string, string>();

    /// <summary>Runs every test of <paramref name="projects"/>, one project after another.</summary>
    /// <param name="projects">Test projects.</param>
    /// <param name="label">Name for log files, such as <c>full</c> or <c>mutant-2</c>.</param>
    public TestRunOutcome RunAll(IReadOnlyList<ProjectNode> projects, string label)
    {
        var runs = new List<(ProjectNode Project, List<string> Arguments)>(projects.Count);
        foreach (var project in projects)
        {
            var relative = Path.GetRelativePath(context.RepoRoot, project.Path).Replace('\\', '/');
            List<string> arguments = context.Mode == DotnetTestMode.TestingPlatform ? ["test", "--project", relative] : ["test", relative];
            runs.Add((project, arguments));
        }

        return Run(runs, label);
    }

    /// <summary>Runs the invocations Sift selected.</summary>
    /// <param name="invocations">From <see cref="TestInvocationBuilder"/>.</param>
    /// <param name="label">Name for log files.</param>
    public TestRunOutcome RunSelected(IReadOnlyList<TestInvocation> invocations, string label)
    {
        var runs = new List<(ProjectNode Project, List<string> Arguments)>(invocations.Count);
        foreach (var invocation in invocations)
        {
            runs.Add((invocation.Selection.Project, [.. invocation.Arguments]));
        }

        return Run(runs, label);
    }

    /// <summary>Runs each project with <c>--no-build</c> and a TRX report, and reads the reports.</summary>
    /// <param name="runs">Project and <c>dotnet test</c> arguments per run.</param>
    /// <param name="label">Name for log files.</param>
    private TestRunOutcome Run(List<(ProjectNode Project, List<string> Arguments)> runs, string label)
    {
        var stopwatch = Stopwatch.StartNew();
        var total = 0;
        var missing = 0;
        var aborted = 0;
        var failed = new Dictionary<string, IReadOnlySet<string>>(Paths.Comparer);
        var dotnet = new DotnetProcess(logger);

        foreach (var (project, baseArguments) in runs)
        {
            baseArguments.Add("--no-build");
            if (context.Settings.Framework is { } framework)
            {
                baseArguments.AddRange(["-f", framework]);
            }

            // A crashed test host leaves the run incomplete; some suites crash now and then, so the
            // run is repeated once before it is reported as aborted.
            TrxOutcome? outcome = null;
            var crashed = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var id = Interlocked.Increment(ref _runs);
                var results = Path.Combine(context.LogDirectory, "trx", $"{id:000}-{label}");

                // A rerun in the same output directory reuses run numbers; an old report would add its
                // tests and failures to this run.
                if (Directory.Exists(results))
                {
                    Directory.Delete(results, recursive: true);
                }

                var reportName = $"sift-{id:000}-{label}.trx";
                List<string> arguments = [.. baseArguments, .. ReportArguments(project, results, reportName)];
                var logPath = Path.Combine(context.LogDirectory, $"{id:000}-{label}-{Path.GetFileNameWithoutExtension(project.Path)}.log");
                int exitCode;
                var started = Stopwatch.GetTimestamp();
                using (var log = File.CreateText(logPath))
                {
                    exitCode = dotnet.Run(arguments, context.RepoRoot, new Dictionary<string, string>(), log, Timeout(project.Path));
                    LogRun(label, project.Path, exitCode);
                }

                Cleanup();
                CollectReport(reportName, results);
                outcome = TrxReport.Read(results);
                if (exitCode == DotnetProcess.TimedOut)
                {
                    // A hang repeats; a second attempt would only double the wait.
                    crashed = true;
                    break;
                }

                var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                _longest[project.Path] = Math.Max(_longest.GetValueOrDefault(project.Path), seconds);
                crashed = File.ReadLines(logPath).Any(static l =>
                    l.Contains("Test host process crashed", StringComparison.Ordinal) || l.Contains("Test Run Aborted", StringComparison.Ordinal));
                if (!crashed)
                {
                    break;
                }

                LogCrashed(label, project.Path, attempt + 1);
            }

            aborted += crashed ? 1 : 0;
            if (outcome is null)
            {
                missing++;
                continue;
            }

            total += outcome.Total;
            failed[project.Path] = outcome.FailedClasses;
        }

        return new TestRunOutcome(stopwatch.Elapsed.TotalSeconds, total, failed, missing, aborted);
    }

    /// <summary>Time limit of a run of <paramref name="project"/>; none before the project ran once.</summary>
    /// <param name="project">Normalized test project path.</param>
    private TimeSpan? Timeout(string project) =>
        _longest.TryGetValue(project, out var seconds)
            ? TimeSpan.FromSeconds(Math.Max(MinimumTimeout.TotalSeconds, seconds * 5))
            : null;

    /// <summary>
    /// Arguments that make the run write a TRX report: VSTest's logger, or the testing platform's
    /// report option, which xUnit v3 names differently.
    /// </summary>
    /// <param name="project">Test project.</param>
    /// <param name="results">Results directory (VSTest only).</param>
    /// <param name="reportName">Report file name (testing platform only).</param>
    /// <remarks>
    /// Under the testing platform no results directory is passed: repositories may already set one
    /// in their build, and a second <c>--results-directory</c> makes the command line invalid. The
    /// report gets a unique name instead and <see cref="CollectReport"/> finds it.
    /// </remarks>
    private List<string> ReportArguments(ProjectNode project, string results, string reportName)
    {
        if (context.Mode == DotnetTestMode.VSTest)
        {
            return ["--logger", "trx", "--results-directory", results];
        }

        var isXunitV3 = Outputs.TryGetValue(project.Path, out var output) && File.Exists(Path.Combine(output, "xunit.v3.core.dll"));
        return isXunitV3
            ? ["--report-xunit-trx", "--report-xunit-trx-filename", reportName]
            : ["--report-trx", "--report-trx-filename", reportName];
    }

    /// <summary>
    /// Moves a testing-platform report named <paramref name="reportName"/> from wherever the run wrote
    /// it (the repository's own results directory, <c>TestResults</c> next to the output) into
    /// <paramref name="results"/>.
    /// </summary>
    /// <param name="reportName">Unique report file name.</param>
    /// <param name="results">Directory the report is read from.</param>
    private void CollectReport(string reportName, string results)
    {
        if (context.Mode == DotnetTestMode.VSTest)
        {
            return;
        }

        var found = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(context.RepoRoot, reportName, found))
        {
            Directory.CreateDirectory(results);
            File.Move(file, Path.Combine(results, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>Runs the cleanup command, if any, and waits for it.</summary>
    public void Cleanup() => RunShell(context.Settings.Commands.Cleanup, "cleanup");

    /// <summary>Runs the setup command, if any, after a checkout.</summary>
    public void Setup() => RunShell(context.Settings.Commands.Setup, "setup");

    /// <summary>Runs a shell command in the clone and waits for it; its output is discarded.</summary>
    /// <param name="command">Command, or null for none.</param>
    /// <param name="label">Name for the log.</param>
    private void RunShell(string? command, string label)
    {
        if (command is not { Length: > 0 })
        {
            return;
        }

        var psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = context.RepoRoot, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start the {label} command.");

        // Both streams are drained, so a chatty command cannot block on a full pipe.
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        LogShell(label, process.ExitCode);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label}: {Project} exited with {ExitCode}")]
    private partial void LogRun(string label, string project, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Label}: the test host of {Project} crashed (attempt {Attempt})")]
    private partial void LogCrashed(string label, string project, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The {Label} command exited with {ExitCode}")]
    private partial void LogShell(string label, int exitCode);
}
