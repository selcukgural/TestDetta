using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sift.Eval;

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

        text.AppendLine(CultureInfo.InvariantCulture, $"# Sift synthetic evaluation: {repository} at {Short(run.Sha)}");
        text.AppendLine();
        if (run.Error is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"**Could not be evaluated:** {Cell(run.Error)}");
            text.AppendLine();
        }

        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Members changed: {mutants.Count} ({mutants.Count(static m => m.Target.Covered)} covered by a recorded test)");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Mutants in a test output: {injected}; failed a test class: {killing.Count}; crashed or hung: {mutants.Count(static m => m.Inconclusive)}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- **Missed: {missed.Count} of {killing.Count} failing mutants ({missed.Sum(static m => m.MissedClasses.Count)} test classes)**; " +
            $"95% upper bound of the miss rate: {MissRate.UpperBound(missed.Count, killing.Count):P2}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Changes Sift did not see: {undetected.Count} ({undetected.Count(static m => m.Killing)} of them failed a test)");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Selected on average: {Average(mutants):P0} of {run.TestClasses} recorded test classes; {Average(killing):P0} for failing mutants");
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
            var failed = mutant.Inconclusive ? "crashed or hung" : mutant.MutatedMethods == 0 ? "not in output" : mutant.KilledClasses.Count.ToString(CultureInfo.InvariantCulture);
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
                json.WriteNumber("selectedShare", Math.Round(mutant.SelectedShare, 4));
                WriteStrings(json, "killed", mutant.KilledClasses);
                WriteStrings(json, "missed", mutant.MissedClasses);
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

    /// <summary>Mean selected share, 0 for no mutants.</summary>
    /// <param name="mutants">Mutants.</param>
    private static double Average(IReadOnlyCollection<SyntheticMutant> mutants) =>
        mutants.Count == 0 ? 0 : mutants.Average(static m => m.SelectedShare);

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
