using System.Globalization;
using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>Formats seconds and time estimates for people, and reads and writes the time limits of the command line.</summary>
internal static class Durations
{
    /// <summary>
    /// Longest limit the command line accepts. A process wait takes at most about 24 days, and a week is past
    /// any CI job; <c>none</c> is there for no limit at all.
    /// </summary>
    public static readonly TimeSpan MaxLimit = TimeSpan.FromDays(7);

    /// <summary>
    /// Reads a time limit of the command line: <c>90s</c>, <c>15m</c>, <c>1h</c> or <c>1h30m</c> (hours, minutes
    /// and seconds in that order, each at most once), a plain number of minutes, or <c>0</c> or <c>none</c>
    /// for no limit. Fractions, negative values and anything over <see cref="MaxLimit"/> are rejected.
    /// </summary>
    /// <param name="value">The option's value.</param>
    /// <param name="limit">The limit, or null for no limit.</param>
    /// <returns>False when <paramref name="value"/> is not a limit.</returns>
    public static bool TryParseLimit(string value, out TimeSpan? limit)
    {
        limit = null;
        var text = value.Trim().ToLowerInvariant();
        if (text == "none")
        {
            return true;
        }

        // A plain number is minutes, the unit CI timeouts are usually given in.
        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            text += "m";
        }

        var seconds = 0L;
        var next = 0;
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            // Nine digits keep the sum far from overflowing; the maximum below rejects such values anyway.
            var unit = i < text.Length ? "hms".IndexOf(text[i], next) : -1;
            if (i == start || i - start > 9 || unit < 0)
            {
                return false;
            }

            seconds += long.Parse(text.AsSpan(start, i - start), CultureInfo.InvariantCulture) * unit switch { 0 => 3600, 1 => 60, _ => 1 };
            next = unit + 1;
            i++;
        }

        if (text.Length == 0 || seconds > MaxLimit.TotalSeconds)
        {
            return false;
        }

        limit = seconds == 0 ? null : TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>A limit as the command line takes it: <c>15m</c>, <c>1h30m</c>, <c>1m30s</c>, or <c>none</c>.</summary>
    /// <param name="limit">The limit, or null for none.</param>
    public static string Limit(TimeSpan? limit) => limit is { } time ? Parts(time, "h", "m", "s", string.Empty) : "none";

    /// <summary>A limit for people: <c>45 min</c>, <c>1 h 30 min</c>, <c>1 min 30 s</c>, or <c>no limit</c>.</summary>
    /// <param name="limit">The limit, or null for none.</param>
    public static string DescribeLimit(TimeSpan? limit) => limit is { } time ? Parts(time, " h", " min", " s", " ") : "no limit";

    /// <summary>The whole hours, minutes and seconds of <paramref name="time"/> that are not zero, with their units.</summary>
    /// <param name="time">A positive duration.</param>
    /// <param name="hours">Unit after the hours.</param>
    /// <param name="minutes">Unit after the minutes.</param>
    /// <param name="seconds">Unit after the seconds.</param>
    /// <param name="separator">Between the parts.</param>
    private static string Parts(TimeSpan time, string hours, string minutes, string seconds, string separator)
    {
        var total = (long)Math.Round(time.TotalSeconds);
        List<string> parts = [];
        foreach (var (value, unit) in (ReadOnlySpan<(long, string)>)[(total / 3600, hours), (total % 3600 / 60, minutes), (total % 60, seconds)])
        {
            if (value > 0)
            {
                parts.Add(value.ToString(CultureInfo.InvariantCulture) + unit);
            }
        }

        return parts.Count == 0 ? "0" + seconds : string.Join(separator, parts);
    }

    /// <summary><c>12.3 s</c>, <c>4 m 10 s</c> or <c>1 h 5 m</c>.</summary>
    /// <param name="seconds">Duration in seconds.</param>
    public static string Format(double seconds)
    {
        if (seconds < 60)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours} h {time.Minutes} m"
            : $"{time.Minutes} m {time.Seconds} s";
    }

    /// <summary>
    /// <c>12.3 s of 4 m 10 s (95% saved)</c>. The percentage is rounded down, so 100% only appears
    /// when nothing runs.
    /// </summary>
    /// <param name="estimate">Recorded time of everything and of the selection.</param>
    public static string Describe(TimeEstimate estimate) =>
        $"{Format(estimate.SelectedSeconds)} of {Format(estimate.FullSeconds)} ({Math.Floor(estimate.SavedFraction * 100)}% saved)";

    /// <summary>
    /// The plan's total estimate as one sentence, or null without timings. Mentions test projects
    /// without timings, which the numbers leave out.
    /// </summary>
    /// <param name="plan">Test plan.</param>
    public static string? Summarize(TestPlan plan)
    {
        if (plan.Estimate is not { } estimate)
        {
            return null;
        }

        var missing = plan.ProjectsWithoutTimings == 0
            ? string.Empty
            : $"; {plan.ProjectsWithoutTimings} test project(s) without timings are not counted";
        return $"Recorded test time: {Describe(estimate)}{missing}.";
    }
}
