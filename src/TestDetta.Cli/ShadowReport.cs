using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>What shadow mode found out about a test class that failed in the full run.</summary>
internal enum ShadowVerdict
{
    /// <summary>The selection would have run it.</summary>
    Caught,

    /// <summary>Left out, and it fails alone with the change and passes alone without it.</summary>
    Miss,

    /// <summary>Left out, but it passes when run alone with the change: flaky or order-dependent.</summary>
    Flaky,

    /// <summary>Left out, but it fails on the base commit too: the change did not break it.</summary>
    PreExisting,

    /// <summary>Left out and not classified (switched off, or a rerun could not run): counts as a miss until checked.</summary>
    Unclassified,
}

/// <summary>A test class in the full run, with the selection's decision and what the run showed.</summary>
/// <param name="Name">Runtime class name.</param>
/// <param name="Selected">True when the selection would have run it.</param>
/// <param name="Decision">Kind of the decision (<see cref="ClassDecisionKind"/>), or <c>not-affected</c> for a project the change does not reach.</param>
/// <param name="Evidence">Why it was selected or left out.</param>
/// <param name="Seconds">Its tests' time in the full run's report; null without a report.</param>
/// <param name="Failed">True when a test of it failed in the full run.</param>
internal sealed record ShadowClass(string Name, bool Selected, string Decision, string Evidence, double? Seconds, bool Failed)
{
    /// <summary>For a failed class: caught, or what the classification found; null for a class that passed.</summary>
    public ShadowVerdict? Verdict { get; init; }

    /// <summary>What the classification saw (the reruns' outcomes), for the reader.</summary>
    public string? Note { get; init; }
}

/// <summary>One test project in the full run.</summary>
/// <param name="Path">Project path relative to the repository root, with '/' separators.</param>
/// <param name="Mode">What the selection would have done: <c>all</c>, <c>filtered</c>, <c>skipped</c>, or <c>not-affected</c>.</param>
/// <param name="Reason">The selection's reason for the project.</param>
/// <param name="ExitCode">Exit code of its full run.</param>
/// <param name="WallSeconds">Measured duration of its full run, build and start-up included.</param>
/// <param name="Reported">True when its test report was read; false leaves failures per class unknown.</param>
/// <param name="Classes">Classes in the report or in the decisions, ordered by name.</param>
internal sealed record ShadowProject(string Path, string Mode, string Reason, int ExitCode, double WallSeconds, bool Reported, IReadOnlyList<ShadowClass> Classes)
{
    /// <summary>TestDetta's own estimate from the recorded timings; null without a map with timings.</summary>
    public TimeEstimate? Estimate { get; init; }

    /// <summary>
    /// The part of <see cref="WallSeconds"/> the selection would have spent: all of it for a project that runs
    /// completely, none for one left out, and for a filtered one the time outside its test classes (build,
    /// start-up, discovery) plus the measured time of the selected classes.
    /// </summary>
    public double SelectedSeconds => Mode switch
    {
        "all" => WallSeconds,
        "filtered" when Reported => Math.Max(0, WallSeconds - Classes.Sum(static c => c.Seconds ?? 0)) + Classes.Where(static c => c.Selected).Sum(static c => c.Seconds ?? 0),
        "filtered" => WallSeconds,
        _ => 0,
    };
}

/// <summary>What is needed to check a shadow result by hand.</summary>
/// <param name="Version">TestDetta's version.</param>
/// <param name="BinarySha256">SHA-256 of the TestDetta assembly that ran, or null when it cannot be read.</param>
/// <param name="BaseRef">The ref compared with.</param>
/// <param name="MergeBase">The merge base the change was measured from, or null when git could not tell.</param>
/// <param name="Head">The commit tested, or null.</param>
/// <param name="Maps">Coverage maps used: test project, the commit they were recorded at, and the file's SHA-256.</param>
/// <param name="Options">The options that shape the selection.</param>
/// <param name="Command">The command that repeats the selection locally.</param>
internal sealed record ShadowReproduction(
    string Version,
    string? BinarySha256,
    string BaseRef,
    string? MergeBase,
    string? Head,
    IReadOnlyList<(string TestProject, string Commit, string Sha256)> Maps,
    IReadOnlyList<string> Options,
    string Command);

/// <summary>The outcome of one shadow run (docs/design/shadow-mode.md).</summary>
/// <param name="Compared">False when TestDetta could not select (the full run still ran); <see cref="NoResultReason"/> says why.</param>
/// <param name="NoResultReason">Why there is no shadow result, or null.</param>
/// <param name="Projects">Every test project of the full run.</param>
/// <param name="Reproduction">How to check the result.</param>
/// <param name="AnalysisSeconds">How long TestDetta's analysis took.</param>
internal sealed record ShadowResult(bool Compared, string? NoResultReason, IReadOnlyList<ShadowProject> Projects, ShadowReproduction Reproduction, double AnalysisSeconds)
{
    /// <summary>True when classification reruns were switched off.</summary>
    public bool ClassificationOff { get; init; }

    /// <summary>Failed classes with the given verdict, with their project.</summary>
    /// <param name="verdict">Verdict to list.</param>
    public IEnumerable<(ShadowProject Project, ShadowClass Class)> With(ShadowVerdict verdict) =>
        Projects.SelectMany(p => p.Classes.Where(c => c.Verdict == verdict).Select(c => (p, c)));

    /// <summary>Projects whose full run failed without a test report naming a failed class: the selection cannot be checked there.</summary>
    public IEnumerable<ShadowProject> Unexplained =>
        Projects.Where(static p => p.ExitCode != 0 && (!p.Reported || !p.Classes.Any(static c => c.Failed)));

    /// <summary>The first line of every report.</summary>
    public string Verdict()
    {
        if (!Compared)
        {
            return $"No shadow result: {NoResultReason}. The full suite ran as usual.";
        }

        var classes = Projects.Sum(static p => p.Classes.Count);
        var selected = Projects.Sum(static p => p.Classes.Count(static c => c.Selected));
        var full = Projects.Sum(static p => p.WallSeconds);
        var part = Projects.Sum(static p => p.SelectedSeconds);
        var share = classes == 0 ? "0%" : (selected / (double)classes).ToString("0.0%", CultureInfo.InvariantCulture);
        var scope = $"Would have run {selected} of {classes} test classes ({share}), {Durations.Format(part)} of {Durations.Format(full)} of test time";
        var misses = With(ShadowVerdict.Miss).Count();
        var unclassified = With(ShadowVerdict.Unclassified).Count();
        var unexplained = Unexplained.Count();
        var failed = Projects.Sum(static p => p.Classes.Count(static c => c.Failed));
        var outcome = (misses, unclassified, unexplained, failed) switch
        {
            ( > 0, _, _, _) => $"{misses} MISS(ES): failing test classes the selection left out (listed below)",
            (_, > 0, _, _) => $"{unclassified} failing class(es) the selection left out could not be classified; treat them as misses until checked",
            (_, _, > 0, _) => $"{unexplained} test project(s) failed without a report naming a failed class, so the selection cannot be checked there",
            (_, _, _, 0) => "no test failed, so this run shows no miss and no catch",
            _ => "every failing test class was selected or failed for a reason the change did not cause",
        };
        return $"{scope}; {outcome}.";
    }
}

/// <summary>Writes a <see cref="ShadowResult"/> as JSON (<c>testdetta-shadow.json</c>), as a Markdown job summary and to the console.</summary>
internal static class ShadowReport
{
    /// <summary>Version of the JSON shape; raised when a field changes meaning.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Writes the JSON report.</summary>
    /// <param name="result">What to write.</param>
    /// <param name="path">File to write; its directory is created.</param>
    public static void WriteJson(ShadowResult result, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            // Paths and class names contain characters such as '+' that the default encoder escapes needlessly.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        json.WriteStartObject();
        json.WriteNumber("schema"u8, SchemaVersion);
        json.WriteString("verdict"u8, result.Verdict());
        json.WriteBoolean("compared"u8, result.Compared);
        if (result.NoResultReason is { } reason)
        {
            json.WriteString("noResultReason"u8, reason);
        }

        json.WriteBoolean("classified"u8, !result.ClassificationOff);
        json.WriteNumber("analysisSeconds"u8, Math.Round(result.AnalysisSeconds, 3));

        json.WriteStartObject("totals"u8);
        json.WriteNumber("projects"u8, result.Projects.Count);
        json.WriteNumber("failedProjects"u8, result.Projects.Count(static p => p.ExitCode != 0));
        json.WriteNumber("classes"u8, result.Projects.Sum(static p => p.Classes.Count));
        json.WriteNumber("selectedClasses"u8, result.Projects.Sum(static p => p.Classes.Count(static c => c.Selected)));
        json.WriteNumber("fullSeconds"u8, Math.Round(result.Projects.Sum(static p => p.WallSeconds), 3));
        json.WriteNumber("selectedSeconds"u8, Math.Round(result.Projects.Sum(static p => p.SelectedSeconds), 3));
        var estimates = result.Projects.Where(static p => p.Estimate is not null).ToList();
        if (estimates.Count > 0)
        {
            json.WriteNumber("estimatedFullSeconds"u8, Math.Round(estimates.Sum(static p => p.Estimate!.FullSeconds), 3));
            json.WriteNumber("estimatedSelectedSeconds"u8, Math.Round(estimates.Sum(static p => p.Estimate!.SelectedSeconds), 3));
        }

        foreach (var (name, verdict) in (ReadOnlySpan<(string, ShadowVerdict)>)[
            ("caught", ShadowVerdict.Caught), ("misses", ShadowVerdict.Miss), ("flaky", ShadowVerdict.Flaky),
            ("preExisting", ShadowVerdict.PreExisting), ("unclassified", ShadowVerdict.Unclassified)])
        {
            json.WriteNumber(name, result.With(verdict).Count());
        }

        json.WriteNumber("unexplainedProjects"u8, result.Unexplained.Count());
        json.WriteEndObject();

        json.WriteStartArray("projects"u8);
        foreach (var project in result.Projects)
        {
            json.WriteStartObject();
            json.WriteString("path"u8, project.Path);
            json.WriteString("mode"u8, project.Mode);
            json.WriteString("reason"u8, project.Reason);
            json.WriteNumber("exitCode"u8, project.ExitCode);
            json.WriteNumber("wallSeconds"u8, Math.Round(project.WallSeconds, 3));
            json.WriteNumber("selectedSeconds"u8, Math.Round(project.SelectedSeconds, 3));
            if (project.Estimate is { } estimate)
            {
                json.WriteNumber("estimatedFullSeconds"u8, Math.Round(estimate.FullSeconds, 3));
                json.WriteNumber("estimatedSelectedSeconds"u8, Math.Round(estimate.SelectedSeconds, 3));
            }

            json.WriteBoolean("reported"u8, project.Reported);
            json.WriteStartArray("classes"u8);
            foreach (var testClass in project.Classes)
            {
                json.WriteStartObject();
                json.WriteString("name"u8, testClass.Name);
                json.WriteBoolean("selected"u8, testClass.Selected);
                json.WriteString("decision"u8, testClass.Decision);
                json.WriteString("evidence"u8, testClass.Evidence);
                if (testClass.Seconds is { } seconds)
                {
                    json.WriteNumber("seconds"u8, Math.Round(seconds, 3));
                }

                json.WriteBoolean("failed"u8, testClass.Failed);
                if (testClass.Verdict is { } verdict)
                {
                    json.WriteString("verdict"u8, Name(verdict));
                }

                if (testClass.Note is { } note)
                {
                    json.WriteString("note"u8, note);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();

        var reproduction = result.Reproduction;
        json.WriteStartObject("reproduction"u8);
        json.WriteString("testDettaVersion"u8, reproduction.Version);
        WriteOptional(json, "testDettaSha256"u8, reproduction.BinarySha256);
        json.WriteString("baseRef"u8, reproduction.BaseRef);
        WriteOptional(json, "mergeBase"u8, reproduction.MergeBase);
        WriteOptional(json, "head"u8, reproduction.Head);
        json.WriteStartArray("maps"u8);
        foreach (var (testProject, commit, sha) in reproduction.Maps)
        {
            json.WriteStartObject();
            json.WriteString("testProject"u8, testProject);
            json.WriteString("commit"u8, commit);
            json.WriteString("sha256"u8, sha);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("options"u8);
        foreach (var option in reproduction.Options)
        {
            json.WriteStringValue(option);
        }

        json.WriteEndArray();
        json.WriteString("command"u8, reproduction.Command);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>
    /// Writes the Markdown job summary. Every value from the repository (paths, class names, reasons) goes into a
    /// code span (<see cref="MarkdownSummary.Code"/>), so a pull request cannot inject links, images or mentions.
    /// </summary>
    /// <param name="result">What to write.</param>
    /// <param name="writer">Destination, usually the file named by <c>GITHUB_STEP_SUMMARY</c>.</param>
    public static void WriteMarkdown(ShadowResult result, TextWriter writer)
    {
        var code = MarkdownSummary.Code;
        writer.WriteLine("### TestDetta shadow run");
        writer.WriteLine();
        writer.WriteLine($"**{code(result.Verdict())}**");
        writer.WriteLine();
        writer.WriteLine("Every test ran, as without TestDetta; the job's result is that run's. This compares it with what TestDetta would have selected.");
        writer.WriteLine();
        if (!result.Compared)
        {
            WriteReproduction(result.Reproduction, writer, code);
            return;
        }

        foreach (var (title, verdict, meaning) in (ReadOnlySpan<(string, ShadowVerdict, string)>)[
            ("Misses", ShadowVerdict.Miss, "failed with the change, alone and in the full run, and passed alone without it; the selection left them out. Please send us this report."),
            ("Not classified", ShadowVerdict.Unclassified, "failed and were left out, and could not be rerun to tell why; treat them as misses until checked."),
            ("Caught", ShadowVerdict.Caught, "failed and the selection would have run them."),
            ("Failing before the change", ShadowVerdict.PreExisting, "were left out and fail on the base commit too."),
            ("Flaky or order-dependent", ShadowVerdict.Flaky, "were left out and failed in the full run, but passed when run alone with the change."),
        ])
        {
            var list = result.With(verdict).ToList();
            if (list.Count == 0)
            {
                continue;
            }

            writer.WriteLine($"**{title}** ({list.Count}): test classes that {meaning}");
            writer.WriteLine();
            foreach (var (project, testClass) in list)
            {
                var note = testClass.Note is { } n ? $"; {code(n)}" : string.Empty;
                writer.WriteLine($"- {code(testClass.Name)} in {code(project.Path)}: {(testClass.Selected ? "selected" : "left out")}, {code(testClass.Evidence)}{note}");
            }

            writer.WriteLine();
        }

        foreach (var project in result.Unexplained)
        {
            writer.WriteLine($"- {code(project.Path)} failed (exit code {project.ExitCode}) {(project.Reported ? "with no failed test in its report" : "without a test report")}: the selection cannot be checked there.");
        }

        writer.WriteLine();
        writer.WriteLine("| Test project | Selection | Classes run | Full run | Selection's part | TestDetta's estimate |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var project in result.Projects)
        {
            var estimate = project.Estimate is { } e ? Durations.Describe(e) : "no timings";
            writer.WriteLine($"| {code(project.Path)} | {project.Mode} | {project.Classes.Count(static c => c.Selected)} of {project.Classes.Count} | {Durations.Format(project.WallSeconds)} | {Durations.Format(project.SelectedSeconds)} | {estimate} |");
        }

        writer.WriteLine();
        writer.WriteLine("The selection's part of a filtered project is its time outside test classes (build, start-up, discovery) plus the measured time of the selected classes.");
        writer.WriteLine();
        WriteReproduction(result.Reproduction, writer, code);
    }

    /// <summary>Writes the verdict and the lists to the console after the full run's output.</summary>
    /// <param name="result">What to write.</param>
    /// <param name="stdout">Destination.</param>
    public static void WriteConsole(ShadowResult result, TextWriter stdout)
    {
        stdout.WriteLine("td shadow:");
        stdout.WriteLine($"  {result.Verdict()}");
        foreach (var (label, verdict) in (ReadOnlySpan<(string, ShadowVerdict)>)[
            ("MISS", ShadowVerdict.Miss), ("not classified", ShadowVerdict.Unclassified), ("caught", ShadowVerdict.Caught),
            ("failing before the change", ShadowVerdict.PreExisting), ("flaky", ShadowVerdict.Flaky)])
        {
            foreach (var (project, testClass) in result.With(verdict))
            {
                stdout.WriteLine($"  {label}: {testClass.Name} ({project.Path}){(testClass.Note is { } note ? $": {note}" : string.Empty)}");
            }
        }

        foreach (var project in result.Unexplained)
        {
            stdout.WriteLine($"  failed without a failed class in a report: {project.Path} (exit code {project.ExitCode})");
        }

        stdout.WriteLine($"  reproduce the selection: {result.Reproduction.Command}");
    }

    /// <summary>The verdict's name in the JSON report.</summary>
    /// <param name="verdict">Verdict.</param>
    public static string Name(ShadowVerdict verdict) => verdict switch
    {
        ShadowVerdict.Caught => "caught",
        ShadowVerdict.Miss => "miss",
        ShadowVerdict.Flaky => "flaky",
        ShadowVerdict.PreExisting => "pre-existing",
        _ => "unclassified",
    };

    /// <summary>The reproduction block of the Markdown summary.</summary>
    /// <param name="reproduction">What to show.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="code">Code span formatter.</param>
    private static void WriteReproduction(ShadowReproduction reproduction, TextWriter writer, Func<string, string> code)
    {
        writer.WriteLine("<details><summary>Reproduce this selection</summary>");
        writer.WriteLine();
        writer.WriteLine($"- TestDetta {code(reproduction.Version)}{(reproduction.BinarySha256 is { } sha ? $", SHA-256 {code(sha)}" : string.Empty)}");
        writer.WriteLine($"- compared with {code(reproduction.BaseRef)}, merge base {code(reproduction.MergeBase ?? "unknown")}, head {code(reproduction.Head ?? "unknown")}");
        foreach (var (testProject, commit, mapSha) in reproduction.Maps)
        {
            writer.WriteLine($"- map of {code(testProject)} recorded at {code(commit)}, SHA-256 {code(mapSha)}");
        }

        writer.WriteLine($"- with these maps in {code(".testdetta/coverage")}, at the head commit: {code(reproduction.Command)}");
        writer.WriteLine();
        writer.WriteLine("</details>");
        writer.WriteLine();
    }

    /// <summary>Writes a string property, or nothing for null.</summary>
    /// <param name="json">Writer.</param>
    /// <param name="name">Property name.</param>
    /// <param name="value">Value or null.</param>
    private static void WriteOptional(Utf8JsonWriter json, ReadOnlySpan<byte> name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }
}
