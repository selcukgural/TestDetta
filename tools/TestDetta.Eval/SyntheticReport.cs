using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TestDetta.Eval;

/// <summary>Writes a synthetic evaluation as <c>synthetic.json</c> and <c>synthetic.md</c>.</summary>
internal static class SyntheticReport
{
    /// <summary>Most mutants listed one per row in the Markdown report; the JSON has all of them.</summary>
    private const int MaxRows = 200;

    /// <summary>Writes both reports into <paramref name="directory"/>.</summary>
    /// <param name="directory">Report directory.</param>
    /// <param name="repository">Evaluated repository, for the heading.</param>
    /// <param name="run">The evaluation so far.</param>
    public static void Write(string directory, string repository, SyntheticRun run)
    {
        File.WriteAllText(Path.Combine(directory, "synthetic.json"), Json(repository, run));
        File.WriteAllText(Path.Combine(directory, "synthetic.md"), Markdown(repository, run));
    }

    /// <summary>Totals, the missed mutants, then one row per mutant.</summary>
    /// <param name="repository">Evaluated repository.</param>
    /// <param name="run">The evaluation.</param>
    public static string Markdown(string repository, SyntheticRun run)
    {
        var text = new StringBuilder();
        var mutants = run.Mutants;
        var injected = mutants.Count(static m => m.MutatedMethods > 0);
        var killing = mutants.Where(static m => m.Killing).ToList();
        var missed = killing.Where(static m => m.MissedClasses.Count > 0).ToList();
        var undetected = mutants.Where(static m => !m.Detected).ToList();
        var inconclusive = mutants.Count(static m => m.Inconclusive);

        text.AppendLine(CultureInfo.InvariantCulture, $"# TestDetta synthetic evaluation: {repository} at {Short(run.Sha)}");
        text.AppendLine();
        if (run.Error is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"**Could not be evaluated:** {Cell(run.Error)}");
            text.AppendLine();
        }

        if (run.Incidents.Count > 0)
        {
            text.AppendLine("## Incidents");
            text.AppendLine();
            foreach (var incident in run.Incidents)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {Cell(incident)}");
            }

            text.AppendLine();
        }

        if (run.Unreported.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"**Left out, no complete test report without mutants (none, empty or crashed):** {Cell(string.Join(", ", run.Unreported))}");
            text.AppendLine();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- Judging: {run.Judging} (docs/evaluation.md, \"Judging\")");
        if (run.Toolchain is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Toolchain: {run.Toolchain}");
        }

        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Members changed: {mutants.Count} ({mutants.Count(static m => m.Target.Covered)} covered by a recorded test)");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Mutants in a test output: {injected}; failed a test class: {killing.Count} ({mutants.Count(static m => m.CaughtByCrash)} by crashing or hanging inside the selection); inconclusive: {inconclusive}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- **Missed: {missed.Count} of {killing.Count} failing mutants ({missed.Sum(static m => m.MissedClasses.Count)} test classes)**; " +
            $"95% upper bound of the miss rate: {MissRate.UpperBound(missed.Count, killing.Count):P2}; " +
            $"worst case, every inconclusive mutant a miss: {missed.Count + inconclusive} of {killing.Count + inconclusive}, upper bound {MissRate.UpperBound(missed.Count + inconclusive, killing.Count + inconclusive):P2}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Changes TestDetta did not see: {undetected.Count} ({undetected.Count(static m => m.Killing)} of them failed a test)");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Selected on average: {Average(mutants):P0} of {run.TestClasses} recorded test classes; {Average(killing):P0} for failing mutants");
        if (mutants.Count(static m => m.StoppedOnBuild) is > 0 and var stopped)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Stopped on a failing build, as td test would (a generator mutant its consumers do not compile with; not in the averages): {stopped}");
        }

        if (OverSelection(killing, run.TestClasses) is { } ratio)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Precision: on failing mutants TestDetta ran {ratio:0.0} test classes per class that failed (median; 1.0 would be exact)");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- Full suite: {run.BaselineSeconds:0.0} s");
        if (mutants.Select(static m => m.Target.Kind).Distinct().Count() > 1)
        {
            foreach (var group in mutants.GroupBy(static m => m.Target.Kind).OrderBy(static g => g.Key))
            {
                var groupKilling = group.Count(static m => m.Killing);
                var groupMissed = group.Count(static m => m.Killing && m.MissedClasses.Count > 0);
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {MutantKinds.Name(group.Key)}: {group.Count()} mutant(s), {groupKilling} failing, {groupMissed} missed");
            }
        }
        text.AppendLine();
        AppendDrift(text, run, killing);
        AppendComparison(text, run, killing, OtherSelector.ProjectLevel, "Project level (whole affected test projects, as dotnet-affected selects)");
        AppendComparison(text, run, killing, OtherSelector.DotnetAffected, "dotnet-affected (the tool itself)");
        AppendTia(text, run, killing);

        if (inconclusive > 0)
        {
            text.AppendLine("## Inconclusive");
            text.AppendLine();
            foreach (var mutant in mutants.Where(static m => m.Inconclusive))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {Code(Member(mutant))}: {Cell(mutant.Note ?? "no reason recorded")}");
            }

            text.AppendLine();
        }

        if (missed.Count > 0)
        {
            text.AppendLine("## Missed");
            text.AppendLine();
            foreach (var mutant in missed)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {Code(Member(mutant))}{(mutant.Detected ? string.Empty : " (change not seen)")}: {string.Join(", ", mutant.MissedClasses.Select(Code))}");
            }

            text.AppendLine();
        }

        text.AppendLine("## Mutants");
        text.AppendLine();
        text.AppendLine("| # | Member | Kind | Covered | Seen | Failed classes | Missed | Selected |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (mutant, index) in mutants.Take(MaxRows).Select(static (m, i) => (m, i + 1)))
        {
            var failed = mutant.Inconclusive ? "inconclusive"
                : mutant.CaughtByCrash ? "crashed inside the selection"
                : mutant.MutatedMethods == 0 ? "not in output"
                : mutant.KilledClasses.Count.ToString(CultureInfo.InvariantCulture);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {index} | {Code(Member(mutant))} | {MutantKinds.Name(mutant.Target.Kind)} | {YesNo(mutant.Target.Covered)} | {YesNo(mutant.Detected)} | {failed} | {mutant.MissedClasses.Count} | {mutant.SelectedShare:P0} |");
        }

        if (mutants.Count > MaxRows)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{mutants.Count - MaxRows} more in synthetic.json.");
        }

        return text.ToString();
    }

    /// <summary>
    /// With drift modes: misses and selected share per way of using maps recorded <see cref="SyntheticRun.MapAge"/>
    /// commits earlier, next to the exact maps, over the failing mutants every mode was judged on.
    /// </summary>
    /// <param name="text">Report so far.</param>
    /// <param name="run">The evaluation.</param>
    /// <param name="killing">Failing mutants.</param>
    private static void AppendDrift(StringBuilder text, SyntheticRun run, List<SyntheticMutant> killing)
    {
        if (run.MapCommit is null)
        {
            return;
        }

        // A mutant caught only by a crash has no per-mode result.
        var judged = killing.Where(static m => m.Others.ContainsKey(OtherSelector.Bridge)).ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"## Map drift: maps from {Short(run.MapCommit)}, {run.MapAge} commit(s) earlier");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{judged.Count} failing mutant(s) judged in every mode.");
        text.AppendLine();
        text.AppendLine("| Maps | Missed mutants | Missed classes | Selected (failing mutants) | Precision |");
        text.AppendLine("|---|---|---|---|---|");
        AppendDriftRow(text, "exact (at the commit)", [.. judged.Select(static m => (m.MissedClasses, m.SelectedShare, m.KilledClasses.Count))], run.TestClasses);
        foreach (var mode in (OtherSelector[])[OtherSelector.Bridge, OtherSelector.NoMap, OtherSelector.Naive])
        {
            AppendDriftRow(text, DriftName(mode), [.. judged.Select(m => (m.Others[mode].Missed, m.Others[mode].SelectedShare, m.KilledClasses.Count))], run.TestClasses);
        }

        if (judged.Count > 0 && judged.All(static m => m.Others.ContainsKey(OtherSelector.Incremental)))
        {
            AppendDriftRow(text, DriftName(OtherSelector.Incremental), [.. judged.Select(static m => (m.Others[OtherSelector.Incremental].Missed, m.Others[OtherSelector.Incremental].SelectedShare, m.KilledClasses.Count))], run.TestClasses);
        }

        text.AppendLine();
        AppendIncrementalSteps(text, run);
    }

    /// <summary>
    /// The incremental recording step from the older maps per test project: what it recorded, what it cost against the
    /// full recording, and the classes whose full entry ran code the incremental map lacked (docs/design/incremental-recording.md).
    /// </summary>
    /// <param name="text">Report so far.</param>
    /// <param name="run">The evaluation.</param>
    private static void AppendIncrementalSteps(StringBuilder text, SyntheticRun run)
    {
        if (run.IncrementalSteps.Count == 0)
        {
            return;
        }

        text.AppendLine("### Incremental recording step");
        text.AppendLine();
        text.AppendLine("| Test project | Step | Classes recorded | Seconds (step / full) | Divergent classes | Reason |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var step in run.IncrementalSteps)
        {
            var full = step.FullSeconds is { } seconds ? seconds.ToString("0", CultureInfo.InvariantCulture) : "-";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {step.Project} | {step.Kind} | {step.Recorded} of {step.Total} | {step.Seconds:0} / {full} | {step.Divergent.Count} | {step.Reason.Replace("|", "/", StringComparison.Ordinal)} |");
        }

        text.AppendLine();
        foreach (var line in run.IncrementalSteps.SelectMany(static s => s.Divergent.Select(d => $"- {s.Project}: {d}")).Take(20))
        {
            text.AppendLine(line);
        }

        text.AppendLine();
    }

    /// <summary>TestDetta next to another selector on the failing mutants both were judged on.</summary>
    /// <param name="text">Report so far.</param>
    /// <param name="run">The evaluation.</param>
    /// <param name="killing">Failing mutants.</param>
    /// <param name="selector">The other selector.</param>
    /// <param name="title">Section heading.</param>
    private static void AppendComparison(StringBuilder text, SyntheticRun run, List<SyntheticMutant> killing, OtherSelector selector, string title)
    {
        var judged = killing.Where(m => m.Others.ContainsKey(selector)).ToList();
        if (judged.Count == 0)
        {
            return;
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"## {title}");
        text.AppendLine();
        text.AppendLine("| Selector | Missed mutants | Missed classes | Selected (failing mutants) | Precision |");
        text.AppendLine("|---|---|---|---|---|");
        AppendDriftRow(text, "TestDetta", [.. judged.Select(static m => (m.MissedClasses, m.SelectedShare, m.KilledClasses.Count))], run.TestClasses);
        AppendDriftRow(text, selector == OtherSelector.DotnetAffected ? "dotnet-affected" : "project level", [.. judged.Select(m => (m.Others[selector].Missed, m.Others[selector].SelectedShare, m.KilledClasses.Count))], run.TestClasses);
        text.AppendLine();
    }

    /// <summary>
    /// With dotnet-tia compared: TestDetta's and dotnet-tia's misses, selected share and precision on the
    /// failing mutants both were judged on, how often dotnet-tia ran everything, and its analysis time.
    /// </summary>
    /// <param name="text">Report so far.</param>
    /// <param name="run">The evaluation.</param>
    /// <param name="killing">Failing mutants.</param>
    private static void AppendTia(StringBuilder text, SyntheticRun run, List<SyntheticMutant> killing)
    {
        var judged = killing.Where(static m => m.Others.ContainsKey(OtherSelector.Tia)).ToList();
        if (judged.Count == 0)
        {
            return;
        }

        var tia = judged.ConvertAll(static m => m.Others[OtherSelector.Tia]);
        var seconds = run.Mutants.Select(static m => m.Others.GetValueOrDefault(OtherSelector.Tia)?.Seconds).OfType<double>().Order().ToList();
        text.AppendLine("## dotnet-tia");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{judged.Count} failing mutant(s) judged by both; dotnet-tia ran everything for {tia.Count(static t => t.FullRun is not null)} of them; " +
            $"median analysis {(seconds.Count == 0 ? "-" : seconds[seconds.Count / 2].ToString("0.0", CultureInfo.InvariantCulture) + " s")}.");
        text.AppendLine();
        text.AppendLine("| Selector | Missed mutants | Missed classes | Selected (failing mutants) | Precision |");
        text.AppendLine("|---|---|---|---|---|");
        AppendDriftRow(text, "TestDetta", [.. judged.Select(static m => (m.MissedClasses, m.SelectedShare, m.KilledClasses.Count))], run.TestClasses);
        AppendDriftRow(text, "dotnet-tia", [.. judged.Select(static m => (m.Others[OtherSelector.Tia].Missed, m.Others[OtherSelector.Tia].SelectedShare, m.KilledClasses.Count))], run.TestClasses);
        text.AppendLine();
    }

    /// <summary>One row of the drift table.</summary>
    /// <param name="text">Report so far.</param>
    /// <param name="name">Mode name.</param>
    /// <param name="results">Missed classes, selected share and failed classes per judged mutant.</param>
    /// <param name="testClasses">Recorded classes the share is taken of.</param>
    private static void AppendDriftRow(StringBuilder text, string name, List<(IReadOnlyList<string> Missed, double Share, int Failed)> results, int testClasses)
    {
        var missedMutants = results.Count(static r => r.Missed.Count > 0);
        var share = results.Count == 0 ? 0 : results.Average(static r => r.Share);
        var precision = OverSelection(results.Select(static r => (r.Share, r.Failed)), testClasses);
        text.AppendLine(CultureInfo.InvariantCulture,
            $"| {name} | {missedMutants} | {results.Sum(static r => r.Missed.Count)} | {share:P0} | {(precision is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) : "-")} |");
    }

    /// <summary>Report name of a drift mode.</summary>
    /// <param name="mode">Mode.</param>
    internal static string DriftName(OtherSelector mode) => mode switch
    {
        OtherSelector.Bridge => "older, bridged",
        OtherSelector.NoMap => "none (by name)",
        OtherSelector.Incremental => "incremental step from the older maps",
        _ => "older, as if current",
    };

    /// <summary>The JSON report, one object per mutant.</summary>
    /// <param name="repository">Evaluated repository.</param>
    /// <param name="run">The evaluation.</param>
    private static string Json(string repository, SyntheticRun run)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("repository", repository);
            json.WriteString("sha", run.Sha);
            json.WriteString("error", run.Error);
            json.WriteNumber("testClasses", run.TestClasses);
            json.WriteNumber("baselineSeconds", Math.Round(run.BaselineSeconds, 1));
            json.WriteBoolean("schema", run.Schema);
            json.WriteNumber("mutantParallelism", run.MutantParallelism);
            json.WriteString("toolchain", run.Toolchain);
            json.WriteString("testdettaCommit", EvalBuild.Commit);
            json.WriteNumber("seconds", Math.Round(run.Seconds, 1));
            json.WriteNumber("mapAge", run.MapAge);
            json.WriteString("mapCommit", run.MapCommit);
            if (run.IncrementalSteps.Count > 0)
            {
                json.WriteStartArray("incrementalSteps");
                foreach (var step in run.IncrementalSteps)
                {
                    json.WriteStartObject();
                    json.WriteString("project", step.Project);
                    json.WriteString("kind", step.Kind);
                    json.WriteString("reason", step.Reason);
                    json.WriteNumber("recorded", step.Recorded);
                    json.WriteNumber("total", step.Total);
                    json.WriteNumber("seconds", step.Seconds);
                    if (step.FullSeconds is { } fullSeconds)
                    {
                        json.WriteNumber("fullSeconds", fullSeconds);
                    }

                    WriteStrings(json, "divergent", step.Divergent);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }
            json.WriteString("judging", run.Judging);
            WriteStrings(json, "incidents", run.Incidents);
            json.WriteStartArray("maps");
            foreach (var map in run.Maps)
            {
                json.WriteStartObject();
                json.WriteString("project", map.Project);
                json.WriteNumber("classes", map.Classes);
                if (map.Failed is { } failedClasses)
                {
                    json.WriteNumber("failed", failedClasses);
                }
                else
                {
                    json.WriteNull("failed");
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("unreported");
            foreach (var project in run.Unreported)
            {
                json.WriteStringValue(project);
            }

            json.WriteEndArray();
            json.WriteStartArray("mutants");
            foreach (var mutant in run.Mutants)
            {
                json.WriteStartObject();
                json.WriteString("target", Member(mutant));
                json.WriteString("kind", MutantKinds.Name(mutant.Target.Kind));
                json.WriteBoolean("covered", mutant.Target.Covered);
                json.WriteBoolean("detected", mutant.Detected);
                json.WriteNumber("mutatedMethods", mutant.MutatedMethods);
                json.WriteBoolean("inconclusive", mutant.Inconclusive);
                json.WriteBoolean("caughtByCrash", mutant.CaughtByCrash);
                json.WriteBoolean("reused", mutant.Reused);
                if (mutant.StoppedOnBuild)
                {
                    json.WriteBoolean("stoppedOnBuild", true);
                }

                if (mutant.Note is { } note)
                {
                    json.WriteString("note", note);
                }

                json.WriteNumber("selectedShare", Math.Round(mutant.SelectedShare, 4));
                WriteStrings(json, "killed", mutant.KilledClasses);
                WriteStrings(json, "missed", mutant.MissedClasses);
                if (mutant.NotCounted.Count > 0)
                {
                    WriteStrings(json, "notCounted", mutant.NotCounted);
                }

                if (mutant.Others.Count > 0)
                {
                    json.WriteStartObject("others");
                    foreach (var (mode, outcome) in mutant.Others.OrderBy(static d => d.Key))
                    {
                        json.WriteStartObject(JsonNamingPolicy.CamelCase.ConvertName(mode.ToString()));
                        json.WriteNumber("selectedShare", Math.Round(outcome.SelectedShare, 4));
                        WriteStrings(json, "missed", outcome.Missed);
                        if (outcome.FullRun is { } fullRun)
                        {
                            json.WriteString("fullRun", fullRun);
                        }

                        if (outcome.Seconds is { } analysisSeconds)
                        {
                            json.WriteNumber("seconds", Math.Round(analysisSeconds, 2));
                        }
                        json.WriteEndObject();
                    }

                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes a string array property.</summary>
    /// <param name="json">Writer.</param>
    /// <param name="name">Property name.</param>
    /// <param name="values">Values.</param>
    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// Median of selected classes per failing class over the failing mutants: how many classes TestDetta
    /// ran for each one that really depended on the change. Failing classes are a lower bound of the
    /// dependent ones (a test may run code without checking it), so this overstates the waste a little.
    /// Null without failing mutants or recorded classes.
    /// </summary>
    /// <param name="killing">Mutants that failed at least one class.</param>
    /// <param name="testClasses">Recorded test classes the selected share is taken of.</param>
    private static double? OverSelection(List<SyntheticMutant> killing, int testClasses) =>
        OverSelection(killing.Where(static m => !m.StoppedOnBuild).Select(static m => (m.SelectedShare, m.KilledClasses.Count)), testClasses);

    /// <summary>See <see cref="OverSelection(List{SyntheticMutant}, int)"/>; from selected shares and failed class counts, as the reports hold them.</summary>
    /// <param name="killing">Selected share and number of failed classes of each failing mutant.</param>
    /// <param name="testClasses">Recorded test classes the selected share is taken of.</param>
    internal static double? OverSelection(IEnumerable<(double Share, int Failed)> killing, int testClasses)
    {
        // A mutant caught only by a crash has no failed class to divide by.
        var ratios = killing
            .Where(static m => m.Failed > 0)
            .Select(m => Math.Max(m.Share * testClasses, m.Failed) / m.Failed)
            .Order()
            .ToList();
        if (ratios.Count == 0 || testClasses == 0)
        {
            return null;
        }

        return ratios.Count % 2 == 1 ? ratios[ratios.Count / 2] : (ratios[(ratios.Count / 2) - 1] + ratios[ratios.Count / 2]) / 2;
    }

    /// <summary>Mean selected share, 0 for no mutants; one whose analysis stopped on a failing build selected nothing and is left out.</summary>
    /// <param name="mutants">Mutants.</param>
    private static double Average(IReadOnlyCollection<SyntheticMutant> mutants) =>
        mutants.Where(static m => !m.StoppedOnBuild).Select(static m => m.SelectedShare).DefaultIfEmpty(0).Average();

    /// <summary><c>Type.Member</c> of a mutant.</summary>
    /// <param name="mutant">Mutant.</param>
    private static string Member(SyntheticMutant mutant) => $"{mutant.Target.Member.TypeName}.{mutant.Target.Member.MemberName}";

    /// <summary>First seven characters of a commit.</summary>
    /// <param name="sha">Commit.</param>
    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>"yes" or "no".</summary>
    /// <param name="value">Value.</param>
    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>A value from the evaluated repository in a code span, safe inside a table cell.</summary>
    /// <param name="value">Type, member or test class name.</param>
    private static string Code(string value) => $"`{Cell(value).Replace('`', '\'')}`";

    /// <summary>A value on one line without table separators.</summary>
    /// <param name="value">Any text.</param>
    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal);
}
