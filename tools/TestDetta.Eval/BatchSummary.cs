using System.Globalization;
using System.Text;
using System.Text.Json;
using TestDetta.Cli;

namespace TestDetta.Eval;

/// <summary>
/// Adds up the reports of a corpus run into <c>summary.md</c>: per repository, per tag and in total,
/// and into <c>summary.json</c>, which <see cref="BatchComparison"/> compares with an earlier run.
/// It reads the JSON reports on disk, so it also summarises a run that is still going or was stopped.
/// </summary>
internal static class BatchSummary
{
    /// <summary>What one repository's reports say.</summary>
    /// <param name="Entry">The corpus entry.</param>
    /// <param name="Status">"done", "running", "not run", or the first line of the failure.</param>
    /// <param name="Killing">Mutants (synthetic and history) that failed at least one test class.</param>
    /// <param name="Missed">Of those, mutants with a test class TestDetta did not select.</param>
    /// <param name="Selected">Summed selected share of the killing synthetic mutants, for an average.</param>
    /// <param name="SyntheticKilling">Killing synthetic mutants, the divisor of <paramref name="Selected"/>.</param>
    /// <param name="Commits">History commits evaluated.</param>
    /// <param name="FullSeconds">History: time of the full suites.</param>
    /// <param name="SelectedSeconds">History: time of TestDetta's selections.</param>
    /// <param name="MissedNames">Missed mutants as <c>Type.Member</c>, for the list at the end.</param>
    internal sealed record RepositoryTotals(
        CorpusEntry Entry,
        string Status,
        int Killing,
        int Missed,
        double Selected,
        int SyntheticKilling,
        int Commits,
        double FullSeconds,
        double SelectedSeconds,
        List<string> MissedNames)
    {
        /// <summary>Commit the synthetic changes were made on; null without a synthetic report.</summary>
        public string? Sha { get; init; }

        /// <summary>TestDetta build that evaluated it (see <see cref="EvalBuild.Commit"/>).</summary>
        public string? TestDettaCommit { get; init; }

        /// <summary>Pinned toolchain, or null for the <c>dotnet</c> on PATH.</summary>
        public string? Toolchain { get; init; }

        /// <summary>Wall-clock time of the synthetic evaluation.</summary>
        public double Seconds { get; init; }

        /// <summary>Synthetic mutants whose test outcome came from the outcome cache.</summary>
        public int Reused { get; init; }

        /// <summary>Median test classes TestDetta ran per class that failed (1.0 is exact); null without data.</summary>
        public double? Precision { get; init; }

        /// <summary>Average share of recorded test classes TestDetta selected for a killing synthetic mutant.</summary>
        public double SelectedShare => SyntheticKilling == 0 ? 0 : Selected / SyntheticKilling;

        /// <summary>
        /// Average share project-level selection (whole affected test projects, as dotnet-affected) ran
        /// for the same killing synthetic mutants; null for reports without it.
        /// </summary>
        public double? ProjectLevelShare { get; init; }

        /// <summary>Killing synthetic mutants project-level selection missed a class of; null for reports without it.</summary>
        public int? ProjectLevelMissed { get; init; }

        /// <summary>Average share dotnet-affected itself ran for the same mutants; null when it was not compared.</summary>
        public double? DotnetAffectedShare { get; init; }
    }

    /// <summary>Writes <c>summary.md</c> and returns its text.</summary>
    /// <param name="directory">Corpus output directory.</param>
    /// <param name="corpus">The corpus.</param>
    /// <param name="only">Names of the repositories the run evaluates; empty for all. Others are not counted.</param>
    public static string Write(string directory, Corpus corpus, IReadOnlySet<string>? only = null)
    {
        var totals = corpus.Repositories
            .Where(r => only is not { Count: > 0 } || only.Contains(r.Name))
            .Select(r => Read(directory, r))
            .ToList();
        var text = Markdown(totals);
        File.WriteAllText(Path.Combine(directory, "summary.md"), text);
        File.WriteAllText(Path.Combine(directory, "summary.json"), Json(totals));
        return text;
    }

    /// <summary>Reads the <c>summary.json</c> of a run directory (or the file itself); null when there is none.</summary>
    /// <param name="path">Run directory or summary file.</param>
    public static List<RepositoryTotals>? ReadSummary(string path)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, "summary.json") : path;
        using var document = ReadJson(file);
        if (document is null)
        {
            return null;
        }

        var totals = new List<RepositoryTotals>();
        foreach (var r in document.RootElement.GetProperty("repositories").EnumerateArray())
        {
            var killing = r.GetProperty("syntheticKilling").GetInt32();
            totals.Add(new RepositoryTotals(
                new CorpusEntry { Name = r.GetProperty("name").GetString()!, Url = string.Empty },
                r.GetProperty("status").GetString()!,
                r.GetProperty("killing").GetInt32(),
                r.GetProperty("missed").GetInt32(),
                r.GetProperty("selectedShare").GetDouble() * killing,
                killing,
                r.GetProperty("commits").GetInt32(),
                0,
                0,
                [.. r.GetProperty("missedMutants").EnumerateArray().Select(static m => m.GetString()!)])
            {
                Sha = r.GetProperty("sha").GetString(),
                // Runs before the rename to TestDetta (round 2c and earlier) wrote siftCommit.
                TestDettaCommit = (Optional(r, "testdettaCommit") ?? Optional(r, "siftCommit"))?.GetString(),
                Toolchain = r.GetProperty("toolchain").GetString(),
                Seconds = r.GetProperty("seconds").GetDouble(),
                Reused = r.GetProperty("reused").GetInt32(),
                Precision = Optional(r, "precision")?.GetDouble(),
                ProjectLevelShare = Optional(r, "projectLevelShare")?.GetDouble(),
                ProjectLevelMissed = Optional(r, "projectLevelMissed")?.GetInt32(),
                DotnetAffectedShare = Optional(r, "dotnetAffectedShare")?.GetDouble(),
            });
        }

        return totals;
    }

    /// <summary>The totals as JSON, one object per repository.</summary>
    /// <param name="totals">One entry per evaluated repository.</param>
    private static string Json(List<RepositoryTotals> totals)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("testdettaCommit", EvalBuild.Commit);
            json.WriteStartArray("repositories");
            foreach (var t in totals)
            {
                json.WriteStartObject();
                json.WriteString("name", t.Entry.Name);
                json.WriteString("status", t.Status);
                json.WriteString("sha", t.Sha);
                json.WriteString("testdettaCommit", t.TestDettaCommit);
                json.WriteString("toolchain", t.Toolchain);
                json.WriteNumber("seconds", Math.Round(t.Seconds, 1));
                json.WriteNumber("killing", t.Killing);
                json.WriteNumber("syntheticKilling", t.SyntheticKilling);
                json.WriteNumber("missed", t.Missed);
                json.WriteNumber("selectedShare", Math.Round(t.SelectedShare, 4));
                json.WriteNumber("reused", t.Reused);
                if (t.Precision is { } precision)
                {
                    json.WriteNumber("precision", Math.Round(precision, 2));
                }
                else
                {
                    json.WriteNull("precision");
                }

                if (t.ProjectLevelShare is { } levelShare)
                {
                    json.WriteNumber("projectLevelShare", Math.Round(levelShare, 4));
                    json.WriteNumber("projectLevelMissed", t.ProjectLevelMissed ?? 0);
                }

                if (t.DotnetAffectedShare is { } toolShare)
                {
                    json.WriteNumber("dotnetAffectedShare", Math.Round(toolShare, 4));
                }

                json.WriteNumber("commits", t.Commits);
                json.WriteStartArray("missedMutants");
                foreach (var name in t.MissedNames)
                {
                    json.WriteStringValue(name);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
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

        int killing = 0, missed = 0, syntheticKilling = 0, commits = 0, reused = 0;
        double selected = 0, fullSeconds = 0, selectedSeconds = 0, seconds = 0;
        string? sha = null, testdettaCommit = null, toolchain = null;
        double? precision = null;
        var missedNames = new List<string>();
        double projectLevel = 0, dotnetAffected = 0;
        int projectLevelMutants = 0, projectLevelMissed = 0, dotnetAffectedMutants = 0;

        using var syntheticReport = ReadJson(Path.Combine(folder, "synthetic.json"));
        if (syntheticReport is { } synthetic)
        {
            sha = Optional(synthetic.RootElement, "sha")?.GetString();
            testdettaCommit = (Optional(synthetic.RootElement, "testdettaCommit") ?? Optional(synthetic.RootElement, "siftCommit"))?.GetString();
            toolchain = Optional(synthetic.RootElement, "toolchain")?.GetString();
            seconds = Optional(synthetic.RootElement, "seconds")?.GetDouble() ?? 0;
            var failing = new List<(double, int)>();
            if (synthetic.RootElement.GetProperty("error").GetString() is { } syntheticError)
            {
                status = "failed: " + syntheticError;
            }
            else if (synthetic.RootElement.GetProperty("mutants").GetArrayLength() == 0)
            {
                // A clean result without data is no result: no test project found, or no library to change.
                status = "no data: nothing to change";
            }
            else if (synthetic.RootElement.TryGetProperty("testClasses", out var recorded) && recorded.GetInt32() == 0)
            {
                status = "no coverage: decided by name only";
            }

            foreach (var mutant in synthetic.RootElement.GetProperty("mutants").EnumerateArray())
            {
                reused += Optional(mutant, "reused")?.GetBoolean() == true ? 1 : 0;
                var caughtByCrash = mutant.TryGetProperty("caughtByCrash", out var crash) && crash.GetBoolean();
                if (mutant.GetProperty("inconclusive").GetBoolean() || (mutant.GetProperty("killed").GetArrayLength() == 0 && !caughtByCrash))
                {
                    continue;
                }

                killing++;
                syntheticKilling++;
                selected += mutant.GetProperty("selectedShare").GetDouble();
                failing.Add((mutant.GetProperty("selectedShare").GetDouble(), mutant.GetProperty("killed").GetArrayLength()));
                if (Optional(mutant, "others") is { } others && Optional(others, "projectLevel") is { } level)
                {
                    projectLevelMutants++;
                    projectLevel += level.GetProperty("selectedShare").GetDouble();
                    projectLevelMissed += level.GetProperty("missed").GetArrayLength() > 0 ? 1 : 0;
                }

                if (Optional(mutant, "others") is { } compared && Optional(compared, "dotnetAffected") is { } tool)
                {
                    dotnetAffectedMutants++;
                    dotnetAffected += tool.GetProperty("selectedShare").GetDouble();
                }
                if (mutant.GetProperty("missed").GetArrayLength() > 0)
                {
                    missed++;
                    missedNames.Add(mutant.GetProperty("target").GetString()!);
                }
            }

            precision = SyntheticReport.OverSelection(failing, Optional(synthetic.RootElement, "testClasses")?.GetInt32() ?? 0);
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

        return new RepositoryTotals(entry, status, killing, missed, selected, syntheticKilling, commits, fullSeconds, selectedSeconds, missedNames)
        {
            Sha = sha,
            TestDettaCommit = testdettaCommit,
            Toolchain = toolchain,
            Seconds = seconds,
            Reused = reused,
            Precision = precision,
            ProjectLevelShare = projectLevelMutants == 0 ? null : projectLevel / projectLevelMutants,
            ProjectLevelMissed = projectLevelMutants == 0 ? null : projectLevelMissed,
            DotnetAffectedShare = dotnetAffectedMutants == 0 ? null : dotnetAffected / dotnetAffectedMutants,
        };
    }

    /// <summary>A property that reports of older builds lack; null when missing or null.</summary>
    /// <param name="element">Report object.</param>
    /// <param name="name">Property name.</param>
    private static JsonElement? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

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

        text.AppendLine("# TestDetta corpus evaluation");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Repositories: {totals.Count(static t => t.Status == "done")} done, {totals.Count(static t => t.Status.StartsWith("failed", StringComparison.Ordinal))} failed, " +
            $"{totals.Count(static t => t.Status is "running" or "not run")} running or not run, of {totals.Count}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- **Missed: {missed} of {killing} failing mutants**; 95% upper bound of the miss rate: {MissRate.UpperBound(missed, killing):P2}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"- Synthetic changes selected {Average(totals.Sum(static t => t.Selected), totals.Sum(static t => t.SyntheticKilling)):P0} of the recorded test classes on average");
        var compared = totals.Where(static t => t.ProjectLevelShare is not null && t.SyntheticKilling > 0).ToList();
        if (compared.Count > 0)
        {
            // Mean over repositories, each weighing as one: a repository is one kind of codebase.
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Against project-level selection (whole affected test projects, as dotnet-affected): on {compared.Count} repositories TestDetta ran " +
                $"{compared.Average(static t => t.SelectedShare):P0} of the test classes on average, project level {compared.Average(static t => t.ProjectLevelShare!.Value):P0}");
        }

        var tool = totals.Where(static t => t.DotnetAffectedShare is not null && t.SyntheticKilling > 0).ToList();
        if (tool.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- Against dotnet-affected itself: on {tool.Count} repositories TestDetta ran {tool.Average(static t => t.SelectedShare):P0}, dotnet-affected {tool.Average(static t => t.DotnetAffectedShare!.Value):P0}");
        }

        var full = totals.Sum(static t => t.FullSeconds);
        var selected = totals.Sum(static t => t.SelectedSeconds);
        if (full > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- History: {totals.Sum(static t => t.Commits)} commits, {Durations.Format(selected)} with TestDetta instead of {Durations.Format(full)} ({1 - (selected / full):P0} saved)");
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
        text.AppendLine("| Repository | Tags | Status | Failing mutants | Missed | Selected | Project level | History commits | History saved |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var t in totals)
        {
            var saved = t.FullSeconds > 0 ? (1 - (t.SelectedSeconds / t.FullSeconds)).ToString("P0", CultureInfo.InvariantCulture) : "–";
            var share = t.SyntheticKilling > 0 ? Average(t.Selected, t.SyntheticKilling).ToString("P0", CultureInfo.InvariantCulture) : "–";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {Cell(t.Entry.Name)} | {Cell(string.Join(", ", t.Entry.Tags ?? []))} | {Cell(t.Status)} | {t.Killing} | {t.Missed} | {share} | {(t.ProjectLevelShare is { } level ? level.ToString("P0", CultureInfo.InvariantCulture) : "–")} | {t.Commits} | {saved} |");
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
