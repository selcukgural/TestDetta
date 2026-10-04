using System.Globalization;
using System.Text;
using System.Text.Json;
using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Eval;

/// <summary>Writes evaluation results as JSON (for comparing runs) and Markdown (for reading).</summary>
internal static class EvalReport
{
    /// <summary>Writes <c>results.json</c> and <c>results.md</c> into <paramref name="directory"/>.</summary>
    /// <param name="directory">Report directory.</param>
    /// <param name="repository">Evaluated repository, for the heading.</param>
    /// <param name="results">One result per merge, oldest first.</param>
    public static void Write(string directory, string repository, IReadOnlyList<MergeResult> results)
    {
        File.WriteAllText(Path.Combine(directory, "results.json"), Json(repository, results));
        File.WriteAllText(Path.Combine(directory, "results.md"), Markdown(repository, results));
    }

    /// <summary>The Markdown report: totals, a table per merge, then every missed test class.</summary>
    /// <param name="repository">Evaluated repository.</param>
    /// <param name="results">Results.</param>
    public static string Markdown(string repository, IReadOnlyList<MergeResult> results)
    {
        var text = new StringBuilder();
        var evaluated = results.Where(static r => r.Runs is not null).ToList();
        var fullSeconds = evaluated.Sum(static r => r.Runs!.Full.Seconds);
        var selectedSeconds = evaluated.Sum(static r => r.Runs!.Selected.Seconds);
        var mutants = evaluated.SelectMany(static r => r.Safety!.Mutants).ToList();
        var killed = mutants.Count(static m => m.KilledClasses.Count > 0);
        var missedMutants = mutants.Count(static m => m.MissedClasses.Count > 0);
        var coverageMisses = evaluated.Sum(static r => r.Safety!.CoverageMisses.Count);
        var aborted = evaluated.Count(static r => r.Runs!.Full.Aborted + r.Runs.Selected.Aborted > 0);
        var inconclusive = mutants.Count(static m => m.Inconclusive);
        var notApplied = mutants.Count(static m => m.MutatedMethods == 0);

        text.AppendLine(CultureInfo.InvariantCulture, $"# TestDetta evaluation: {repository}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Merges: {evaluated.Count} evaluated, {results.Count - evaluated.Count} failed");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Test time: {Durations.Format(selectedSeconds)} with TestDetta instead of {Durations.Format(fullSeconds)} ({Percent(fullSeconds, selectedSeconds)} saved)");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Tests run: {evaluated.Sum(static r => r.Runs!.Selected.Total)} instead of {evaluated.Sum(static r => r.Runs!.Full.Total)}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Mutants: {mutants.Count} injected, {killed} caught by the full suite, **{missedMutants} missed by TestDetta**");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Coverage check: **{coverageMisses} missed test class(es)**");
        if (aborted + inconclusive + notApplied > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Caveats: {aborted} merge(s) with a crashed test host (marked ⚠), {inconclusive} mutant run(s) crashed, {notApplied} mutant(s) not found in any test output");
        }
        text.AppendLine();
        text.AppendLine("| Merge | Files | Full | TestDetta | Saved | Estimate | Mutants (caught/missed) | Decision |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var result in results)
        {
            var sha = result.Merge.Sha[..7];
            if (result.Runs is null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {sha} | {result.Merge.ChangedFiles} | failed | | | | | {Cell(result.Error ?? "")} |");
                continue;
            }

            var runs = result.Runs;
            var safety = result.Safety!;
            var estimate = result.Plan?.Estimate is { } e ? $"{Math.Floor(e.SavedFraction * 100)}%" : "–";
            var warning = runs.Full.Aborted + runs.Selected.Aborted > 0 ? " ⚠" : string.Empty;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {sha}{warning} | {result.Merge.ChangedFiles} | {runs.Full.Total} / {Durations.Format(runs.Full.Seconds)} | " +
                $"{runs.Selected.Total} / {Durations.Format(runs.Selected.Seconds)} | {Percent(runs.Full.Seconds, runs.Selected.Seconds)} | {estimate} | " +
                $"{safety.Mutants.Count(static m => m.KilledClasses.Count > 0)}/{safety.Mutants.Count(static m => m.MissedClasses.Count > 0)} | {Cell(Decisions(result.Plan!))} |");
        }

        var failures = evaluated.Where(static r => !r.Safety!.Passed).ToList();
        if (failures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Missed test classes");
            foreach (var result in failures)
            {
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"### {result.Merge.Sha[..7]} {Cell(result.Merge.Subject)}");
                foreach (var mutant in result.Safety!.Mutants.Where(static m => m.MissedClasses.Count > 0))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"- mutant `{mutant.Target.TypeName}.{mutant.Target.MemberName}`: {string.Join(", ", mutant.MissedClasses.Select(static c => $"`{c}`"))}");
                }

                foreach (var miss in result.Safety.CoverageMisses)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"- coverage: `{miss}`");
                }
            }
        }

        return text.ToString();
    }

    /// <summary>The JSON report, one object per merge.</summary>
    /// <param name="repository">Evaluated repository.</param>
    /// <param name="results">Results.</param>
    private static string Json(string repository, IReadOnlyList<MergeResult> results)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("repository", repository);
            json.WriteStartArray("merges");
            foreach (var result in results)
            {
                json.WriteStartObject();
                json.WriteString("sha", result.Merge.Sha);
                json.WriteString("parent", result.Merge.Parent);
                json.WriteString("subject", result.Merge.Subject);
                json.WriteNumber("changedFiles", result.Merge.ChangedFiles);
                json.WriteString("error", result.Error);

                if (result.Runs is { } runs)
                {
                    WriteRun(json, "full", runs.Full);
                    WriteRun(json, "selected", runs.Selected);
                    json.WriteStartArray("decisions");
                    foreach (var test in result.Plan!.Projects)
                    {
                        json.WriteStartObject();
                        json.WriteString("project", Path.GetFileName(test.Project.Path));
                        json.WriteString("mode", test.Mode.ToString());
                        json.WriteNumber("classes", test.TestClasses.Count);
                        json.WriteString("reason", test.Reason);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();

                    json.WriteStartArray("mutants");
                    foreach (var mutant in result.Safety!.Mutants)
                    {
                        json.WriteStartObject();
                        json.WriteString("target", $"{mutant.Target.TypeName}.{mutant.Target.MemberName}");
                        json.WriteNumber("mutatedMethods", mutant.MutatedMethods);
                        json.WriteBoolean("inconclusive", mutant.Inconclusive);
                        WriteStrings(json, "killed", mutant.KilledClasses);
                        WriteStrings(json, "missed", mutant.MissedClasses);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    WriteStrings(json, "coverageMisses", result.Safety.CoverageMisses);
                }

                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes a run's time, test count and report gaps.</summary>
    /// <param name="json">Writer inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="run">The run.</param>
    private static void WriteRun(Utf8JsonWriter json, string name, TestRunOutcome run)
    {
        json.WriteStartObject(name);
        json.WriteNumber("seconds", Math.Round(run.Seconds, 1));
        json.WriteNumber("tests", run.Total);
        json.WriteNumber("missingReports", run.Unreported.Count);
        json.WriteNumber("aborted", run.Aborted);
        json.WriteEndObject();
    }

    /// <summary>Writes an array of strings.</summary>
    /// <param name="json">Writer inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="values">Items.</param>
    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }
        json.WriteEndArray();
    }

    /// <summary>Short text per test project: <c>Api.Tests: 4 classes, Unit: skip</c>.</summary>
    /// <param name="plan">TestDetta's plan.</param>
    private static string Decisions(TestPlan plan) =>
        plan.Projects.Count == 0
            ? "no tests"
            : string.Join(", ", plan.Projects.Select(static t =>
                $"{Path.GetFileNameWithoutExtension(t.Project.Path)}: " + t.Mode switch
                {
                    TestRunMode.Filtered => $"{t.TestClasses.Count} classes",
                    TestRunMode.All => "all",
                    _ => "skip",
                }));

    /// <summary>Saved share of <paramref name="full"/>, rounded down.</summary>
    /// <param name="full">Full time.</param>
    /// <param name="selected">Selected time.</param>
    private static string Percent(double full, double selected) =>
        full <= 0 ? "–" : $"{Math.Floor(Math.Max(0, full - selected) / full * 100)}%";

    /// <summary>Text safe inside a Markdown table cell: pipes and line breaks removed.</summary>
    /// <param name="value">Cell text.</param>
    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal);
}
