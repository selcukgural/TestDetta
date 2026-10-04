using System.Globalization;
using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>Formats seconds and time estimates for people.</summary>
internal static class Durations
{
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
