using System.Globalization;
using System.Text;
using TestDetta.Cli;

namespace TestDetta.Eval;

/// <summary>
/// Compares a corpus run with an earlier one (a night with the night before), repository by
/// repository, and points out what needs a look beyond "no miss": a repository that stopped
/// working, TestDetta selecting more or less at the same commit, test outcomes that changed although
/// nothing else did, and runs that got much slower. Written to <c>comparison.md</c>.
/// </summary>
internal static class BatchComparison
{
    /// <summary>Change of the average selected share, in points, that is worth a look at the same commit.</summary>
    private const double ShareThreshold = 0.02;

    /// <summary>Growth of the evaluation time that is worth a look.</summary>
    private const double SlowerThreshold = 0.5;

    /// <summary>Compares the run in <paramref name="directory"/> with <paramref name="previous"/> and writes <c>comparison.md</c>.</summary>
    /// <param name="directory">Directory of the current run, with its <c>summary.json</c>.</param>
    /// <param name="previous">Directory or <c>summary.json</c> of the earlier run.</param>
    /// <returns>The comparison, or a line saying there was nothing to compare with.</returns>
    public static string Write(string directory, string previous)
    {
        var before = BatchSummary.ReadSummary(previous);
        var now = BatchSummary.ReadSummary(directory) ?? [];
        var text = before is null
            ? $"## Compared with the previous run\n\nNo summary.json in {previous}; nothing to compare with.\n"
            : Markdown(before, now);
        File.WriteAllText(Path.Combine(directory, "comparison.md"), text);
        return text;
    }

    /// <summary>One row per repository of the current run, then the findings.</summary>
    /// <param name="before">Earlier run.</param>
    /// <param name="now">Current run.</param>
    internal static string Markdown(List<BatchSummary.RepositoryTotals> before, List<BatchSummary.RepositoryTotals> now)
    {
        var earlier = before.ToDictionary(static t => t.Entry.Name, StringComparer.OrdinalIgnoreCase);
        var findings = new List<string>();
        var text = new StringBuilder();
        text.AppendLine("## Compared with the previous run");
        text.AppendLine();
        text.AppendLine("| Repository | Commit | Status | Killing | Missed | Selected | Time |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var t in now)
        {
            if (!earlier.TryGetValue(t.Entry.Name, out var b))
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Cell(t.Entry.Name)} | {Short(t.Sha)} (new) | {Cell(t.Status)} | {t.Killing} | {t.Missed} | {t.SelectedShare:P1} | {Durations.Format(t.Seconds)} |");
                continue;
            }

            var sameCommit = t.Sha is not null && string.Equals(t.Sha, b.Sha, StringComparison.Ordinal);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(t.Entry.Name)} | {(sameCommit ? Short(t.Sha) : $"{Short(b.Sha)} → {Short(t.Sha)}")} | {Change(b.Status, t.Status)} | {Change(b.Killing, t.Killing)} | {Change(b.Missed, t.Missed)} | {Change(b.SelectedShare, t.SelectedShare)} | {Durations.Format(b.Seconds)} → {Durations.Format(t.Seconds)} |");
            findings.AddRange(Findings(b, t, sameCommit));
        }

        foreach (var gone in before.Where(b => !now.Any(t => string.Equals(t.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase))))
        {
            findings.Add($"{Cell(gone.Entry.Name)}: evaluated last time, not in this run");
        }

        text.AppendLine();
        if (findings.Count == 0)
        {
            text.AppendLine("Nothing changed that needs a look.");
            return text.ToString();
        }

        text.AppendLine("### Worth a look");
        text.AppendLine();
        foreach (var finding in findings)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {finding}");
        }

        return text.ToString();
    }

    /// <summary>
    /// What changed in one repository that a person should look at. Selection and test outcomes are
    /// only compared at the same commit with the same toolchain: when the repository moved, its tests
    /// and code changed too and a different number says nothing about TestDetta.
    /// </summary>
    /// <param name="before">Earlier totals.</param>
    /// <param name="now">Current totals.</param>
    /// <param name="sameCommit">Both runs evaluated the same commit.</param>
    private static IEnumerable<string> Findings(BatchSummary.RepositoryTotals before, BatchSummary.RepositoryTotals now, bool sameCommit)
    {
        var name = Cell(now.Entry.Name);
        if (before.Status == "done" && now.Status != "done")
        {
            yield return $"{name}: {Cell(now.Status)} (was done)";
        }

        if (now.Missed > before.Missed)
        {
            yield return $"{name}: {now.Missed} missed mutant(s), {before.Missed} before";
        }

        if (now.Status != "done" || before.Status != "done")
        {
            yield break;
        }

        var sameToolchain = string.Equals(before.Toolchain, now.Toolchain, StringComparison.Ordinal);
        if (sameCommit && sameToolchain)
        {
            var share = now.SelectedShare - before.SelectedShare;
            if (share > ShareThreshold)
            {
                yield return $"{name}: TestDetta selects {share * 100:0.#} points more at the same commit (precision)";
            }
            else if (share < -ShareThreshold)
            {
                yield return $"{name}: TestDetta selects {-share * 100:0.#} points less at the same commit; check that nothing it needs was dropped";
            }

            if (now.Killing != before.Killing)
            {
                yield return $"{name}: {now.Killing} killing mutants, {before.Killing} before, at the same commit and toolchain: the suite or the evaluation behaves differently";
            }
        }
        else if (!sameToolchain)
        {
            yield return $"{name}: toolchain {before.Toolchain ?? "dotnet on PATH"} → {now.Toolchain ?? "dotnet on PATH"}";
        }

        if (before.Seconds > 0 && now.Seconds > before.Seconds * (1 + SlowerThreshold))
        {
            yield return $"{name}: {Durations.Format(now.Seconds)}, {Durations.Format(before.Seconds)} before";
        }
    }

    /// <summary><c>a → b</c>, or <c>b</c> when equal.</summary>
    /// <param name="before">Earlier value.</param>
    /// <param name="now">Current value.</param>
    private static string Change(int before, int now) =>
        before == now ? now.ToString(CultureInfo.InvariantCulture) : $"{before} → {now}";

    /// <summary>Shares as percentages, <c>a → b</c> when they differ in the shown precision.</summary>
    /// <param name="before">Earlier share.</param>
    /// <param name="now">Current share.</param>
    private static string Change(double before, double now)
    {
        var a = before.ToString("P1", CultureInfo.InvariantCulture);
        var b = now.ToString("P1", CultureInfo.InvariantCulture);
        return a == b ? b : $"{a} → {b}";
    }

    /// <summary>Statuses, <c>a → b</c> when they differ.</summary>
    /// <param name="before">Earlier status.</param>
    /// <param name="now">Current status.</param>
    private static string Change(string before, string now) =>
        string.Equals(before, now, StringComparison.Ordinal) ? Cell(now) : $"{Cell(before)} → {Cell(now)}";

    /// <summary>First seven characters of a commit, or a dash.</summary>
    /// <param name="sha">Commit, or null.</param>
    private static string Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : "-";

    /// <summary>A value safe inside a Markdown table cell or list item.</summary>
    /// <param name="value">Raw text, which may come from a repository (a failure message).</param>
    private static string Cell(string value) =>
        value.ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);
}
