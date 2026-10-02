using Sift.Eval;

namespace Sift.Tests;

/// <summary>Every corpus run's summary is kept, and the dashboard shows the latest results with their trends.</summary>
public sealed class BatchHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sift-history-" + Guid.NewGuid().ToString("N"));

    public BatchHistoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Runs_are_kept_and_the_dashboard_shows_the_latest_with_trends()
    {
        var corpus = Corpus.Load(WriteCorpus());
        var history = Path.Combine(_directory, "history");

        WriteRun(share: 0.2, missed: false);
        BatchSummary.Write(_directory, corpus);
        BatchHistory.Record(_directory, history, new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
        WriteRun(share: 0.3, missed: true);
        BatchSummary.Write(_directory, corpus);
        var dashboard = File.ReadAllText(BatchHistory.Record(_directory, history, new DateTimeOffset(2026, 10, 2, 1, 0, 0, TimeSpan.Zero)));

        Assert.Equal(["20261001T010000Z.json", "20261002T010000Z.json"], Directory.GetFiles(history, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("2 run(s) recorded", dashboard, StringComparison.Ordinal);
        Assert.Contains("30.0 %", dashboard, StringComparison.Ordinal);
        Assert.Contains("<polyline", dashboard, StringComparison.Ordinal);
        Assert.Contains("class=\"miss\"", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_from_repositories_is_encoded()
    {
        var totals = new BatchSummary.RepositoryTotals(
            new CorpusEntry { Name = "a<b", Url = string.Empty }, "failed: <script>alert(1)</script>", 0, 0, 0, 0, 0, 0, 0, []);

        var page = BatchHistory.Dashboard([(DateTimeOffset.UnixEpoch, [totals])]);

        Assert.DoesNotContain("<script>", page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", page, StringComparison.Ordinal);
        Assert.Contains("a&lt;b", page, StringComparison.Ordinal);
    }

    private void WriteRun(double share, bool missed)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "a"));
        File.WriteAllText(Path.Combine(_directory, "a", "stdout.md"), "done");
        var missedClasses = missed ? "\"T: X\"" : string.Empty;
        File.WriteAllText(Path.Combine(_directory, "a", "synthetic.json"), $$"""
            { "sha": "abcdef1234", "error": null, "testClasses": 10, "seconds": 60, "mutants": [
              { "target": "A.Hit", "inconclusive": false, "selectedShare": {{share.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "killed": ["T: X"], "missed": [{{missedClasses}}] } ] }
            """);
    }

    private string WriteCorpus()
    {
        var path = Path.Combine(_directory, "corpus.json");
        File.WriteAllText(path, """{ "repositories": [ { "name": "a", "url": "x" } ] }""");
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
