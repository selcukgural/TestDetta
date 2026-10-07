using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A recording that failed on TestDetta's side says so (B1): tests that ran and passed while the tracker wrote nothing,
/// or while no test class was recorded, make the project "not recorded" with the reason, instead of a map that lists
/// every class as failed or a crash that never happened.
/// </summary>
public sealed class RecordingSafetyNetTests
{
    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    [Fact]
    public void Tests_that_passed_while_the_tracker_wrote_nothing_are_not_recorded_rather_than_rerun_as_a_crash()
    {
        var runs = Runs(new CoverageRecorder.ShardResult(0, [], Report(total: 4, failed: [], executed: ["T.A", "T.B"])));

        var failure = Assert.Throws<RecordingFailedException>(() => CoverageRecorder.ThrowIfTrackerSilent(runs, "/repo/tests/T/T.csproj"));

        Assert.True(failure.TestsPassed);
        Assert.Contains("ran and passed (4), but TestDetta's tracker wrote no coverage results", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_run_without_results_is_left_to_the_crash_reruns()
    {
        // Exit code 1 with a failed test: maybe a crash; the recorder runs it again in one process before it decides.
        CoverageRecorder.ThrowIfTrackerSilent(Runs(new CoverageRecorder.ShardResult(1, [], Report(total: 4, failed: ["T.A"], executed: ["T.A"]))), "/repo/tests/T/T.csproj");
        CoverageRecorder.ThrowIfTrackerSilent(Runs(new CoverageRecorder.ShardResult(0, ["results.json"], Report(total: 4, failed: [], executed: ["T.A"]))), "/repo/tests/T/T.csproj");
    }

    [Fact]
    public void Tests_that_passed_with_no_class_recorded_are_not_recorded_rather_than_all_failed()
    {
        var runs = Runs(new CoverageRecorder.ShardResult(0, ["results.json"], Report(total: 3, failed: [], executed: ["T.A", "T.B"])));

        var failure = Assert.Throws<RecordingFailedException>(() => CoverageRecorder.ThrowIfNoClassRecorded(runs, Map(classes: []), "/repo/tests/T/T.csproj"));

        Assert.True(failure.TestsPassed);
        Assert.Contains("recorded none of their classes", failure.Message, StringComparison.Ordinal);

        // One class recorded: the rest stay untrusted, as before (docs/design/unrecorded-classes.md).
        CoverageRecorder.ThrowIfNoClassRecorded(runs, Map(classes: ["T.A"]), "/repo/tests/T/T.csproj");
    }

    private static Task<CoverageRecorder.ShardResult>[] Runs(params CoverageRecorder.ShardResult[] results) => [.. results.Select(Task.FromResult)];

    private static TrxOutcome Report(int total, string[] failed, string[] executed) =>
        new(total, failed.ToHashSet(StringComparer.Ordinal), executed.ToHashSet(StringComparer.Ordinal)) { ReportedClasses = executed.ToHashSet(StringComparer.Ordinal) };

    private static CoverageMap Map(string[] classes) =>
        new(new CoverageMapHeader("tests/T/T.csproj", "abc", DateTimeOffset.UtcNow), [], new CoverageHits(classes.ToDictionary(static c => c, static IReadOnlyList<int> (_) => []), new Dictionary<string, IReadOnlyList<int>>(), []))
        {
            FailedClasses = None,
        };
}
