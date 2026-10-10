using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>A corpus run compared with the one before points out what needs a look beyond "no miss".</summary>
[Trait("Category", "Eval")]
public sealed class BatchComparisonTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-comparison-" + Guid.NewGuid().ToString("N"));

    public BatchComparisonTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Same_commit_with_a_wider_selection_or_other_outcomes_is_worth_a_look()
    {
        var text = BatchComparison.Markdown(
            [Totals("a", "aaaaaaa1", share: 0.20, killing: 40), Totals("b", "bbbbbbb1", share: 0.30, killing: 10)],
            [Totals("a", "aaaaaaa1", share: 0.25, killing: 41), Totals("b", "bbbbbbb1", share: 0.30, killing: 10)]);

        Assert.Contains("a: TestDetta selects 5 points more at the same commit", text, StringComparison.Ordinal);
        Assert.Contains("a: 41 killing mutants, 40 before", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- b:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_that_moved_is_not_judged_by_its_numbers()
    {
        var text = BatchComparison.Markdown(
            [Totals("a", "aaaaaaa1", share: 0.20, killing: 40)],
            [Totals("a", "aaaaaaa2", share: 0.40, killing: 12)]);

        Assert.Contains("aaaaaaa → aaaaaaa", text, StringComparison.Ordinal);
        Assert.Contains("Nothing changed that needs a look.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_new_misses_slow_runs_and_missing_repositories_are_listed()
    {
        var text = BatchComparison.Markdown(
            [Totals("a", "aaaaaaa1"), Totals("b", "bbbbbbb1"), Totals("c", "ccccccc1", seconds: 600), Totals("d", "ddddddd1")],
            [Totals("a", "aaaaaaa1", status: "failed: build | broke"), Totals("b", "bbbbbbb1", missed: 1), Totals("c", "ccccccc1", seconds: 1200)]);

        Assert.Contains("a: failed: build / broke (was done)", text, StringComparison.Ordinal);
        Assert.Contains("b: 1 missed mutant(s), 0 before", text, StringComparison.Ordinal);
        Assert.Contains("c: 20 m 0 s, 10 m 0 s before", text, StringComparison.Ordinal);
        Assert.Contains("d: evaluated last time, not in this run", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_json_round_trips_and_a_missing_previous_run_is_said()
    {
        var corpus = Corpus.Load(WriteCorpus());
        Directory.CreateDirectory(Path.Combine(_directory, "a"));
        File.WriteAllText(Path.Combine(_directory, "a", "stdout.md"), "done");
        File.WriteAllText(Path.Combine(_directory, "a", "synthetic.json"), """
            { "sha": "abcdef1234", "error": null, "testdettaCommit": "c0ffee", "toolchain": "SDK 10.0.401 (osx-arm64)", "seconds": 90.5, "mutants": [
              { "target": "A.Hit", "inconclusive": false, "reused": true, "selectedShare": 0.5, "killed": ["T: X"], "missed": [] } ] }
            """);

        BatchSummary.Write(_directory, corpus);
        var read = Assert.Single(BatchSummary.ReadSummary(_directory)!);

        Assert.Equal(("abcdef1234", "c0ffee", "SDK 10.0.401 (osx-arm64)", 90.5, 1, 0.5), (read.Sha, read.TestDettaCommit, read.Toolchain, read.Seconds, read.Reused, read.SelectedShare));
        Assert.Contains("nothing to compare with", BatchComparison.Write(_directory, Path.Combine(_directory, "none")), StringComparison.Ordinal);
        Assert.Contains("Nothing changed", BatchComparison.Write(_directory, _directory), StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_written_before_the_rename_still_reads()
    {
        var old = Path.Combine(_directory, "old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "summary.json"), """
            { "repositories": [ { "name": "a", "status": "done", "killing": 3, "missed": 0, "selectedShare": 0.5, "syntheticKilling": 3,
              "commits": 0, "missedMutants": [], "sha": "abcdef1234", "siftCommit": "c0ffee", "toolchain": "SDK", "seconds": 9, "reused": 0 } ] }
            """);

        var read = Assert.Single(BatchSummary.ReadSummary(old)!);

        Assert.Equal("c0ffee", read.TestDettaCommit);
    }

    private static BatchSummary.RepositoryTotals Totals(string name, string sha, double share = 0.2, int killing = 10, int missed = 0, string status = "done", double seconds = 300) =>
        new(new CorpusEntry { Name = name, Url = string.Empty }, status, killing, missed, share * killing, killing, 0, 0, 0, [])
        {
            Sha = sha,
            Seconds = seconds,
        };

    private string WriteCorpus()
    {
        var path = Path.Combine(_directory, "corpus.json");
        File.WriteAllText(path, """{ "repositories": [ { "name": "a", "url": "x" } ] }""");
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
