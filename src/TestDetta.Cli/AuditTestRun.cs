using System.Diagnostics;
using TestDetta.Core;
using static TestDetta.Cli.TestRunArguments;

namespace TestDetta.Cli;

/// <summary>A test project the audit runs: one that wrote a readable report without crashing in the baseline.</summary>
/// <param name="Project">The test project in the team's tree.</param>
/// <param name="Relative">Its path relative to the repository root.</param>
/// <param name="Mode">The runner <c>dotnet test</c> uses for it.</param>
/// <param name="Syntax">Its filter syntax, for runs of some classes.</param>
internal sealed record AuditProject(ProjectNode Project, string Relative, DotnetTestMode Mode, TestFilterSyntax Syntax)
{
    /// <summary>Classes that failed in the baseline, without any change: a failure of theirs is not a change's.</summary>
    public IReadOnlySet<string> BaselineFailed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Wall time of the baseline run, build included; the time limits of the change's runs grow from it.</summary>
    public double BaselineSeconds { get; init; }

    /// <summary>Test classes in the baseline's report.</summary>
    public int BaselineClasses { get; init; }

    /// <summary>The project's build output folders, relative to the root of a working copy (one per target framework).</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];
}

/// <summary>What one <c>dotnet test</c> of a project showed.</summary>
/// <param name="Failed">Failed classes in its report.</param>
/// <param name="Complete">True when it wrote a report, did not crash and finished within its limit.</param>
/// <param name="Problem">Why it is not complete, for the report; null when it is.</param>
internal sealed record AuditRunOutcome(IReadOnlySet<string> Failed, bool Complete, string? Problem)
{
    /// <summary>Test classes in its report.</summary>
    public int Classes { get; init; }

    /// <summary>Its wall time.</summary>
    public double Seconds { get; init; }

    /// <summary>True when it was killed because the audit was cancelled.</summary>
    public bool Cancelled { get; init; }

    /// <summary>True when it was killed at its limit while that limit was what was left of the budget.</summary>
    public bool OutOfBudget { get; init; }
}

/// <summary>One <c>dotnet test</c> of a project in a working copy.</summary>
/// <param name="Copy">Root of the working copy.</param>
/// <param name="Project">The project in the team's tree.</param>
/// <param name="Arguments">The arguments after the project: the user's, with a class filter joined in for a run of some classes.</param>
/// <param name="Label">Name of the run's log and report folder.</param>
internal sealed record AuditRunRequest(string Copy, ProjectNode Project, IReadOnlyList<string> Arguments, string Label)
{
    /// <summary>True to run the copy's existing build (every run after the baseline).</summary>
    public bool NoBuild { get; init; }

    /// <summary>Longest the run may take; null for no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>True when <see cref="Timeout"/> is what was left of the budget, so a run killed at it ran out of budget.</summary>
    public bool CappedByBudget { get; init; }

    /// <summary>Extra variables, such as a worker's own temporary directory.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Runs a test project of a working copy the full way, as the team's own <c>dotnet test</c> with their arguments, with
/// a test report TestDetta reads per class, and nothing kept afterwards but the outcome: the report and the log are
/// deleted once read, so a long audit does not fill the disk.
/// </summary>
/// <param name="workspace">The work folder and its copies.</param>
/// <param name="cancel">Stops the run.</param>
internal sealed class AuditTestRun(AuditWorkspace workspace, CancellationToken cancel)
{
    /// <summary>Lines with which VSTest says the test host died, so the report is partial.</summary>
    private static readonly string[] CrashLines = ["Test host process crashed", "Test Run Aborted"];

    /// <summary>Runs the request and reads its report.</summary>
    /// <param name="request">What to run.</param>
    /// <param name="outputDirectory">The project's build output in the copy, for a testing-platform report option; null before it exists.</param>
    public AuditRunOutcome Run(AuditRunRequest request, string? outputDirectory)
    {
        var copyProject = workspace.InCopy(request.Copy, request.Project.Path);
        var location = DotnetTestLocation.For(request.Copy, copyProject);
        var runDirectory = location.FullDirectory(request.Copy);
        var results = workspace.Scratch(Path.Combine("reports", $"{request.Label}-{Guid.NewGuid():N}"));
        var reportName = $"testdetta-audit-{Guid.NewGuid():N}.trx";
        try
        {
            var (report, userResults) = ReportArguments(location.Mode, outputDirectory, results, reportName, request.Arguments, runDirectory);
            if (report is null)
            {
                return new AuditRunOutcome(new HashSet<string>(StringComparer.Ordinal), false, "no test report TestDetta can read can be asked for");
            }

            List<string> command = [.. location.TestArguments(request.Copy, copyProject), .. request.Arguments];
            if (request.NoBuild && !command.Contains("--no-build"))
            {
                command = Inserted(command, ["--no-build"]);
            }

            command = Inserted(command, report);
            var before = Snapshot(userResults);
            var log = Path.Combine(results, "run.log");
            var stopwatch = Stopwatch.StartNew();
            int exitCode;
            using (var writer = File.CreateText(log))
            {
                exitCode = AuditProcess.Run(command, runDirectory, request.Environment, writer, request.Timeout, cancel);
            }

            var seconds = stopwatch.Elapsed.TotalSeconds;
            var none = new HashSet<string>(StringComparer.Ordinal);
            if (exitCode == AuditProcess.Cancelled)
            {
                return new AuditRunOutcome(none, false, "cancelled") { Cancelled = true, Seconds = seconds };
            }

            if (exitCode == AuditProcess.TimedOut)
            {
                return request.CappedByBudget
                    ? new AuditRunOutcome(none, false, "stopped: the budget ran out") { OutOfBudget = true, Seconds = seconds }
                    : new AuditRunOutcome(none, false, $"timed out after {Durations.Format(seconds)}") { Seconds = seconds };
            }

            var crashed = File.ReadLines(log).Any(static l => CrashLines.Any(c => l.Contains(c, StringComparison.Ordinal)));
            var outcome = Collect(location.Mode, request.Copy, reportName, results, userResults, before);
            if (outcome is null)
            {
                return new AuditRunOutcome(none, false, $"wrote no test report (exit code {exitCode})") { Seconds = seconds };
            }

            if (crashed)
            {
                return new AuditRunOutcome(outcome.FailedClasses, false, "the test host crashed") { Classes = outcome.ReportedClasses.Count, Seconds = seconds };
            }

            return new AuditRunOutcome(outcome.FailedClasses, true, null) { Classes = outcome.ReportedClasses.Count, Seconds = seconds };
        }
        finally
        {
            TestDetta.Mutants.TreeCopy.Remove(results);
        }
    }

    /// <summary>
    /// The build output folders of a test project in a copy, from MSBuild: one per target framework it builds (only
    /// the user's <c>-f</c> when given), as <c>dotnet test</c> runs them all.
    /// </summary>
    /// <param name="copy">Root of the working copy.</param>
    /// <param name="project">The project in the team's tree.</param>
    /// <param name="userArguments">The user's arguments, for <c>-c</c> and <c>-f</c>.</param>
    /// <exception cref="InvalidOperationException">MSBuild could not answer.</exception>
    public List<string> OutputDirectories(string copy, ProjectNode project, IReadOnlyList<string> userArguments)
    {
        var copyProject = workspace.InCopy(copy, project.Path);
        var runDirectory = DotnetTestLocation.For(copy, copyProject).FullDirectory(copy);
        var options = BeforeRunSettings(userArguments);
        var configuration = ValueOf(options, "-c") ?? ValueOf(options, "--configuration") ?? "Debug";
        List<string> query = ["msbuild", copyProject, $"-p:Configuration={configuration}", "-getProperty:TargetPath", "-getProperty:TargetFrameworks"];
        if ((ValueOf(options, "-f") ?? ValueOf(options, "--framework")) is { } framework)
        {
            query.Add($"-p:TargetFramework={framework}");
        }

        using var properties = System.Text.Json.JsonDocument.Parse(AuditProcess.Capture(query, runDirectory, cancel));
        var values = properties.RootElement.GetProperty("Properties");
        if (values.GetProperty("TargetPath").GetString() is { Length: > 0 } single)
        {
            return [Paths.Normalize(Path.GetDirectoryName(single)!)];
        }

        var outputs = new List<string>();
        var frameworks = values.GetProperty("TargetFrameworks").GetString() ?? string.Empty;
        foreach (var each in frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            List<string> one = [.. query.Where(static q => !q.StartsWith("-getProperty", StringComparison.Ordinal)), $"-p:TargetFramework={each}", "-getProperty:TargetPath"];
            if (AuditProcess.Capture(one, runDirectory, cancel).Trim() is { Length: > 0 } path)
            {
                outputs.Add(Paths.Normalize(Path.GetDirectoryName(path)!));
            }
        }

        return outputs;
    }

    /// <summary>
    /// Builds a testing-platform test project in a copy before its baseline: its report option is read from its build
    /// output (an option it does not know would fail the run), which a fresh checkout does not have.
    /// </summary>
    /// <param name="copy">Root of the working copy.</param>
    /// <param name="project">The project in the team's tree.</param>
    /// <param name="userArguments">The user's arguments, for <c>-c</c> and <c>-f</c>.</param>
    /// <returns>True when it built.</returns>
    public bool Build(string copy, ProjectNode project, IReadOnlyList<string> userArguments)
    {
        var copyProject = workspace.InCopy(copy, project.Path);
        var runDirectory = DotnetTestLocation.For(copy, copyProject).FullDirectory(copy);
        var options = BeforeRunSettings(userArguments);
        List<string> command = ["build", copyProject, "-c", ValueOf(options, "-c") ?? ValueOf(options, "--configuration") ?? "Debug"];
        if ((ValueOf(options, "-f") ?? ValueOf(options, "--framework")) is { } framework)
        {
            command.AddRange(["-f", framework]);
        }

        return AuditProcess.Run(command, runDirectory, new Dictionary<string, string>(), TextWriter.Null, limit: null, cancel) == 0;
    }
}
