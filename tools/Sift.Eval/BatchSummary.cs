using System.Globalization;
using System.Text;
using System.Text.Json;
using Sift.Cli;

namespace Sift.Eval;

/// <summary>
/// Adds up the reports of a corpus run into <c>summary.md</c>: per repository, per tag and in total.
/// It reads the JSON reports on disk, so it also summarises a run that is still going or was stopped.
/// </summary>
internal static class BatchSummary
{
    /// <summary>What one repository's reports say.</summary>
    /// <param name="Entry">The corpus entry.</param>
    /// <param name="Status">"done", "running", "not run", or the first line of the failure.</param>
    /// <param name="Killing">Mutants (synthetic and history) that failed at least one test class.</param>
    /// <param name="Missed">Of those, mutants with a test class Sift did not select.</param>
    /// <param name="Selected">Summed selected share of the killing synthetic mutants, for an average.</param>
    /// <param name="SyntheticKilling">Killing synthetic mutants, the divisor of <paramref name="Selected"/>.</param>
    /// <param name="Commits">History commits evaluated.</param>
    /// <param name="FullSeconds">History: time of the full suites.</param>
    /// <param name="SelectedSeconds">History: time of Sift's selections.</param>
    /// <param name="MissedNames">Missed mutants as <c>Type.Member</c>, for the list at the end.</param>
    private sealed record RepositoryTotals(
        CorpusEntry Entry,
        string Status,
        int Killing,
        int Missed,
        double Selected,
        int SyntheticKilling,
        int Commits,
        double FullSeconds,
        double SelectedSeconds,
        List<string> MissedNames);

    /// <summary>Writes <c>summary.md</c> and returns its text.</summary>
    /// <param name="directory">Corpus output directory.</param>
    /// <param name="corpus">The corpus.</param>
    public static string Write(string directory, Corpus corpus)
    {
        var totals = corpus.Repositories.Select(r => Read(directory, r)).ToList();
        var text = Markdown(totals);
        File.WriteAllText(Path.Combine(directory, "summary.md"), text);
        return text;
    }

    /// <summary>Reads one repository's reports.</summary>
    /// <param name="directory">Corpus output directory.</param>
    /// <param name="entry">The corpus entry.</param>
    private static RepositoryTotals Read(string directory, CorpusEntry entry)
    {
        var folder = Path.Combine(directory, entry.Name);
        var error = Path.Combine(folder, "error.txt");
        var status = File.Exists(error) ? "failed: " + File.ReadLines(error).FirstOrDefault()
            : File.Exists(Path.Combine(folder, "stdout.md")) && new FileInfo(Path.Combine(folder, "stdout.md")).Length > 0 ? "done"
            : Directory.Exists(folder) ? "running"
            : "not run";

        int killing = 0, missed = 0, syntheticKilling = 0, commits = 0;
        double selected = 0, fullSeconds = 0, selectedSeconds = 0;
        var missedNames = new List<string>();

        using var syntheticReport = ReadJson(Path.Combine(folder, "synthetic.json"));
        if (syntheticReport is { } synthetic)
        {
            if (synthetic.RootElement.GetProperty("error").GetString() is { } syntheticError)
            {
                status = "failed: " + syntheticError;
            }

            foreach (var mutant in synthetic.RootElement.GetProperty("mutants").EnumerateArray())
            {
                if (mutant.GetProperty("inconclusive").GetBoolean() || mutant.GetProperty("killed").GetArrayLength() == 0)
                {
                    continue;
                }

                killing++;
                syntheticKilling++;
                selected += mutant.GetProperty("selectedShare").GetDouble();
                if (mutant.GetProperty("missed").GetArrayLength() > 0)
                {
                    missed++;
                    missedNames.Add(mutant.GetProperty("target").GetString()!);
                }
            }
        }

        using var historyReport = ReadJson(Path.Combine(folder, "results.json"));
        if (historyReport is { } history)
        {
            foreach (var merge in history.RootElement.GetProperty("merges").EnumerateArray())
            {
                if (!merge.TryGetProperty("full", out var full))
                {
                    continue;
                }

                commits++;
                fullSeconds += full.GetProperty("seconds").GetDouble();
                selectedSeconds += merge.GetProperty("selected").GetProperty("seconds").GetDouble();
                foreach (var mutant in merge.GetProperty("mutants").EnumerateArray())
                {
                    if (mutant.GetProperty("inconclusive").GetBoolean() || mutant.GetProperty("killed").GetArrayLength() == 0)
                    {
                        continue;
                    }

                    killing++;
                    if (mutant.GetProperty("missed").GetArrayLength() > 0)
                    {
                        missed++;
                        missedNames.Add(mutant.GetProperty("target").GetString()! + " (history)");
                    }
                }
            }
        }

        return new RepositoryTotals(entry, status, killing, missed, selected, syntheticKilling, commits, fullSeconds, selectedSeconds, missedNames);
    }

    /// <summary>Parses a report, or null when it does not exist yet or is being written.</summary>
    /// <param name="path">Report file.</param>
    private static JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>The summary: totals, per tag, per repository, then every missed mutant.</summary>
    /// <param name="totals">One entry per corpus repository.</param>
    private static string Markdown(List<RepositoryTotals> totals)
    {
        var text = new StringBuilder();
        var killing = totals.Sum(static t => t.Killing);
        var missed = totals.Sum(static t => t.Missed);

        text.AppendLine("# Sift corpus evaluation");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Repositories: {totals.Count(static t => t.Status == "done")} done, {totals.Count(static t => t.Status.StartsWith("failed", StringComparison.Ordinal))} failed, " +
            $"{totals.Count(static t => t.Status is "running" or "not run")} running or not run, of {totals.Count}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- **Missed: {missed} of {killing} failing mutants**; 95% upper bound of the miss rate: {MissRate.UpperBound(missed, killing):P2}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Synthetic changes selected {Average(totals.Sum(static t => t.Selected), totals.Sum(static t => t.SyntheticKilling)):P0} of the recorded test classes on average");
        var full = totals.Sum(static t => t.FullSeconds);
        var selected = totals.Sum(static t => t.SelectedSeconds);
        if (full > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- History: {totals.Sum(static t => t.Commits)} commits, {Durations.Format(selected)} with Sift instead of {Durations.Format(full)} ({1 - (selected / full):P0} saved)");
        }

        text.AppendLine();
        text.AppendLine("## By tag");
        text.AppendLine();
        text.AppendLine("| Tag | Repositories | Failing mutants | Missed | Upper bound |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var tag in totals.SelectMany(static t => t.Entry.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var tagged = totals.Where(t => t.Entry.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true).ToList();
            var tagKilling = tagged.Sum(static t => t.Killing);
            var tagMissed = tagged.Sum(static t => t.Missed);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(tag)} | {tagged.Count} | {tagKilling} | {tagMissed} | {MissRate.UpperBound(tagMissed, tagKilling):P1} |");
        }

        text.AppendLine();
        text.AppendLine("## By repository");
        text.AppendLine();
        text.AppendLine("| Repository | Tags | Status | Failing mutants | Missed | Selected | History commits | History saved |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var t in totals)
        {
            var saved = t.FullSeconds > 0 ? (1 - (t.SelectedSeconds / t.FullSeconds)).ToString("P0", CultureInfo.InvariantCulture) : "–";
            var share = t.SyntheticKilling > 0 ? Average(t.Selected, t.SyntheticKilling).ToString("P0", CultureInfo.InvariantCulture) : "–";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(t.Entry.Name)} | {Cell(string.Join(", ", t.Entry.Tags ?? []))} | {Cell(t.Status)} | {t.Killing} | {t.Missed} | {share} | {t.Commits} | {saved} |");
        }

        var misses = totals.Where(static t => t.MissedNames.Count > 0).ToList();
        if (misses.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Missed");
            text.AppendLine();
            foreach (var t in misses)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {Cell(t.Entry.Name)}: {string.Join(", ", t.MissedNames.Select(static n => $"`{Cell(n).Replace('`', '\'')}`"))}");
            }
        }

        return text.ToString();
    }

    /// <summary>Sum divided by count, 0 for no items.</summary>
    /// <param name="sum">Sum.</param>
    /// <param name="count">Count.</param>
    private static double Average(double sum, int count) => count == 0 ? 0 : sum / count;

    /// <summary>A value on one line without table separators.</summary>
    /// <param name="value">Any text.</param>
    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal);
}
