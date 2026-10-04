using System.Globalization;
using System.Net;
using System.Text;
using TestDetta.Cli;

namespace TestDetta.Eval;

/// <summary>
/// Keeps the <c>summary.json</c> of every corpus run in a history directory and draws
/// <c>dashboard.html</c> from all of them: per repository the latest result (misses, selection,
/// precision, time, reuse) and its trend over the runs, so a regression shows at a glance. The page
/// is static HTML with inline SVG and no script; every value is HTML-encoded, since statuses carry
/// failure messages from the repositories.
/// </summary>
internal static class BatchHistory
{
    /// <summary>Runs shown in a trend line; older ones stay in the directory.</summary>
    private const int TrendRuns = 30;

    /// <summary>Copies the run's summary into the history and redraws the dashboard; returns the dashboard's path.</summary>
    /// <param name="runDirectory">Corpus output directory, holding <c>summary.json</c>.</param>
    /// <param name="historyDirectory">History directory.</param>
    /// <param name="at">When the run ended; names the history file.</param>
    public static string Record(string runDirectory, string historyDirectory, DateTimeOffset at)
    {
        Directory.CreateDirectory(historyDirectory);
        var name = at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".json";
        File.Copy(Path.Combine(runDirectory, "summary.json"), Path.Combine(historyDirectory, name), overwrite: true);

        var runs = new List<(DateTimeOffset At, List<BatchSummary.RepositoryTotals> Totals)>();
        foreach (var file in Directory.EnumerateFiles(historyDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            if (DateTimeOffset.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var when) && BatchSummary.ReadSummary(file) is { } totals)
            {
                runs.Add((when, totals));
            }
        }

        var dashboard = Path.Combine(historyDirectory, "dashboard.html");
        File.WriteAllText(dashboard, Dashboard(runs));
        return dashboard;
    }

    /// <summary>The dashboard page: totals of the latest run, then one row per repository with its trends.</summary>
    /// <param name="runs">Runs in time order.</param>
    internal static string Dashboard(IReadOnlyList<(DateTimeOffset At, List<BatchSummary.RepositoryTotals> Totals)> runs)
    {
        var html = new StringBuilder();
        html.AppendLine("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>TestDetta corpus</title>
            <style>
            :root { --bg: #fbfbfa; --fg: #1d1d1b; --muted: #6b6a66; --line: #e3e2de; --bad: #b42318; --good: #2f6f3e; --accent: #3d5a98; }
            @media (prefers-color-scheme: dark) { :root { --bg: #161615; --fg: #ecebe8; --muted: #9c9a94; --line: #2c2b29; --bad: #f97066; --good: #7cc48d; --accent: #8fa8e0; } }
            body { background: var(--bg); color: var(--fg); font: 14px/1.45 system-ui, sans-serif; margin: 0; padding: 24px 16px; }
            main { max-width: 1100px; margin: 0 auto; }
            h1 { font-size: 20px; margin: 0 0 4px; } p { margin: 0 0 16px; color: var(--muted); }
            .scroll { overflow-x: auto; } table { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }
            th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--line); white-space: nowrap; }
            th { color: var(--muted); font-weight: 500; } td.num { text-align: right; }
            .bad { color: var(--bad); font-weight: 600; } .good { color: var(--good); } .status { max-width: 260px; overflow: hidden; text-overflow: ellipsis; }
            svg { display: block; } polyline { fill: none; stroke: var(--accent); stroke-width: 1.5; } circle.miss { fill: var(--bad); } circle.point { fill: var(--accent); }
            code { font-size: 12px; color: var(--muted); }
            </style></head><body><main>
            """);

        if (runs.Count == 0)
        {
            html.AppendLine("<h1>TestDetta corpus</h1><p>No runs recorded yet.</p></main></body></html>");
            return html.ToString();
        }

        var (at, latest) = runs[^1];
        var commits = latest.Select(static t => t.TestDettaCommit).OfType<string>().Distinct(StringComparer.Ordinal).Select(Short).DefaultIfEmpty("unknown");
        html.AppendLine(CultureInfo.InvariantCulture,
            $"<h1>TestDetta corpus</h1><p>Latest run {Encode(at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))}, " +
            $"TestDetta {Encode(string.Join(", ", commits))}; {runs.Count} run(s) recorded. " +
            $"{latest.Count(static t => t.Status == "done")} of {latest.Count} repositories done, " +
            $"<span class=\"{(latest.Sum(static t => t.Missed) > 0 ? "bad" : "good")}\">{latest.Sum(static t => t.Missed)} missed</span> of {latest.Sum(static t => t.Killing)} failing mutants.</p>");

        html.AppendLine("<div class=\"scroll\"><table><thead><tr><th>Repository</th><th>Status</th><th>Commit</th><th>Killing</th><th>Missed</th>" +
            "<th>Selected</th><th>Selected, trend</th><th>Precision</th><th>Time</th><th>Time, trend</th><th>Reused</th></tr></thead><tbody>");
        var recent = runs.Skip(Math.Max(0, runs.Count - TrendRuns)).ToList();
        foreach (var t in latest.OrderBy(static t => t.Entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            var history = recent.Select(r => r.Totals.FirstOrDefault(x => string.Equals(x.Entry.Name, t.Entry.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            html.AppendLine(CultureInfo.InvariantCulture,
                $"<tr><td>{Encode(t.Entry.Name)}</td>" +
                $"<td class=\"status{(t.Status == "done" ? string.Empty : " bad")}\" title=\"{Encode(t.Status)}\">{Encode(t.Status)}</td>" +
                $"<td><code>{Encode(Short(t.Sha))}</code></td>" +
                $"<td class=\"num\">{t.Killing}</td>" +
                $"<td class=\"num{(t.Missed > 0 ? " bad" : string.Empty)}\">{t.Missed}</td>" +
                $"<td class=\"num\">{t.SelectedShare.ToString("P1", CultureInfo.InvariantCulture)}</td>" +
                $"<td>{Sparkline([.. history.Select(static h => h is { Status: "done" } ? h.SelectedShare : (double?)null)], [.. history.Select(static h => h?.Missed > 0)])}</td>" +
                $"<td class=\"num\">{(t.Precision is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) : "-")}</td>" +
                $"<td class=\"num\">{(t.Seconds > 0 ? Encode(Durations.Format(t.Seconds)) : "-")}</td>" +
                $"<td>{Sparkline([.. history.Select(static h => h is { Seconds: > 0 } ? h.Seconds : (double?)null)], [.. history.Select(static _ => false)])}</td>" +
                $"<td class=\"num\">{t.Reused}</td></tr>");
        }

        html.AppendLine("</tbody></table></div>");
        html.AppendLine("<p>Selected: average share of recorded test classes TestDetta ran for a failing mutant. Precision: median classes run per class that failed (1.0 is exact). " +
            "Trends cover the last runs, oldest left; a red dot marks a run with a miss. Numbers of runs at different commits are not comparable one to one.</p>");
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    /// <summary>A small line chart of <paramref name="values"/>, gaps where a run has no value, red dots on runs with a miss.</summary>
    /// <param name="values">One value per run, null for none.</param>
    /// <param name="misses">Per run, whether it missed.</param>
    private static string Sparkline(IReadOnlyList<double?> values, IReadOnlyList<bool> misses)
    {
        const double width = 120, height = 24;
        var present = values.OfType<double>().ToList();
        if (present.Count == 0)
        {
            return string.Empty;
        }

        var (low, high) = (present.Min(), present.Max());
        var span = high - low;
        double X(int i) => values.Count == 1 ? width / 2 : 2 + (i * (width - 4) / (values.Count - 1));
        double Y(double v) => span == 0 ? height / 2 : height - 2 - ((v - low) * (height - 4) / span);

        var svg = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"trend\">"));
        var points = new List<string>();
        for (var i = 0; i <= values.Count; i++)
        {
            if (i < values.Count && values[i] is { } v)
            {
                points.Add(string.Create(CultureInfo.InvariantCulture, $"{X(i):0.#},{Y(v):0.#}"));
                continue;
            }

            if (points.Count == 1)
            {
                // A lone run between gaps has no line to draw.
                var xy = points[0].Split(',');
                svg.Append(CultureInfo.InvariantCulture, $"<circle class=\"point\" cx=\"{xy[0]}\" cy=\"{xy[1]}\" r=\"1.5\"/>");
            }
            else if (points.Count > 1)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{string.Join(' ', points)}\"/>");
            }

            points.Clear();
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (misses[i] && values[i] is { } v)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<circle class=\"miss\" cx=\"{X(i):0.#}\" cy=\"{Y(v):0.#}\" r=\"2.5\"/>");
            }
        }

        return svg.Append("</svg>").ToString();
    }

    /// <summary>First seven characters of a commit, or a dash.</summary>
    /// <param name="sha">Commit, or null.</param>
    private static string Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha ?? "-";

    /// <summary>HTML-encodes text for an element or a quoted attribute.</summary>
    /// <param name="value">Raw text.</param>
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
