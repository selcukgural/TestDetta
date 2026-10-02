using Sift.Eval;

namespace Sift.Tests;

public sealed class CorpusTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sift-corpus-test-" + Guid.NewGuid().ToString("N"));

    public CorpusTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Entries_take_the_defaults_they_leave_out()
    {
        var corpus = Corpus.Load(Write("""
            // comments are allowed
            {
              "defaults": { "framework": "net10.0", "synthetic": 50, "parallel": 4 },
              "repositories": [
                { "name": "a", "url": "https://example.org/a.git", "synthetic": 0, "merges": 6, "tags": ["fsharp"] },
                { "name": "b", "url": "https://example.org/b.git", },
              ],
            }
            """));

        var a = corpus.Resolve(corpus.Repositories[0]);
        var b = corpus.Resolve(corpus.Repositories[1]);

        Assert.Equal((0, 6, "net10.0", 4), (a.Synthetic, a.Merges, a.Framework, a.Parallel));
        Assert.Equal((50, null, "net10.0"), (b.Synthetic, b.Merges, b.Framework));
        Assert.Equal(["fsharp"], a.Tags);
    }

    [Theory]
    [InlineData("""{ "repositories": [] }""")]
    [InlineData("""{ "repositories": [ { "name": "a", "url": "x" }, { "name": "A", "url": "y" } ] }""")]
    [InlineData("""{ "repositories": [ { "name": "a", "url": "x", "typo": 1 } ] }""")]
    [InlineData("""{ "repositories": [ { "url": "x" } ] }""")]
    public void Invalid_corpus_is_rejected(string json) =>
        Assert.Throws<InvalidDataException>(() => Corpus.Load(Write(json)));

    [Theory]
    [InlineData("corpus.json")]
    [InlineData("corpus-extra.json")]
    public void Repository_corpus_file_is_valid(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "eval", "corpus.json")))
        {
            root = root.Parent ?? throw new InvalidOperationException("eval/corpus.json not found above the test output");
        }

        var path = Path.Combine(root.FullName, "eval", file);

        Assert.NotEmpty(Corpus.Load(path).Repositories);
    }

    [Fact]
    public void Summary_adds_up_synthetic_and_history_mutants()
    {
        var corpus = Corpus.Load(Write("""
            { "repositories": [
              { "name": "a", "url": "x", "tags": ["reflection"] },
              { "name": "b", "url": "y", "tags": ["reflection", "pinvoke"] },
              { "name": "c", "url": "z" } ] }
            """));
        Directory.CreateDirectory(Path.Combine(_directory, "a"));
        File.WriteAllText(Path.Combine(_directory, "a", "stdout.md"), "done");
        File.WriteAllText(Path.Combine(_directory, "a", "synthetic.json"), """
            { "error": null, "mutants": [
              { "target": "A.Hit", "inconclusive": false, "selectedShare": 0.5, "killed": ["T: X"], "missed": [] },
              { "target": "A.Miss", "inconclusive": false, "selectedShare": 0.1, "killed": ["T: Y"], "missed": ["T: Y"] },
              { "target": "A.Quiet", "inconclusive": false, "selectedShare": 0.0, "killed": [], "missed": [] } ] }
            """);
        Directory.CreateDirectory(Path.Combine(_directory, "b"));
        File.WriteAllText(Path.Combine(_directory, "b", "error.txt"), "clone failed\nstack");
        File.WriteAllText(Path.Combine(_directory, "b", "results.json"), """
            { "merges": [ { "full": { "seconds": 10 }, "selected": { "seconds": 4 },
              "mutants": [ { "target": "B.Run", "inconclusive": false, "killed": ["T: Z"], "missed": [] } ] } ] }
            """);

        var summary = BatchSummary.Write(_directory, corpus);

        Assert.Contains("**Missed: 1 of 3 failing mutants**", summary, StringComparison.Ordinal);
        Assert.Contains("| reflection | 2 | 3 | 1 |", summary, StringComparison.Ordinal);
        Assert.Contains("| b | reflection, pinvoke | failed: clone failed |", summary, StringComparison.Ordinal);
        Assert.Contains("| c |  | not run |", summary, StringComparison.Ordinal);
        Assert.Contains("`A.Miss`", summary, StringComparison.Ordinal);

        // A run of some repositories counts only those: the others are not "not run".
        var scoped = BatchSummary.Write(_directory, corpus, new HashSet<string>(["a"], StringComparer.OrdinalIgnoreCase));
        Assert.Contains("Repositories: 1 done, 0 failed, 0 running or not run, of 1", scoped, StringComparison.Ordinal);
        Assert.Contains("| reflection | 1 | 2 | 1 |", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("| c |", scoped, StringComparison.Ordinal);
    }

    private string Write(string json)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
