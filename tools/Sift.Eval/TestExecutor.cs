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
internal sealed record TestRunOutcome(
    double Seconds,
    int Total,
    IReadOnlyDictionary<string, IReadOnlySet<string>> FailedByProject,
    int MissingReports);

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
        var failed = new Dictionary<string, IReadOnlySet<string>>(Paths.Comparer);
        var dotnet = new DotnetProcess(logger);

        foreach (var (project, arguments) in runs)
        {
            var id = Interlocked.Increment(ref _runs);
            var results = Path.Combine(context.LogDirectory, "trx", $"{id:000}-{label}");

            // A rerun in the same output directory reuses run numbers; an old report would add its
            // tests and failures to this run.
            if (Directory.Exists(results))
            {
                Directory.Delete(results, recursive: true);
            }
            arguments.Add("--no-build");
            if (context.Settings.Framework is { } framework)
            {
                arguments.AddRange(["-f", framework]);
            }

            arguments.AddRange(ReportArguments(project, results));

            using (var log = File.CreateText(Path.Combine(context.LogDirectory, $"{id:000}-{label}-{Path.GetFileNameWithoutExtension(project.Path)}.log")))
            {
                var exitCode = dotnet.Run(arguments, context.RepoRoot, new Dictionary<string, string>(), log);
                LogRun(label, project.Path, exitCode);
            }

            Cleanup();

            if (TrxReport.Read(results) is not { } outcome)
            {
                missing++;
                continue;
            }

            total += outcome.Total;
            failed[project.Path] = outcome.FailedClasses;
        }

        return new TestRunOutcome(stopwatch.Elapsed.TotalSeconds, total, failed, missing);
    }

    /// <summary>
    /// Arguments that make the run write a TRX report: VSTest's logger, or the testing platform's
    /// report option, which xUnit v3 names differently.
    /// </summary>
    /// <param name="project">Test project.</param>
    /// <param name="results">Results directory.</param>
    private List<string> ReportArguments(ProjectNode project, string results)
    {
        if (context.Mode == DotnetTestMode.VSTest)
        {
            return ["--logger", "trx", "--results-directory", results];
        }

        var bin = Path.Combine(project.Directory, "bin");
        var isXunitV3 = Directory.Exists(bin) && Directory.EnumerateFiles(bin, "xunit.v3.core.dll", SearchOption.AllDirectories).Any();
        return [isXunitV3 ? "--report-xunit-trx" : "--report-trx", "--results-directory", results];
    }

    /// <summary>Runs the cleanup command, if any, and waits for it.</summary>
    public void Cleanup()
    {
        if (context.Settings.Cleanup is not { Length: > 0 } command)
        {
            return;
        }

        var psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = context.RepoRoot, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the cleanup command.");
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        LogCleanup(process.ExitCode);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Label}: {Project} exited with {ExitCode}")]
    private partial void LogRun(string label, string project, int exitCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cleanup exited with {ExitCode}")]
    private partial void LogCleanup(int exitCode);
}
