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
/// <param name="Complete">
/// True when it finished within its limit and wrote a report that says the run completed: no crashed or aborted test
/// host (in the report or the log), no error of the run itself, and no failing exit code without a failed test.
/// </param>
/// <param name="Problem">Why it is not complete, for the report; null when it is.</param>
internal sealed record AuditRunOutcome(IReadOnlySet<string> Failed, bool Complete, string? Problem)
{
    /// <summary>Test classes in its report.</summary>
    public int Classes { get; init; }

    /// <summary>
    /// Classes with a test that ran (passed or failed): a class missing here did not run at all, so its absence from
    /// <see cref="Failed"/> says nothing (a class filter that matched none of its tests, say).
    /// </summary>
    public IReadOnlySet<string> Executed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

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
    /// <summary>
    /// Lines with which VSTest says the test host died, so the report is partial. The runs are made in English
    /// (<see cref="AuditProcess.English"/>); the report's own outcome and the exit code are read as well, so a message
    /// in another language does not hide a crash either.
    /// </summary>
    private static readonly string[] CrashLines = ["Test host process crashed", "Test Run Aborted", "The active test run was aborted"];

    /// <summary>Overall outcomes of a TRX report that mean the run did not complete.</summary>
    private static readonly string[] IncompleteOutcomes = ["Aborted", "Error", "Timeout"];

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

            var problem = Problem(exitCode, crashed, ReportProblem(results), outcome.FailedClasses.Count);
            return new AuditRunOutcome(outcome.FailedClasses, problem is null, problem)
            {
                Classes = outcome.ReportedClasses.Count,
                Executed = outcome.ExecutedClasses,
                Seconds = seconds,
            };
        }
        finally
        {
            TestDetta.Mutants.TreeCopy.Remove(results);
        }
    }

    /// <summary>
    /// Why a run that wrote a report did not complete, or null when it did: the log says the test host crashed or the
    /// run was aborted; the report says so itself (<paramref name="reportProblem"/>); or the run failed (a non-zero exit
    /// code) without a failed test, as a cleanup failure at the run's level, a testing-platform run in which no test ran
    /// (exit code 8), or a crash whose message was not recognized does. A class that never ran then must not count as
    /// passed.
    /// </summary>
    /// <param name="exitCode">The run's exit code.</param>
    /// <param name="crashLine">True when the log holds a line that says the test host crashed.</param>
    /// <param name="reportProblem">What the report says went wrong (<see cref="ReportProblem"/>), or null.</param>
    /// <param name="failedClasses">Failed classes in the report.</param>
    internal static string? Problem(int exitCode, bool crashLine, string? reportProblem, int failedClasses) =>
        crashLine ? "the test host crashed"
        : reportProblem is not null ? reportProblem
        : exitCode != 0 && failedClasses == 0 ? $"it failed (exit code {exitCode}) without a failed test"
        : null;

    /// <summary>
    /// What the reports in <paramref name="folder"/> say went wrong with the run as a whole, or null: a TRX report whose
    /// overall outcome is <c>Aborted</c>, <c>Error</c> or <c>Timeout</c>, or that holds a run message saying the run was
    /// aborted or the test host crashed (VSTest writes it there while the outcome stays <c>Failed</c>; in English, since
    /// the runs are made in English; other run messages of outcome <c>Error</c> are adapters' lines such as xUnit's
    /// "[FAIL]" and mean nothing); an NUnit result whose run was cancelled. A crash whose message is not recognized is
    /// still seen: by a failing exit code without a failed test (<see cref="Problem"/>), or, for a whole project's run,
    /// by test classes missing from its report (<see cref="AuditTrial"/>).
    /// </summary>
    /// <param name="folder">The run's report folder.</param>
    internal static string? ReportProblem(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!file.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            System.Xml.Linq.XDocument document;
            try
            {
                document = System.Xml.Linq.XDocument.Load(file);
            }
            catch (System.Xml.XmlException)
            {
                return $"its test report {Path.GetFileName(file)} could not be read";
            }

            if (document.Root is { Name.LocalName: "test-run" } nunit)
            {
                if (string.Equals(nunit.Attribute("label")?.Value, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return "the test run was cancelled";
                }

                continue;
            }

            if (document.Root is not { Name.LocalName: "TestRun" } run)
            {
                continue;
            }

            var ns = run.Name.Namespace;
            var summary = run.Element(ns + "ResultSummary");
            if (summary?.Attribute("outcome")?.Value is { } overall && IncompleteOutcomes.Contains(overall, StringComparer.OrdinalIgnoreCase))
            {
                return $"the test run ended as {overall}";
            }

            var aborted = summary?.Element(ns + "RunInfos")?.Elements(ns + "RunInfo")
                .Select(i => i.Element(ns + "Text")?.Value.Trim() ?? string.Empty)
                .FirstOrDefault(static text => CrashLines.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase)));
            if (aborted is not null)
            {
                var first = aborted.Split('\n', 2)[0].Trim();
                return $"the test run was aborted: {(first.Length > 200 ? first[..200] + "…" : first)}";
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a testing-platform test project in a copy before its baseline: its report option is read from its build
    /// output (an option it does not know would fail the run), which a fresh checkout does not have.
    /// </summary>
    /// <param name="copy">Root of the working copy.</param>
    /// <param name="project">The project in the team's tree.</param>
    /// <param name="userArguments">The user's arguments, for <c>-c</c> and <c>-f</c>.</param>
    /// <param name="limit">Longest the build may take (what is left of the budget); null for no limit.</param>
    /// <returns>True when it built.</returns>
    public bool Build(string copy, ProjectNode project, IReadOnlyList<string> userArguments, TimeSpan? limit)
    {
        var copyProject = workspace.InCopy(copy, project.Path);
        var runDirectory = DotnetTestLocation.For(copy, copyProject).FullDirectory(copy);
        var options = BeforeRunSettings(userArguments);
        List<string> command = ["build", copyProject, "-c", ValueOf(options, "-c") ?? ValueOf(options, "--configuration") ?? "Debug"];
        if ((ValueOf(options, "-f") ?? ValueOf(options, "--framework")) is { } framework)
        {
            command.AddRange(["-f", framework]);
        }

        return AuditProcess.Run(command, runDirectory, new Dictionary<string, string>(), TextWriter.Null, limit, cancel) == 0;
    }
}
