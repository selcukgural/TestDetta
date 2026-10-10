using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TestDetta.Core;

namespace TestDetta.Cli;

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

    /// <summary>Older coverage maps in use and what changed since they were recorded.</summary>
    public IReadOnlyList<MapDrift> Drifts { get; init; } = [];

    /// <summary>The license check of the run; null when it was not checked.</summary>
    public TestDetta.Core.Licensing.LicenseStatus? License { get; init; }

    /// <summary>True when the license decided the run (see <see cref="AnalysisResult.LicenseEnforced"/>).</summary>
    public bool LicenseEnforced { get; init; }

    /// <summary>See <see cref="AnalysisResult.UnmappedFilesRead"/>.</summary>
    public IReadOnlyList<string>? UnmappedFilesRead { get; init; }

    /// <summary>Colour and characters of the text format; the other formats are never styled.</summary>
    public ConsoleStyle Style { get; init; } = ConsoleStyle.Plain;
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
        foreach (var invocation in TestInvocationBuilder.Build(report.Tests, report.RepoRoot, []))
        {
            stdout.WriteLine(CommandLine(invocation));
        }
    }

    /// <summary>One line on an older coverage map in use, for text output and the Markdown summary.</summary>
    /// <param name="drift">The map's commit and the changes since.</param>
    internal static string DriftLine(MapDrift drift) =>
        $"Coverage maps from {drift.FromCommit[..Math.Min(12, drift.FromCommit.Length)]} are {drift.Commits} commit(s) behind the base; "
        + $"{drift.ChangedFiles.Count} file(s) changed since then were bridged.";

    /// <summary>Human readable report (docs/design/console-output.md); see <see cref="TextReport.WriteAnalysis"/>.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="stdout">Destination.</param>
    private static void WriteText(ResultReport report, TextWriter stdout) => TextReport.WriteAnalysis(report, stdout, report.Style);

    /// <summary>
    /// The invocation as a POSIX shell command line, run from the repository root. A project that
    /// runs elsewhere (<see cref="DotnetTestLocation"/>) gets a subshell that changes there first, so
    /// every line stands on its own.
    /// </summary>
    /// <param name="invocation">What would be run.</param>
    public static string CommandLine(TestInvocation invocation)
    {
        var command = "dotnet " + string.Join(' ', invocation.Arguments.Select(static a => IsShellSafe(a) ? a : ShellQuote(a)));
        var directory = invocation.Location.Directory;
        return directory.Length == 0 ? command : $"(cd {(IsShellSafe(directory) ? directory : ShellQuote(directory))} && {command})";
    }

    /// <summary>
    /// What TestDetta kept of each run test project's own test configuration (docs/design/team-test-configuration.md), one
    /// item per project that has any: its test case filter, run settings file and testing platform arguments, which every
    /// run TestDetta starts keeps. Empty when no run project sets any.
    /// </summary>
    /// <param name="tests">Decisions per test project.</param>
    /// <param name="repoRoot">Repository root, for the paths.</param>
    internal static List<(string Project, string Kept)> TeamConfiguration(IReadOnlyList<TestProjectSelection> tests, string repoRoot)
    {
        var lines = new List<(string, string)>();
        foreach (var test in tests)
        {
            if (test.Mode == TestRunMode.Skipped || test.Project.TestConfiguration is not { } configuration)
            {
                continue;
            }

            var parts = new List<string>();
            if (configuration.CaseFilter is { } filter)
            {
                parts.Add($"test case filter {filter}");
            }

            if (configuration.RunSettings is { } settings)
            {
                parts.Add($"run settings {Path.GetRelativePath(repoRoot, settings).Replace('\\', '/')}");
            }

            if (configuration.PlatformArguments is { } arguments)
            {
                parts.Add($"testing platform arguments {arguments}");
            }

            lines.Add((Path.GetRelativePath(repoRoot, test.Project.Path).Replace('\\', '/'), string.Join(", ", parts)));
        }

        return lines;
    }

    /// <summary>
    /// A line about the license when it matters to the reader: only when the license is enforced and
    /// it is not simply valid (a warning, or every test running). Null otherwise, so runs with a valid
    /// license, and runs before licensing was enforced, stay quiet.
    /// </summary>
    /// <param name="license">The license check, or null.</param>
    /// <param name="enforced">True when the license decides the run.</param>
    internal static string? LicenseLine(TestDetta.Core.Licensing.LicenseStatus? license, bool enforced) =>
        enforced && license is { State: not (TestDetta.Core.Licensing.LicenseState.Valid or TestDetta.Core.Licensing.LicenseState.PublicRepository) }
            ? $"License: {license.Message}"
            : null;

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
                json.WriteStartArray("decisions"u8);
                foreach (var classDecision in test.Decisions)
                {
                    json.WriteStartObject();
                    json.WriteString("class"u8, classDecision.TestClass);
                    json.WriteBoolean("selected"u8, classDecision.Selected);
                    json.WriteString("kind"u8, JsonNamingPolicy.CamelCase.ConvertName(classDecision.Kind.ToString()));
                    json.WriteString("evidence"u8, classDecision.Evidence);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
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

            json.WriteStartArray("olderCoverageMaps"u8);
            foreach (var drift in report.Drifts)
            {
                json.WriteStartObject();
                json.WriteString("commit"u8, drift.FromCommit);
                json.WriteNumber("commitsBehind"u8, drift.Commits);
                json.WriteNumber("changedFiles"u8, drift.ChangedFiles.Count);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            if (report.License is { } license)
            {
                json.WriteStartObject("license"u8);
                json.WriteString("state"u8, JsonNamingPolicy.CamelCase.ConvertName(license.State.ToString()));
                json.WriteString("message"u8, license.Message);
                json.WriteBoolean("allowsSelection"u8, license.AllowsSelection);
                json.WriteBoolean("enforced"u8, report.LicenseEnforced);
                if (license.Claims is { } claims)
                {
                    json.WriteString("id"u8, claims.Id);
                    json.WriteNumber("seats"u8, claims.Seats);
                    json.WriteString("validUntil"u8, claims.ValidUntil.UtcDateTime);
                }

                if (license.ActiveCommitters is { } active)
                {
                    json.WriteNumber("activeCommitters"u8, active);
                }

                json.WriteEndObject();
            }

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
