using System.Globalization;
using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>What <see cref="TextReport.WriteRunSummary"/> reports after <c>td test</c>.</summary>
/// <param name="RepoRoot">Repository root; printed paths are relative to it.</param>
/// <param name="Selections">Every decision, skipped projects included.</param>
/// <param name="Results">Results of the invocations that ran.</param>
/// <param name="Plan">Test plan, for the time estimate; null leaves it out.</param>
internal sealed record RunSummary(string RepoRoot, IReadOnlyList<TestProjectSelection> Selections, IReadOnlyList<TestRunResult> Results, TestPlan? Plan);

/// <summary>One test project in the <c>td record</c> summary.</summary>
/// <param name="Project">The project as the tables show it.</param>
/// <param name="Step">The incremental step (<c>full</c>, <c>partial</c>, <c>carry</c>, <c>current</c>); null without <c>--incremental</c>.</param>
internal sealed record RecordRow(string Project, string? Step)
{
    /// <summary>What happened to the map.</summary>
    public Cell Result { get; set; } = new("-", Role.Muted);

    /// <summary>Test classes in the map; null when there is none.</summary>
    public int? Classes { get; set; }

    /// <summary>Instrumented methods in the map; null when there is none or it was not recorded now.</summary>
    public int? Methods { get; set; }

    /// <summary>Lines under the table about this project: why, what failed, what to do.</summary>
    public List<Cell> Notes { get; } = [];
}

/// <summary>What <see cref="TextReport.WriteRecordSummary"/> reports after <c>td record</c>.</summary>
/// <param name="Rows">One per test project, in order.</param>
/// <param name="Incremental">True with <c>--incremental</c>, which adds the step column.</param>
/// <param name="MapsDirectory">Where the maps were written, relative to the repository.</param>
/// <param name="Saved">The line about copying the maps to <c>--maps-dir</c>; null without it.</param>
internal sealed record RecordSummary(IReadOnlyList<RecordRow> Rows, bool Incremental, string MapsDirectory, string? Saved);

/// <summary>
/// The human-readable console reports of <c>td affected</c> and <c>td test</c> (docs/design/console-output.md): a header
/// line of facts, boxed tables of projects and decisions, the selected classes, and the saving as a bar. Colour and
/// characters come from the <see cref="ConsoleStyle"/>, so the same text reads in a terminal, a CI log and a file.
/// </summary>
internal static class TextReport
{
    /// <summary>Cells of the saving bar.</summary>
    private const int BarWidth = 20;

    /// <summary>The report of <c>td affected</c>: header, affected projects, tests to run, selected classes, estimate.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    public static void WriteAnalysis(ResultReport report, TextWriter writer, ConsoleStyle style)
    {
        var testsOnly = report.Options.Affected.TestsOnly;
        var selected = testsOnly ? [.. report.Result.Tests] : report.Result.Affected;
        var dot = style.Paint(Role.Muted, $" {style.Glyphs.Dot} ");
        writer.WriteLine(
            Brand(style) + dot
            + style.Paint(Role.Number, Count(report.Stats.ChangedFileCount, "changed file")) + style.Paint(Role.Muted, $" vs {report.Options.Analysis.BaseRef}") + dot
            + style.Paint(Role.Number, $"{selected.Count} of {report.Stats.ProjectCount}") + $" {(testsOnly ? "test " : string.Empty)}projects affected");
        WriteNotes(report, writer, style);

        if (selected.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine(style.Paint(Role.Heading, "Affected projects"));
            var table = new TextTable(style, new TextColumn("Project"), new TextColumn("Kind"), new TextColumn("Why"));
            foreach (var affected in selected)
            {
                table.Add(
                    new Cell(Display(report.RepoRoot, affected.Project.Path), affected.Project.IsTestProject ? Role.Test : Role.Plain),
                    new Cell(affected.Project.IsTestProject ? "test" : "code", affected.Project.IsTestProject ? Role.Test : Role.Muted),
                    new Cell(Reason(affected.Reason), Role.Muted));
            }

            table.Write(writer);
        }

        if (report.Tests.Count == 0)
        {
            return;
        }

        writer.WriteLine();
        WriteDecisions(report.RepoRoot, report.Tests, writer, style);
        WriteEstimate(report.Plan, writer, style);
    }

    /// <summary>The plan <c>td test</c> prints before running: header, decisions, selected classes and estimate.</summary>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    public static void WritePlan(AnalysisResult analysis, TextWriter writer, ConsoleStyle style)
    {
        var dot = style.Paint(Role.Muted, $" {style.Glyphs.Dot} ");
        writer.WriteLine(Brand(style) + dot + style.Paint(Role.Number, Count(analysis.ChangedFiles.Count, "changed file")) + dot
                         + style.Paint(Role.Number, Count(analysis.Tests.Count, "affected test project")));
        if (analysis.Tests.Count == 0)
        {
            return;
        }

        WriteTeamConfiguration(analysis, writer, style);
        WriteDecisions(analysis.RepoRoot, analysis.Tests, writer, style);
        WriteEstimate(analysis.Plan, writer, style);
        writer.WriteLine();
    }

    /// <summary>One line per run test project whose own test configuration TestDetta keeps (<see cref="ResultWriter.TeamConfiguration"/>).</summary>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    private static void WriteTeamConfiguration(AnalysisResult analysis, TextWriter writer, ConsoleStyle style)
    {
        foreach (var (project, kept) in ResultWriter.TeamConfiguration(analysis.Tests, analysis.RepoRoot))
        {
            writer.WriteLine(style.Paint(Role.Muted, $"{project}: your {kept} kept in every run"));
        }
    }

    /// <summary>
    /// The summary after <c>td test</c>: outcome and duration per project, the totals, and the estimate, so the result
    /// is readable below long test output.
    /// </summary>
    /// <param name="summary">What ran and how it went.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    public static void WriteRunSummary(RunSummary summary, TextWriter writer, ConsoleStyle style)
    {
        writer.WriteLine();
        writer.WriteLine(style.Paint(Role.Heading, "td summary"));
        var table = new TextTable(style, new TextColumn("Test project"), new TextColumn("Result"), new TextColumn("Ran", Wrap: true), new TextColumn("Time", AlignRight: true));
        var failed = 0;
        foreach (var result in summary.Results)
        {
            var passed = result.ExitCode == 0;
            failed += passed ? 0 : 1;
            var scope = result switch
            {
                { NotATestProject: true } => "not a test project",
                { RanUnfiltered: true } => "all tests, filter matched nothing",
                { Invocation.Selection.Mode: TestRunMode.Filtered } => Count(result.Invocation.Selection.TestClasses.Count, "class", "classes"),
                _ => "all tests",
            };
            table.Add(
                new Cell(Display(summary.RepoRoot, result.Invocation.Selection.Project.Path)),
                passed ? new Cell($"{style.Glyphs.Pass} passed", Role.Good) : new Cell($"{style.Glyphs.Fail} FAILED", Role.Bad),
                new Cell(scope, Role.Muted),
                new Cell(Durations.Format(result.Elapsed.TotalSeconds), Role.Number));
        }

        foreach (var skipped in summary.Selections.Where(static s => s.Mode == TestRunMode.Skipped))
        {
            table.Add(new Cell(Display(summary.RepoRoot, skipped.Project.Path)), new Cell("skipped", Role.Muted), new Cell("nothing affected", Role.Muted));
        }

        if (summary.Results.Count > 0 || summary.Selections.Count > 0)
        {
            table.Write(writer);
        }

        var dot = style.Paint(Role.Muted, $" {style.Glyphs.Dot} ");
        var skippedCount = summary.Selections.Count(static s => s.Mode == TestRunMode.Skipped);
        writer.WriteLine(
            style.Paint(Role.Number, Count(summary.Results.Count, "project")) + " run" + dot
            + (failed > 0 ? style.Paint(Role.Bad, $"{failed} failed") : style.Paint(Role.Good, "0 failed")) + dot
            + style.Paint(Role.Muted, $"{skippedCount} skipped"));
        if (summary.Plan is { } plan)
        {
            WriteEstimate(plan, writer, style);
        }
    }

    /// <summary>
    /// The summary after <c>td record</c>: a row per test project with what happened to its map, then the notes per
    /// project (the incremental step's reason, failures, what selection does about them).
    /// </summary>
    /// <param name="summary">What was recorded.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    public static void WriteRecordSummary(RecordSummary summary, TextWriter writer, ConsoleStyle style)
    {
        writer.WriteLine();
        writer.WriteLine(style.Paint(Role.Heading, "td record summary") + style.Paint(Role.Muted, $" {style.Glyphs.Dot} maps in {summary.MapsDirectory}"));
        List<TextColumn> columns = [new("Test project")];
        if (summary.Incremental)
        {
            columns.Add(new TextColumn("Step"));
        }

        columns.AddRange([new TextColumn("Map", Wrap: true), new TextColumn("Classes", AlignRight: true), new TextColumn("Methods", AlignRight: true)]);
        var table = new TextTable(style, columns);
        foreach (var row in summary.Rows)
        {
            List<Cell> cells = [new(row.Project)];
            if (summary.Incremental)
            {
                cells.Add(new Cell(row.Step ?? "-", Role.Muted));
            }

            cells.AddRange([
                row.Result,
                new Cell(row.Classes?.ToString(CultureInfo.InvariantCulture) ?? "-", Role.Number),
                new Cell(row.Methods?.ToString(CultureInfo.InvariantCulture) ?? "-", Role.Number),
            ]);
            table.Add([.. cells]);
        }

        table.Write(writer);
        foreach (var row in summary.Rows.Where(static r => r.Notes.Count > 0))
        {
            writer.WriteLine($"  {row.Project}");
            foreach (var note in row.Notes)
            {
                writer.WriteLine("    " + style.Paint(note.Role, note.Text));
            }
        }

        if (summary.Saved is { } saved)
        {
            writer.WriteLine(style.Paint(Role.Muted, saved));
        }
    }

    /// <summary>The drift, ignored-file and cosmetic-change notes below the header.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    private static void WriteNotes(ResultReport report, TextWriter writer, ConsoleStyle style)
    {
        foreach (var drift in report.Drifts)
        {
            writer.WriteLine(style.Paint(Role.Muted, ResultWriter.DriftLine(drift)));
        }

        if (report.Result.CosmeticFiles.Count > 0)
        {
            writer.WriteLine(style.Paint(Role.Muted, $"{report.Result.CosmeticFiles.Count} C# file(s) with only whitespace or comment changes (ignored)."));
        }

        if (ResultWriter.NotBuildInputsLine(report.NotBuildInputs, static f => f) is { } notBuilt)
        {
            writer.WriteLine(style.Paint(Role.Muted, notBuilt));
        }

        if (report.Result.UnmappedFiles.Count > 0)
        {
            // With maps that recorded file reads, a file outside every project runs the tests that read it.
            var read = report.UnmappedFilesRead?.Count ?? 0;
            writer.WriteLine(style.Paint(Role.Muted, report.UnmappedFilesRead is null
                ? $"{report.Result.UnmappedFiles.Count} changed file(s) outside any project (ignored)."
                : $"{report.Result.UnmappedFiles.Count} changed file(s) outside any project: {read} read by recorded tests, {report.Result.UnmappedFiles.Count - read} read by none (ignored)."));
        }
    }

    /// <summary>
    /// The decision per test project as a table (run mode, classes, saving, reason), then the selected classes under
    /// their project, every one of them, so a filtered run can be reproduced from the output.
    /// </summary>
    /// <param name="repoRoot">Repository root; printed paths are relative to it.</param>
    /// <param name="tests">Decisions to print.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    private static void WriteDecisions(string repoRoot, IReadOnlyList<TestProjectSelection> tests, TextWriter writer, ConsoleStyle style)
    {
        writer.WriteLine(style.Paint(Role.Heading, "Tests to run"));
        var table = new TextTable(
            style,
            new TextColumn("Test project"),
            new TextColumn("Run"),
            new TextColumn("Classes", AlignRight: true),
            new TextColumn("Saved", AlignRight: true),
            new TextColumn("Why", Wrap: true));
        foreach (var test in tests)
        {
            var (run, role) = test.Mode switch
            {
                TestRunMode.All => ("all", Role.Notice),
                TestRunMode.Filtered => ("filtered", Role.Good),
                _ => ("skipped", Role.Muted),
            };
            table.Add(
                new Cell(Display(repoRoot, test.Project.Path)),
                new Cell(run, role),
                new Cell(Classes(test), Role.Number),
                new Cell(test.Estimate is { } estimate ? Percent(estimate.SavedFraction) : "-", test.Estimate is null ? Role.Muted : Role.Good),
                new Cell(test.Reason, Role.Muted));
        }

        table.Write(writer);
        foreach (var test in tests.Where(static t => t.Mode == TestRunMode.Filtered && t.TestClasses.Count > 0))
        {
            writer.WriteLine($"  {Display(repoRoot, test.Project.Path)}" + style.Paint(Role.Muted, $" {style.Glyphs.Arrow} {Count(test.TestClasses.Count, "class", "classes")}"));
            foreach (var testClass in test.TestClasses)
            {
                writer.WriteLine("    " + style.Paint(Role.Muted, testClass));
            }
        }
    }

    /// <summary>
    /// The plan's recorded time against the full suite, with a bar of the share saved; nothing without timings. Test
    /// projects without timings are named, since the numbers leave them out.
    /// </summary>
    /// <param name="plan">Test plan.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="style">Colour, characters and width.</param>
    private static void WriteEstimate(TestPlan plan, TextWriter writer, ConsoleStyle style)
    {
        if (plan.Estimate is not { } estimate)
        {
            return;
        }

        var full = (int)Math.Round(estimate.SavedFraction * BarWidth);
        var missing = plan.ProjectsWithoutTimings == 0 ? string.Empty : $"; {plan.ProjectsWithoutTimings} test project(s) without timings are not counted";
        writer.WriteLine(
            "Recorded test time " + style.Paint(Role.Number, Durations.Format(estimate.SelectedSeconds))
            + style.Paint(Role.Muted, $" instead of {Durations.Format(estimate.FullSeconds)}  ")
            + style.Paint(Role.Good, new string(style.Glyphs.Full, full)) + style.Paint(Role.Muted, new string(style.Glyphs.Empty, BarWidth - full))
            + style.Paint(Role.Good, $" {Percent(estimate.SavedFraction)} saved") + style.Paint(Role.Muted, missing));
    }

    /// <summary><c>TestDetta 0.1.3</c>, without the build's commit.</summary>
    /// <param name="style">Colour.</param>
    private static string Brand(ConsoleStyle style) => style.Paint(Role.Heading, "TestDetta " + CliApp.Version.Split('+')[0]);

    /// <summary>Selected classes of the project's candidates, <c>4 / 97</c>; just the count when the candidates are unknown.</summary>
    /// <param name="test">A project's decision.</param>
    private static string Classes(TestProjectSelection test)
    {
        var total = test.Decisions.Count;
        var chosen = test.Mode switch
        {
            TestRunMode.Filtered => test.TestClasses.Count,
            TestRunMode.All => total,
            _ => 0,
        };
        return total > 0 ? $"{chosen} / {total}" : test.Mode == TestRunMode.Filtered ? chosen.ToString(CultureInfo.InvariantCulture) : "-";
    }

    /// <summary>A share as a whole percentage, rounded down so 100% only appears when nothing runs.</summary>
    /// <param name="fraction">From 0 to 1.</param>
    private static string Percent(double fraction) => $"{Math.Floor(fraction * 100).ToString(CultureInfo.InvariantCulture)}%";

    /// <summary>Why a project is affected, in words.</summary>
    /// <param name="reason">The analyzer's reason.</param>
    private static string Reason(ImpactReason reason) => reason switch
    {
        ImpactReason.DirectChange => "changed",
        ImpactReason.Dependency => "depends on a change",
        _ => "build configuration changed",
    };

    /// <summary><c>1 changed file</c> or <c>3 changed files</c>.</summary>
    /// <param name="count">How many.</param>
    /// <param name="one">Singular noun phrase.</param>
    /// <param name="many">Plural; null adds an <c>s</c>.</param>
    private static string Count(int count, string one, string? many = null) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many ?? one + "s")}";

    /// <summary>
    /// A project as the tables show it: relative to the repository, and <c>tests/Shop.Tests</c> for
    /// <c>tests/Shop.Tests/Shop.Tests.csproj</c>, where the folder already names the project.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="path">Project file.</param>
    internal static string Display(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var folder = Path.GetDirectoryName(relative)?.Replace('\\', '/');
        return folder is { Length: > 0 } && string.Equals(Path.GetFileName(folder), Path.GetFileNameWithoutExtension(relative), StringComparison.Ordinal)
            ? folder
            : relative;
    }
}
