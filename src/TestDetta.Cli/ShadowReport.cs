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

    /// <summary>
    /// Left out, and a test of it fails with the change (alone, or in its project's full-run context when it is
    /// order-dependent) and passes, or does not exist, the same way without it.
    /// </summary>
    Miss,

    /// <summary>Left out, but it passes when run alone and in its project's full-run context with the change: it failed at random.</summary>
    Flaky,

    /// <summary>Left out, but every test of it that fails with the change fails the same way on the base commit: the change did not break it.</summary>
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

    /// <summary>Its tests that failed in the full run, by the names the report gives them; the classification compares these.</summary>
    public IReadOnlyList<string> FailedTests { get; init; } = [];
}

/// <summary>One test project of the repository and what the full run showed of it.</summary>
/// <param name="Path">Project path relative to the repository root, with '/' separators.</param>
/// <param name="Mode">
/// What the selection would have done: <c>all</c>, <c>filtered</c>, <c>skipped</c>, <c>not-affected</c>; <c>unknown</c>
/// without a selection.
/// </param>
/// <param name="Reason">The selection's reason for the project.</param>
/// <param name="ExitCode">
/// Exit code of its own run when it ran on its own; in one command over a solution, 1 when its report says the run
/// failed and 0 when it says it passed; null when that is not known (no report from that command).
/// </param>
/// <param name="WallSeconds">
/// Its time in the full run: run on its own, its measured <c>dotnet test</c> (build and start-up included); in one
/// command over a solution, from the start to the finish its reports give (the test host's time, without the build,
/// which that command shares between projects).
/// </param>
/// <param name="Reported">True when a test report of it was read; false leaves failures per class unknown.</param>
/// <param name="Classes">The classes in its reports, ordered by name: the ones compared with the selection.</param>
internal sealed record ShadowProject(string Path, string Mode, string Reason, int? ExitCode, double WallSeconds, bool Reported, IReadOnlyList<ShadowClass> Classes)
{
    /// <summary>TestDetta's own estimate from the recorded timings, for all of the project; null without a map with timings.</summary>
    public TimeEstimate? Estimate { get; init; }

    /// <summary>
    /// Classes the selection decided about that appear in none of this job's reports, with their decisions (no
    /// <see cref="ShadowClass.Seconds"/>, never <see cref="ShadowClass.Failed"/>): not compared, since no test of them
    /// ran here, but shown, so the report holds every decision the selection made.
    /// </summary>
    public IReadOnlyList<ShadowClass> NotRun { get; init; } = [];

    /// <summary>
    /// Why none of its classes can be compared with the selection (no test of it ran in this job, it wrote no report
    /// that could be read, it was not built before the run); null when some of them ran.
    /// </summary>
    public string? NotCompared { get; init; }

    /// <summary>True when some of its classes ran in this job, so the run can be compared with the selection.</summary>
    public bool Ran => Classes.Count > 0;

    /// <summary>
    /// True when the team's command, which runs this project, failed and the project wrote no test report: a build
    /// error or a crash the selection cannot be checked against, counted as unexplained.
    /// </summary>
    public bool MissingReport { get; init; }

    /// <summary>
    /// True for a testing-platform project that was not built before the run: its build output did not exist, so
    /// TestDetta could not tell which report option it takes (options are never guessed: one the project does not know
    /// fails its run), it ran without a report, and its results cannot be compared with the selection.
    /// <see cref="NotCompared"/> then says so (<see cref="NotBuiltReason"/>).
    /// </summary>
    public bool NotBuilt { get; init; }

    /// <summary>What the report says of a project that was not built before the run (<see cref="NotBuilt"/>).</summary>
    /// <param name="output">Its build output folder that did not exist, relative to the repository root.</param>
    public static string NotBuiltReason(string output) =>
        $"no test report: the project was not built before the run ({output} did not exist), so TestDetta could not tell which report option " +
        "it takes and its failures cannot be compared with the selection; build first and run dotnet test --no-build";

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

/// <summary>How the full run was made.</summary>
internal enum ShadowRunKind
{
    /// <summary>The team's own <c>dotnet test</c> command as given, with only a report option added.</summary>
    Verbatim,

    /// <summary>One <c>dotnet test</c> per test project, because the team's command could not be run as given (<see cref="ShadowRunInfo.FallbackReason"/>).</summary>
    PerProject,

    /// <summary>The team's command as given without any report, because the repository's projects could not be read.</summary>
    Plain,
}

/// <summary>The full run as a whole: how it was made, how long it took and how it ended.</summary>
/// <param name="Kind">How it was made.</param>
/// <param name="Command">The command line, or a description of the per-project commands.</param>
/// <param name="FallbackReason">Why it ran per project; null otherwise.</param>
/// <param name="WallSeconds">Measured time of the whole full run, from its start to its end, build included.</param>
/// <param name="ExitCode">Its exit code: the command's own, or for per-project runs 1 when any failed.</param>
internal sealed record ShadowRunInfo(ShadowRunKind Kind, string Command, string? FallbackReason, double WallSeconds, int ExitCode)
{
    /// <summary>Test assemblies whose reports could not be matched to one test project; their tests are not compared.</summary>
    public IReadOnlyList<string> UnmappedReports { get; init; } = [];

    /// <summary>
    /// The folder every test project of which ran or was compared, when the test projects the command runs could not
    /// be told (several candidates, an unreadable solution, test assemblies named); null when they were known.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>The kind's name in the JSON report.</summary>
    public string KindName => Kind switch
    {
        ShadowRunKind.Verbatim => "verbatim",
        ShadowRunKind.PerProject => "per-project",
        _ => "plain",
    };

    /// <summary>One sentence for the console: how the full run was made and how long it took.</summary>
    public string Describe() => Describe(static text => text);

    /// <summary>One sentence for the reports: how the full run was made and how long it took.</summary>
    /// <param name="code">Formats the command and the reason, which hold the repository's own names (a code span in Markdown).</param>
    public string Describe(Func<string, string> code)
    {
        var how = Kind switch
        {
            ShadowRunKind.Verbatim => $"Ran your own command as given, adding only a test report per project: {code(Command)} ({Durations.Format(WallSeconds)} in all).",
            ShadowRunKind.PerProject => $"Ran each test project your command runs on its own, one after another, because {code(FallbackReason ?? "it had to")} ({Durations.Format(WallSeconds)} in all).",
            _ => $"Ran your own command as given: {code(Command)} ({Durations.Format(WallSeconds)} in all).",
        };
        return Scope is { } under
            ? $"{how} Which test projects your command runs could not be told, so every test project under {code(under)} counts."
            : how;
    }
}

/// <summary>What is needed to check a shadow result by hand.</summary>
/// <param name="Version">TestDetta's version.</param>
/// <param name="BinarySha256">SHA-256 of the TestDetta assembly that ran, or null when it cannot be read.</param>
/// <param name="BaseRef">The ref compared with.</param>
/// <param name="MergeBase">The merge base the change was measured from, or null when git could not tell.</param>
/// <param name="Head">The commit tested, or null.</param>
/// <param name="Maps">Coverage maps used: test project, the commit they were recorded at, and the file's SHA-256.</param>
/// <param name="Options">The options that shape the selection and the classification (its time limits).</param>
/// <param name="Command">
/// The command that repeats the run and its decisions: the same <c>td test --shadow</c> at the head
/// commit, compared with the merge base, with the same maps in place (their hashes above).
/// </param>
/// <param name="ExplainCommand">The command that lists every decision without running tests.</param>
internal sealed record ShadowReproduction(
    string Version,
    string? BinarySha256,
    string BaseRef,
    string? MergeBase,
    string? Head,
    IReadOnlyList<(string TestProject, string Commit, string Sha256)> Maps,
    IReadOnlyList<string> Options,
    string Command,
    string ExplainCommand);

/// <summary>How the classification went: the limits it had and the part of its budget it spent.</summary>
/// <param name="Limits">The limits in effect.</param>
/// <param name="Seconds">Time the classification took, every rerun and the base checkout together.</param>
/// <param name="BudgetRanOut">True when the budget left classes unclassified or stopped a rerun.</param>
internal sealed record ShadowClassification(ShadowLimits Limits, double Seconds, bool BudgetRanOut)
{
    /// <summary>One sentence for the reports: time spent of the budget, the limits per rerun, and whether the budget ran out.</summary>
    public string Describe()
    {
        var budget = Limits.Budget is { } limit ? $"{Durations.Format(Seconds)} of its {Durations.DescribeLimit(limit)} budget" : $"{Durations.Format(Seconds)}, with no budget";
        var ranOut = BudgetRanOut ? "; the budget ran out, so the classes left then are not classified" : string.Empty;
        return $"Classification took {budget}; {Limits.Describe()}{ranOut}.";
    }
}

/// <summary>
/// The outcome of one shadow run (docs/design/shadow-mode.md). The selection's decisions appear only for the classes
/// in this job's own test reports: a free shadow run must not hand out the selection for tests it did not pay for
/// (a filter that matches nothing makes the full run cost nothing). Classes the selection knows that did not run are
/// listed by name only, a project none of whose tests ran shows no decision at all, and every total counts the
/// classes that ran.
/// </summary>
/// <param name="Compared">False when TestDetta could not select (the full run still ran); <see cref="NoResultReason"/> says why.</param>
/// <param name="NoResultReason">Why there is no shadow result, or null.</param>
/// <param name="Projects">Every test project of the repository.</param>
/// <param name="Reproduction">How to check the result.</param>
/// <param name="AnalysisSeconds">How long TestDetta's analysis took.</param>
internal sealed record ShadowResult(bool Compared, string? NoResultReason, IReadOnlyList<ShadowProject> Projects, ShadowReproduction Reproduction, double AnalysisSeconds)
{
    /// <summary>True when classification reruns were switched off.</summary>
    public bool ClassificationOff { get; init; }

    /// <summary>The classification's limits and time; null when it did not run (switched off, or nothing to compare).</summary>
    public ShadowClassification? Classification { get; init; }

    /// <summary>How the full run was made and how long it took; null in reports built without one.</summary>
    public ShadowRunInfo? Run { get; init; }

    /// <summary>The time limit of the analysis before the full run (<c>--shadow-analysis-timeout</c>); null for none.</summary>
    public TimeSpan? AnalysisTimeout { get; init; }

    /// <summary>Failed classes with the given verdict, with their project.</summary>
    /// <param name="verdict">Verdict to list.</param>
    public IEnumerable<(ShadowProject Project, ShadowClass Class)> With(ShadowVerdict verdict) =>
        Projects.SelectMany(p => p.Classes.Where(c => c.Verdict == verdict).Select(c => (p, c)));

    /// <summary>
    /// Projects whose run failed without a test report naming a failed class, and projects the failed command runs
    /// that wrote no report at all: the selection cannot be checked there.
    /// </summary>
    public IEnumerable<ShadowProject> Unexplained =>
        Projects.Where(static p => p.MissingReport || p.ExitCode is not (null or 0) && (!p.Reported || !p.Classes.Any(static c => c.Failed)));

    /// <summary>
    /// True when the full run failed and no project's report says so, and no project it runs is missing a report: a
    /// build error outside the test projects, which the selection cannot be checked against either.
    /// </summary>
    public bool UnexplainedRun => Run is { ExitCode: not 0 } && !Projects.Any(static p => p.ExitCode is not (null or 0) || p.MissingReport);

    /// <summary>Classes the selection knows that did not run in this job, over every project.</summary>
    public int NotRunClasses => Projects.Sum(static p => p.NotRun.Count);

    /// <summary>Test classes compared: those in this job's reports.</summary>
    public int Classes => Projects.Where(static p => p.Ran).Sum(static p => p.Classes.Count);

    /// <summary>The compared classes the selection would have run.</summary>
    public int SelectedClasses => Projects.Where(static p => p.Ran).Sum(static p => p.Classes.Count(static c => c.Selected));

    /// <summary>Test time of the projects compared (<see cref="ShadowProject.WallSeconds"/> added up).</summary>
    public double FullSeconds => Projects.Where(static p => p.Ran).Sum(static p => p.WallSeconds);

    /// <summary>The part of <see cref="FullSeconds"/> the selection would have spent.</summary>
    public double SelectedSeconds => Projects.Where(static p => p.Ran).Sum(static p => p.SelectedSeconds);

    /// <summary>The first line of every report.</summary>
    public string Verdict()
    {
        if (!Compared)
        {
            return $"No shadow result: {NoResultReason}. The full suite ran as usual.";
        }

        var classes = Classes;
        var selected = SelectedClasses;
        var share = classes == 0 ? "0%" : (selected / (double)classes).ToString("0.0%", CultureInfo.InvariantCulture);
        var scope = $"Would have run {selected} of {classes} test classes ({share}), {Durations.Format(SelectedSeconds)} of {Durations.Format(FullSeconds)} of test time";
        if (Run is { } run)
        {
            scope += $"; the whole test run took {Durations.Format(run.WallSeconds)}";
        }

        if (NotRunClasses is > 0 and var notRun)
        {
            scope += $"; {notRun} class(es) the selection knows did not run in this job and are not compared";
        }

        if (Projects.Count(static p => p.NotBuilt) is > 0 and var notBuilt)
        {
            scope += $"; {notBuilt} testing-platform project(s) were not built before the run, so they wrote no test report and are not compared " +
                     "(build first and run dotnet test --no-build)";
        }

        var misses = With(ShadowVerdict.Miss).Count();
        var unclassified = With(ShadowVerdict.Unclassified).Count();
        var unexplained = Unexplained.Count();
        var failed = Projects.Sum(static p => p.Classes.Count(static c => c.Failed));
        var outcome = (misses, unclassified, unexplained, failed) switch
        {
            ( > 0, _, _, _) => $"{misses} MISS(ES): failing test classes the selection left out (listed below)",
            (_, > 0, _, _) => $"{unclassified} failing class(es) the selection left out could not be classified; treat them as misses until checked",
            (_, _, > 0, _) => $"{unexplained} test project(s) failed without a report naming a failed class, so the selection cannot be checked there",
            _ when UnexplainedRun => $"the test run failed (exit code {Run!.ExitCode}) without a report naming a failed test, so the selection cannot be checked",
            (_, _, _, 0) => "no test failed, so this run shows no miss and no catch",
            _ => "every failing test class was selected or failed for a reason the change did not cause",
        };
        return $"{scope}; {outcome}.";
    }
}

/// <summary>Writes a <see cref="ShadowResult"/> as JSON (<c>testdetta-shadow.json</c>), as a Markdown job summary and to the console.</summary>
internal static class ShadowReport
{
    /// <summary>
    /// Version of the JSON shape; raised when a field changes meaning. 2 (2026-10-07): decisions only for the classes
    /// that ran, the <c>not-shown</c> mode and <c>notRun</c>, a nullable project <c>exitCode</c>, the <c>run</c> object,
    /// and a reproduction <c>command</c> that needs no license next to <c>explainCommand</c>. 3 (2026-10-07): verdicts
    /// compare failing tests (<c>failedTests</c>), <c>flaky</c> needs a pass in the full-run context too and an
    /// order-dependent failure is a miss or pre-existing; a project with <c>notRun</c> classes is <c>partial</c>, its
    /// mode and reason derived from the classes that ran; <c>missingReport</c>; <c>run.scopeKnown</c> and
    /// <c>run.scope</c>; <c>analysisTimeoutSeconds</c>. 4 (2026-10-07): a project's <c>notBuilt</c> and
    /// <c>totals.notBuiltProjects</c> (a testing-platform project not built before the run, which wrote no report).
    /// 5 (2026-10-24): every decision is shown, since TestDetta is free: a project's <c>mode</c>, <c>reason</c> and
    /// estimate are always the selection's own (no <c>not-shown</c> mode, no <c>partial</c>), <c>notRun</c> holds objects
    /// with each class's decision, and <c>notCompared</c> says why a project has no class to compare.
    /// </summary>
    public const int SchemaVersion = 5;

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
        WriteLimit(json, "analysisTimeoutSeconds"u8, result.AnalysisTimeout);
        if (result.Run is { } run)
        {
            json.WriteStartObject("run"u8);
            json.WriteString("kind"u8, run.KindName);
            json.WriteString("command"u8, run.Command);
            WriteOptional(json, "fallbackReason"u8, run.FallbackReason);
            json.WriteBoolean("scopeKnown"u8, run.Scope is null);
            WriteOptional(json, "scope"u8, run.Scope);
            json.WriteNumber("wallSeconds"u8, Math.Round(run.WallSeconds, 3));
            json.WriteNumber("exitCode"u8, run.ExitCode);
            json.WriteStartArray("unmappedReports"u8);
            foreach (var assembly in run.UnmappedReports)
            {
                json.WriteStringValue(assembly);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        if (result.Classification is { } classification)
        {
            var limits = classification.Limits;
            json.WriteStartObject("classification"u8);
            WriteLimit(json, "rerunTimeoutSeconds"u8, limits.RerunTimeout);
            WriteLimit(json, "rerunIdleSeconds"u8, limits.RerunIdle);
            WriteLimit(json, "baseTimeoutSeconds"u8, limits.BaseTimeout);
            WriteLimit(json, "baseIdleSeconds"u8, limits.BaseIdle);
            WriteLimit(json, "budgetSeconds"u8, limits.Budget);
            json.WriteNumber("spentSeconds"u8, Math.Round(classification.Seconds, 3));
            json.WriteBoolean("budgetRanOut"u8, classification.BudgetRanOut);
            json.WriteEndObject();
        }

        json.WriteStartObject("totals"u8);
        json.WriteNumber("projects"u8, result.Projects.Count);
        json.WriteNumber("failedProjects"u8, result.Projects.Count(static p => p.ExitCode is not (null or 0)));
        json.WriteNumber("classes"u8, result.Classes);
        json.WriteNumber("selectedClasses"u8, result.SelectedClasses);
        json.WriteNumber("notRunClasses"u8, result.NotRunClasses);
        json.WriteNumber("fullSeconds"u8, Math.Round(result.FullSeconds, 3));
        json.WriteNumber("selectedSeconds"u8, Math.Round(result.SelectedSeconds, 3));
        if (result.Run is { } whole)
        {
            json.WriteNumber("wallSeconds"u8, Math.Round(whole.WallSeconds, 3));
        }

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
        json.WriteNumber("notBuiltProjects"u8, result.Projects.Count(static p => p.NotBuilt));
        json.WriteBoolean("unexplainedRun"u8, result.UnexplainedRun);
        json.WriteEndObject();

        json.WriteStartArray("projects"u8);
        foreach (var project in result.Projects)
        {
            WriteProject(json, project);
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
        json.WriteString("explainCommand"u8, reproduction.ExplainCommand);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>Writes one project of the JSON report: the classes that ran, compared, and those that did not, with their decisions.</summary>
    /// <param name="json">Writer.</param>
    /// <param name="project">The project.</param>
    private static void WriteProject(Utf8JsonWriter json, ShadowProject project)
    {
        json.WriteStartObject();
        json.WriteString("path"u8, project.Path);
        json.WriteString("mode"u8, project.Mode);
        json.WriteString("reason"u8, project.Reason);
        if (project.ExitCode is { } exitCode)
        {
            json.WriteNumber("exitCode"u8, exitCode);
        }
        else
        {
            json.WriteNull("exitCode"u8);
        }

        json.WriteNumber("wallSeconds"u8, Math.Round(project.WallSeconds, 3));
        json.WriteNumber("selectedSeconds"u8, Math.Round(project.SelectedSeconds, 3));
        if (project.Estimate is { } estimate)
        {
            json.WriteNumber("estimatedFullSeconds"u8, Math.Round(estimate.FullSeconds, 3));
            json.WriteNumber("estimatedSelectedSeconds"u8, Math.Round(estimate.SelectedSeconds, 3));
        }

        json.WriteBoolean("reported"u8, project.Reported);
        WriteOptional(json, "notCompared"u8, project.NotCompared);
        json.WriteBoolean("missingReport"u8, project.MissingReport);
        json.WriteBoolean("notBuilt"u8, project.NotBuilt);
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
            json.WriteStartArray("failedTests"u8);
            foreach (var test in testClass.FailedTests)
            {
                json.WriteStringValue(test);
            }

            json.WriteEndArray();
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
        json.WriteStartArray("notRun"u8);
        foreach (var testClass in project.NotRun)
        {
            json.WriteStartObject();
            json.WriteString("name"u8, testClass.Name);
            json.WriteBoolean("selected"u8, testClass.Selected);
            json.WriteString("decision"u8, testClass.Decision);
            json.WriteString("evidence"u8, testClass.Evidence);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// Writes the Markdown job summary. Every value from the repository (paths, class names, reasons, test names) goes
    /// into a code span (<see cref="MarkdownSummary.Code"/>), so a pull request cannot inject links, images or mentions;
    /// in a table cell its pipes are escaped too (<see cref="MarkdownSummary.TableCode"/>), so it cannot split the row.
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
        writer.WriteLine("Every test your command runs ran, as without TestDetta; the job's result is that run's. This compares it with what TestDetta would have selected, for the test classes that ran in this job.");
        writer.WriteLine();
        if (result.Run is { } run)
        {
            writer.WriteLine(run.Describe(code));
            writer.WriteLine();
        }

        if (!result.Compared)
        {
            WriteReproduction(result.Reproduction, writer, code);
            return;
        }

        foreach (var (title, verdict, meaning) in (ReadOnlySpan<(string, ShadowVerdict, string)>)[
            ("Misses", ShadowVerdict.Miss, "failed with the change, in the full run and again alone or in their project's full-run context, and passed the same way without it; the selection left them out. Please send us this report."),
            ("Not classified", ShadowVerdict.Unclassified, "failed and were left out, and could not be rerun to tell why; treat them as misses until checked."),
            ("Caught", ShadowVerdict.Caught, "failed and the selection would have run them."),
            ("Failing before the change", ShadowVerdict.PreExisting, "were left out, and every test of them that fails with the change fails on the base commit too."),
            ("Flaky", ShadowVerdict.Flaky, "were left out and failed in the full run, but passed when rerun alone and in their project's full-run context with the change."),
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

        foreach (var project in result.Projects.Where(static p => p.NotBuilt))
        {
            writer.WriteLine($"- {code(project.Path)}: {project.NotCompared}{(project.ExitCode is { } exit and not 0 ? $" (it failed with exit code {exit})" : string.Empty)}.");
        }

        foreach (var project in result.Unexplained.Where(static p => !p.NotBuilt))
        {
            writer.WriteLine(project.MissingReport
                ? $"- {code(project.Path)} wrote no test report while your command failed (exit code {result.Run?.ExitCode}): the selection cannot be checked there."
                : $"- {code(project.Path)} failed (exit code {project.ExitCode}) {(project.Reported ? "with no failed test in its report" : "without a test report")}: the selection cannot be checked there.");
        }

        if (result.UnexplainedRun)
        {
            writer.WriteLine($"- The test run failed (exit code {result.Run!.ExitCode}) and no report names a failed test: the selection cannot be checked there.");
        }

        foreach (var assembly in result.Run?.UnmappedReports ?? [])
        {
            writer.WriteLine($"- A report of {code(assembly)} could not be matched to one test project; its tests are not compared.");
        }

        writer.WriteLine();
        var cell = MarkdownSummary.TableCode;
        writer.WriteLine("| Test project | Selection | Classes run | Not run here | Time | Selection's part | TestDetta's estimate |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var project in result.Projects)
        {
            var estimate = project.Estimate is { } e ? Durations.Describe(e) : "no timings";
            var notRun = project.NotRun.Count == 0 ? "0" : $"{project.NotRun.Count} ({project.NotRun.Count(static c => c.Selected)} selected)";
            var ran = project.NotCompared is { } why ? $"none: {cell(why)}" : $"{project.Classes.Count(static c => c.Selected)} of {project.Classes.Count}";
            var part = project.Ran ? Durations.Format(project.SelectedSeconds) : string.Empty;
            writer.WriteLine($"| {cell(project.Path)} | {project.Mode} | {ran} | {notRun} | {Durations.Format(project.WallSeconds)} | {part} | {estimate} |");
        }

        writer.WriteLine();
        writer.WriteLine(TimeNote(result.Run));
        writer.WriteLine();
        if (result.Classification is { } classification)
        {
            writer.WriteLine(classification.Describe());
            writer.WriteLine();
        }

        WriteReproduction(result.Reproduction, writer, code);
    }

    /// <summary>Writes the verdict, how the run was made, the selection per project and the lists to the console after the full run's output.</summary>
    /// <param name="result">What to write.</param>
    /// <param name="stdout">Destination.</param>
    public static void WriteConsole(ShadowResult result, TextWriter stdout)
    {
        stdout.WriteLine("td shadow:");
        stdout.WriteLine($"  {result.Verdict()}");
        if (result.Run is { } run)
        {
            stdout.WriteLine($"  {run.Describe()}");
        }

        foreach (var project in result.Compared ? result.Projects : [])
        {
            var notRun = project.NotRun.Count > 0
                ? $"; {project.NotRun.Count} class(es) did not run in this job, {project.NotRun.Count(static c => c.Selected)} of them selected"
                : string.Empty;
            stdout.WriteLine(project.NotCompared is { } why
                ? $"  {project.Path}: {project.Mode} ({project.Reason}); not compared: {why}{notRun}"
                : $"  {project.Path}: {project.Mode}, would have run {project.Classes.Count(static c => c.Selected)} of the {project.Classes.Count} class(es) that ran ({project.Reason}){notRun}");
        }

        foreach (var (label, verdict) in (ReadOnlySpan<(string, ShadowVerdict)>)[
            ("MISS", ShadowVerdict.Miss), ("not classified", ShadowVerdict.Unclassified), ("caught", ShadowVerdict.Caught),
            ("failing before the change", ShadowVerdict.PreExisting), ("flaky", ShadowVerdict.Flaky)])
        {
            foreach (var (project, testClass) in result.With(verdict))
            {
                stdout.WriteLine($"  {label}: {testClass.Name} ({project.Path}){(testClass.Note is { } note ? $": {note}" : string.Empty)}");
            }
        }

        // A project not built before the run is named with its reason in the list of projects above.
        foreach (var project in result.Unexplained.Where(static p => !p.NotBuilt))
        {
            stdout.WriteLine(project.MissingReport
                ? $"  no test report while your command failed: {project.Path}"
                : $"  failed without a failed class in a report: {project.Path} (exit code {project.ExitCode})");
        }

        if (result.UnexplainedRun)
        {
            stdout.WriteLine($"  the test run failed (exit code {result.Run!.ExitCode}) without a report naming a failed test");
        }

        foreach (var assembly in result.Run?.UnmappedReports ?? [])
        {
            stdout.WriteLine($"  a report of {assembly} could not be matched to one test project; its tests are not compared");
        }

        if (result.Classification is { } classification)
        {
            stdout.WriteLine($"  {classification.Describe()}");
        }

        stdout.WriteLine($"  reproduce this result: {result.Reproduction.Command}");
        stdout.WriteLine($"  list every decision without running tests: {result.Reproduction.ExplainCommand}");
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

    /// <summary>How the times in the table were measured, which depends on how the full run was made.</summary>
    /// <param name="run">The full run, or null.</param>
    private static string TimeNote(ShadowRunInfo? run) => run?.Kind == ShadowRunKind.Verbatim
        ? "Your command ran the projects in one go, so a project's time is its test reports' start to finish (test host only; the build is shared and counted only in the whole run's time). The test time above adds these up; the selection's part of a filtered project is that time outside its test classes (start-up, discovery) plus the measured time of the selected classes. Only classes that ran here are counted."
        : "Each project ran on its own, so a project's time is its measured `dotnet test`, build included. The selection's part of a filtered project is its time outside test classes (build, start-up, discovery) plus the measured time of the selected classes. Only classes that ran here are counted.";

    /// <summary>The reproduction block of the Markdown summary.</summary>
    /// <param name="reproduction">What to show.</param>
    /// <param name="writer">Destination.</param>
    /// <param name="code">Code span formatter.</param>
    private static void WriteReproduction(ShadowReproduction reproduction, TextWriter writer, Func<string, string> code)
    {
        writer.WriteLine("<details><summary>Reproduce this result</summary>");
        writer.WriteLine();
        writer.WriteLine($"- TestDetta {code(reproduction.Version)}{(reproduction.BinarySha256 is { } sha ? $", SHA-256 {code(sha)}" : string.Empty)}");
        writer.WriteLine($"- compared with {code(reproduction.BaseRef)}, merge base {code(reproduction.MergeBase ?? "unknown")}, head {code(reproduction.Head ?? "unknown")}");
        foreach (var (testProject, commit, mapSha) in reproduction.Maps)
        {
            writer.WriteLine($"- map of {code(testProject)} recorded at {code(commit)}, SHA-256 {code(mapSha)}");
        }

        writer.WriteLine($"- options: {code(string.Join(' ', reproduction.Options))}");
        writer.WriteLine($"- at the head commit, with these maps in {code(".testdetta/coverage")} (compare their SHA-256): {code(reproduction.Command)}");
        writer.WriteLine($"- every decision without running tests: {code(reproduction.ExplainCommand)}");
        writer.WriteLine();
        writer.WriteLine("</details>");
        writer.WriteLine();
    }

    /// <summary>Writes a limit in seconds, or null for no limit.</summary>
    /// <param name="json">Writer.</param>
    /// <param name="name">Property name.</param>
    /// <param name="limit">The limit, or null for none.</param>
    private static void WriteLimit(Utf8JsonWriter json, ReadOnlySpan<byte> name, TimeSpan? limit)
    {
        if (limit is { } time)
        {
            json.WriteNumber(name, Math.Round(time.TotalSeconds, 3));
        }
        else
        {
            json.WriteNull(name);
        }
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
