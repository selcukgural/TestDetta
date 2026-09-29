using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sift.Core;

namespace Sift.Cli;

/// <summary>Counts shown in the summary line.</summary>
/// <param name="ChangedFileCount">Number of changed files git reported.</param>
/// <param name="ProjectCount">Number of projects in the repository.</param>
internal sealed record RunStats(int ChangedFileCount, int ProjectCount);

/// <summary>Everything <see cref="ResultWriter"/> needs to print one analysis.</summary>
/// <param name="Options">Command line options, for the base ref, the format and the tests-only filter.</param>
/// <param name="RepoRoot">Repository root; printed paths are relative to it.</param>
/// <param name="Stats">Counts for the summary line.</param>
/// <param name="Result">Project-level result.</param>
/// <param name="Plan">Test-level decision for every affected test project, and the time estimate.</param>
internal sealed record ResultReport(
    CliOptions Options,
    string RepoRoot,
    RunStats Stats,
    ImpactResult Result,
    TestPlan Plan)
{
    /// <summary>Test-level decision for every affected test project.</summary>
    public IReadOnlyList<TestProjectSelection> Tests => Plan.Projects;
}

/// <summary>Prints an analysis result on stdout in the format chosen on the command line.</summary>
internal static class ResultWriter
{
    /// <summary>Writes <paramref name="report"/> to <paramref name="stdout"/>.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="stdout">Destination, normally <see cref="Console.Out"/>.</param>
    public static void Write(ResultReport report, TextWriter stdout)
    {
        switch (report.Options.Affected.Format)
        {
            case OutputFormat.Paths:
                WritePaths(report, stdout);
                break;

            case OutputFormat.Commands:
                WriteCommands(report, stdout);
                break;

            case OutputFormat.Json:
                WriteJson(report, stdout);
                break;

            default:
                WriteText(report, stdout);
                break;
        }
    }

    /// <summary>
    /// One relative .csproj path per line. With <c>--tests-only</c>, test projects that can be
    /// skipped are left out.
    /// </summary>
    /// <param name="report">What to print.</param>
    /// <param name="stdout">Destination.</param>
    private static void WritePaths(ResultReport report, TextWriter stdout)
    {
        if (report.Options.Affected.TestsOnly)
        {
            foreach (var test in report.Tests)
            {
                if (test.Mode != TestRunMode.Skipped)
                {
                    stdout.WriteLine(Relative(report.RepoRoot, test.Project.Path));
                }
            }

            return;
        }

        foreach (var affected in report.Result.Affected)
        {
            stdout.WriteLine(Relative(report.RepoRoot, affected.Project.Path));
        }
    }

    /// <summary>One <c>dotnet test</c> command per test project to run, ready for <c>sh</c>.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="stdout">Destination.</param>
    private static void WriteCommands(ResultReport report, TextWriter stdout)
    {
        var mode = DotnetTestModeDetector.Detect(report.RepoRoot);
        foreach (var invocation in TestInvocationBuilder.Build(report.Tests, report.RepoRoot, [], mode))
        {
            stdout.WriteLine(CommandLine(invocation));
        }
    }

    /// <summary>Human readable summary: affected projects with reasons, then what to run per test project.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="stdout">Destination.</param>
    private static void WriteText(ResultReport report, TextWriter stdout)
    {
        var testsOnly = report.Options.Affected.TestsOnly;
        var selected = testsOnly ? [.. report.Result.Tests] : report.Result.Affected;

        stdout.WriteLine($"{report.Stats.ChangedFileCount} changed file(s) vs {report.Options.Analysis.BaseRef}, {report.Stats.ProjectCount} project(s) in repo.");
        stdout.WriteLine($"{selected.Count} affected{(testsOnly ? " test" : "")} project(s):");

        foreach (var affected in selected)
        {
            var marker = affected.Project.IsTestProject ? "[test] " : "       ";
            stdout.WriteLine($"  {marker}{Relative(report.RepoRoot, affected.Project.Path)}  ({affected.Reason})");
        }

        if (report.Result.CosmeticFiles.Count > 0)
        {
            stdout.WriteLine($"{report.Result.CosmeticFiles.Count} C# file(s) with only whitespace or comment changes (ignored).");
        }

        if (report.Result.UnmappedFiles.Count > 0)
        {
            stdout.WriteLine($"{report.Result.UnmappedFiles.Count} changed file(s) outside any project (ignored).");
        }

        if (report.Tests.Count == 0)
        {
            return;
        }

        stdout.WriteLine();
        WriteTestDecisions(report.RepoRoot, report.Tests, stdout);
        if (Durations.Summarize(report.Plan) is { } total)
        {
            stdout.WriteLine(total);
        }
    }

    /// <summary>
    /// The plan <c>sift test</c> prints before running: one line per affected test project with
    /// its decision and reason, followed by the selected classes.
    /// </summary>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="stdout">Destination.</param>
    public static void WritePlan(AnalysisResult analysis, TextWriter stdout)
    {
        stdout.WriteLine($"sift: {analysis.ChangedFiles.Count} changed file(s), {analysis.Tests.Count} affected test project(s).");
        if (analysis.Tests.Count == 0)
        {
            return;
        }

        WriteTestDecisions(analysis.RepoRoot, analysis.Tests, stdout);
        if (Durations.Summarize(analysis.Plan) is { } total)
        {
            stdout.WriteLine(total);
        }

        stdout.WriteLine();
    }

    /// <summary>
    /// Summary after <c>sift test</c>: outcome and duration per project, then the totals, so the
    /// result is readable below long test output.
    /// </summary>
    /// <param name="repoRoot">Repository root; printed paths are relative to it.</param>
    /// <param name="selections">All decisions, including skipped projects.</param>
    /// <param name="results">Results of the invocations that ran.</param>
    /// <param name="stdout">Destination.</param>
    /// <param name="plan">Test plan, for the time estimate; null leaves it out.</param>
    public static void WriteRunSummary(
        string repoRoot,
        IReadOnlyList<TestProjectSelection> selections,
        IReadOnlyList<TestRunResult> results,
        TextWriter stdout,
        TestPlan? plan = null)
    {
        stdout.WriteLine();
        stdout.WriteLine("sift summary:");

        var failed = 0;
        foreach (var result in results)
        {
            var passed = result.ExitCode == 0;
            failed += passed ? 0 : 1;
            var scope = result switch
            {
                { NotATestProject: true } => "not a test project",
                { RanUnfiltered: true } => "all tests, filter matched nothing",
                { Invocation.Selection.Mode: TestRunMode.Filtered } => $"{result.Invocation.Selection.TestClasses.Count} class(es)",
                _ => "all tests",
            };
            stdout.WriteLine(
                $"  {(passed ? "passed" : "FAILED")}  {Relative(repoRoot, result.Invocation.Selection.Project.Path)}  ({scope}, {result.Elapsed.TotalSeconds:0.0} s)");
        }

        var skipped = selections.Count(static s => s.Mode == TestRunMode.Skipped);
        stdout.WriteLine($"{results.Count} project(s) run, {failed} failed, {skipped} skipped.");
        if (plan is not null && Durations.Summarize(plan) is { } total)
        {
            stdout.WriteLine(total);
        }
    }

    /// <summary>The invocation as a POSIX shell command line.</summary>
    /// <param name="invocation">What would be run.</param>
    public static string CommandLine(TestInvocation invocation) =>
        "dotnet " + string.Join(' ', invocation.Arguments.Select(static a => IsShellSafe(a) ? a : ShellQuote(a)));

    /// <summary>One line per test project with its decision and reason, followed by the selected classes.</summary>
    /// <param name="repoRoot">Repository root; printed paths are relative to it.</param>
    /// <param name="tests">Decisions to print.</param>
    /// <param name="stdout">Destination.</param>
    private static void WriteTestDecisions(string repoRoot, IReadOnlyList<TestProjectSelection> tests, TextWriter stdout)
    {
        stdout.WriteLine("Tests to run:");
        foreach (var test in tests)
        {
            var label = test.Mode switch
            {
                TestRunMode.All => "all     ",
                TestRunMode.Filtered => "filtered",
                _ => "skip    ",
            };

            var estimate = test.Estimate is { } e ? $", {Durations.Describe(e)}" : string.Empty;
            stdout.WriteLine($"  {label} {Relative(repoRoot, test.Project.Path)}  ({test.Reason}{estimate})");
            foreach (var testClass in test.TestClasses)
            {
                stdout.WriteLine($"             {testClass}");
            }
        }
    }

    /// <summary>
    /// JSON written with <see cref="Utf8JsonWriter"/> directly: no reflection-based serializer and
    /// no intermediate objects for the shape.
    /// </summary>
    /// <param name="report">What to print.</param>
    /// <param name="stdout">Destination.</param>
    private static void WriteJson(ResultReport report, TextWriter stdout)
    {
        var selected = report.Options.Affected.TestsOnly ? [.. report.Result.Tests] : report.Result.Affected;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   // Paths contain characters such as '+' that the default encoder escapes needlessly.
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            json.WriteStartObject();
            json.WriteString("base"u8, report.Options.Analysis.BaseRef);
            json.WriteNumber("changedFiles"u8, report.Stats.ChangedFileCount);

            json.WriteStartArray("projects"u8);
            foreach (var affected in selected)
            {
                json.WriteStartObject();
                json.WriteString("path"u8, Relative(report.RepoRoot, affected.Project.Path));
                json.WriteBoolean("isTest"u8, affected.Project.IsTestProject);
                json.WriteString("reason"u8, affected.Reason.ToString());
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("tests"u8);
            foreach (var test in report.Tests)
            {
                json.WriteStartObject();
                json.WriteString("project"u8, Relative(report.RepoRoot, test.Project.Path));
                json.WriteString("mode"u8, test.Mode.ToString());
                json.WriteString("reason"u8, test.Reason);
                WriteStringArray(json, "classes"u8, test.TestClasses);
                if (test.Mode == TestRunMode.Filtered)
                {
                    json.WriteString("filter"u8, TestFilter.ForClasses(test.TestClasses));
                }

                WriteEstimate(json, test.Estimate);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartObject("estimate"u8);
            WriteEstimateFields(json, report.Plan.Estimate);
            json.WriteNumber("projectsWithoutTimings"u8, report.Plan.ProjectsWithoutTimings);
            json.WriteEndObject();

            WriteStringArray(json, "unmappedFiles"u8, report.Result.UnmappedFiles);
            WriteStringArray(json, "cosmeticFiles"u8, report.Result.CosmeticFiles);
            WriteCodeChanges(json, report.Result.CodeChanges);

            json.WriteEndObject();
        }

        stdout.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    /// <summary>Writes an <c>estimate</c> object, or nothing without timings.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="estimate">Estimate, or null.</param>
    private static void WriteEstimate(Utf8JsonWriter json, TimeEstimate? estimate)
    {
        if (estimate is null)
        {
            return;
        }

        json.WriteStartObject("estimate"u8);
        WriteEstimateFields(json, estimate);
        json.WriteEndObject();
    }

    /// <summary>Writes the recorded full, selected and saved seconds; nulls without timings.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="estimate">Estimate, or null.</param>
    private static void WriteEstimateFields(Utf8JsonWriter json, TimeEstimate? estimate)
    {
        if (estimate is null)
        {
            json.WriteNull("fullSeconds"u8);
            json.WriteNull("selectedSeconds"u8);
            json.WriteNull("savedSeconds"u8);
            return;
        }

        json.WriteNumber("fullSeconds"u8, Math.Round(estimate.FullSeconds, 1));
        json.WriteNumber("selectedSeconds"u8, Math.Round(estimate.SelectedSeconds, 1));
        json.WriteNumber("savedSeconds"u8, Math.Round(estimate.SavedSeconds, 1));
    }

    /// <summary>Writes the <c>codeChanges</c> array: changed symbols per C# file.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="changes">Non-cosmetic code changes.</param>
    private static void WriteCodeChanges(Utf8JsonWriter json, IReadOnlyList<CodeChange> changes)
    {
        json.WriteStartArray("codeChanges"u8);
        foreach (var change in changes)
        {
            json.WriteStartObject();
            json.WriteString("path"u8, change.Path);
            json.WriteBoolean("affectsWholeProject"u8, change.AffectsWholeProject);
            json.WriteStartArray("symbols"u8);
            foreach (var symbol in change.Symbols)
            {
                json.WriteStartObject();
                json.WriteString("type"u8, symbol.TypeName);
                json.WriteString("member"u8, symbol.MemberName);
                json.WriteString("kind"u8, symbol.Kind.ToString());
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    /// <summary>Writes a JSON array of strings under <paramref name="name"/>.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">UTF-8 property name.</param>
    /// <param name="values">Array items.</param>
    private static void WriteStringArray(Utf8JsonWriter json, ReadOnlySpan<byte> name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    /// <summary>True when <paramref name="value"/> needs no quoting in a POSIX shell.</summary>
    /// <param name="value">Argument to check.</param>
    private static bool IsShellSafe(string value) =>
        value.Length > 0 && value.AsSpan().IndexOfAnyExcept(ShellSafeCharacters) < 0;

    /// <summary>Characters that never need quoting in a POSIX shell.</summary>
    private static readonly System.Buffers.SearchValues<char> ShellSafeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./=:,+@%");

    /// <summary>Wraps <paramref name="value"/> in single quotes for POSIX shells, escaping embedded quotes.</summary>
    /// <param name="value">Argument to quote.</param>
    private static string ShellQuote(string value) => $"'{value.Replace("'", @"'\''", StringComparison.Ordinal)}'";

    /// <summary>Path of <paramref name="path"/> relative to <paramref name="root"/>, with '/' separators.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="path">Absolute path inside the repository.</param>
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
