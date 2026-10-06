using System.Diagnostics;
using TestDetta.Coverage;

namespace TestDetta.Cli;

/// <summary>
/// The time budget of shadow mode's classification (<see cref="ShadowLimits.Budget"/>): it starts when the full
/// run is over, every rerun and the base commit's checkout draw from it, and a rerun's timeout is cut to what
/// is left, so the classification never runs past it. The full run and its exit code are outside it.
/// </summary>
/// <param name="limit">The budget, or null for none.</param>
/// <param name="elapsed">Time since the budget started; null for a stopwatch started now (tests pass their own clock).</param>
internal sealed class ShadowBudget(TimeSpan? limit, Func<TimeSpan>? elapsed = null)
{
    /// <summary>
    /// Less than this left counts as nothing left: a <c>dotnet test</c> needs seconds just to start, so a
    /// rerun given less would only be killed.
    /// </summary>
    private static readonly TimeSpan Least = TimeSpan.FromSeconds(1);

    /// <summary>When the budget started, for the default clock.</summary>
    private readonly long _started = Stopwatch.GetTimestamp();

    /// <summary>The budget, or null for none.</summary>
    public TimeSpan? Limit => limit;

    /// <summary>Time spent since the budget started.</summary>
    public TimeSpan Spent => elapsed?.Invoke() ?? Stopwatch.GetElapsedTime(_started);

    /// <summary>True when the budget is used up; never without a budget.</summary>
    public bool RanOut => limit is { } budget && budget - Spent < Least;

    /// <summary>True once the budget left a class unclassified or stopped a rerun (<see cref="Exhausted"/>).</summary>
    public bool CutShort { get; private set; }

    /// <summary>
    /// Marks that the budget cut the classification short, and returns the reason given for every class it
    /// leaves unclassified.
    /// </summary>
    public string Exhausted()
    {
        CutShort = true;
        return $"the classification time budget ({Durations.DescribeLimit(limit)}) ran out";
    }

    /// <summary>
    /// The limits of a rerun: its own, with the timeout cut to what is left of the budget, and whether that cut
    /// applied (a rerun killed after such a cut ran out of budget, not of its own time).
    /// </summary>
    /// <param name="timeout">The rerun's own timeout, or null for none.</param>
    /// <param name="idle">The rerun's idle limit, or null for none.</param>
    public (RunLimits Limits, bool Capped) For(TimeSpan? timeout, TimeSpan? idle)
    {
        if (limit is not { } budget)
        {
            return (new RunLimits(timeout, idle), false);
        }

        var left = budget - Spent;
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }

        return timeout is { } own && own <= left ? (new RunLimits(own, idle), false) : (new RunLimits(left, idle), true);
    }
}
