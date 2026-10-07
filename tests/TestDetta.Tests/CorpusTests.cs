using TestDetta.Eval;

namespace TestDetta.Tests;

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

    [Fact]
    public void An_entry_toolchain_replaces_the_default_one_as_a_whole()
    {
        var corpus = Corpus.Load(Write("""
            {
              "defaults": { "toolchain": { "sdk": "10.0.401", "runtimes": ["8.0.31"] } },
              "repositories": [
                { "name": "a", "url": "https://example.org/a.git", "toolchain": { "sdk": "10.0.401", "architecture": "x64" } },
                { "name": "b", "url": "https://example.org/b.git" },
              ],
            }
            """));

        var a = corpus.Resolve(corpus.Repositories[0]).Toolchain!.ToToolchain();
        var b = corpus.Resolve(corpus.Repositories[1]).Toolchain!.ToToolchain();

        Assert.Equal(("sdk-10.0.401", "x64"), (a.DirectoryName, a.Architecture));
        Assert.Equal(("sdk-10.0.401+8.0.31", null), (b.DirectoryName, b.Architecture));
    }

    [Fact]
    public void Entry_environment_adds_to_the_default_one_and_wins_on_the_same_name()
    {
        // The default pins the locale for every repository; an entry's own variables (protobuf-net's
        // stack size) must not drop it.
        var corpus = Corpus.Load(Write("""
            {
              "defaults": { "environment": { "LC_ALL": "en_US.UTF-8", "Shared": "default" } },
              "repositories": [
                { "name": "a", "url": "https://example.org/a.git", "environment": { "DOTNET_DefaultStackSize": "1000000", "Shared": "entry" } },
                { "name": "b", "url": "https://example.org/b.git" },
              ],
            }
            """));

        var a = corpus.Resolve(corpus.Repositories[0]).Environment!;
        var b = corpus.Resolve(corpus.Repositories[1]).Environment!;

        Assert.Equal(
            [("DOTNET_DefaultStackSize", "1000000"), ("LC_ALL", "en_US.UTF-8"), ("Shared", "entry")],
            a.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => (p.Key, p.Value)));
        Assert.Equal([("LC_ALL", "en_US.UTF-8"), ("Shared", "default")], b.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => (p.Key, p.Value)));
    }

    [Theory]
    [InlineData("""{ "repositories": [] }""")]
    [InlineData("""{ "repositories": [ { "name": "a", "url": "x", "toolchain": { "sdk": "../../x" } } ] }""")]
    [InlineData("""{ "repositories": [ { "name": "a", "url": "x", "toolchain": { "sdk": "10.0.401", "architecture": "x86" } } ] }""")]
    [InlineData("""{ "repositories": [ { "name": "a", "url": "x", "toolchain": { "runtimes": ["8.0.31"] } } ] }""")]
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

    [Fact]
    public void Summary_sets_TestDetta_next_to_project_level_selection()
    {
        var corpus = Corpus.Load(Write("""{ "repositories": [ { "name": "a", "url": "x" }, { "name": "old", "url": "y" } ] }"""));
        Directory.CreateDirectory(Path.Combine(_directory, "a"));
        File.WriteAllText(Path.Combine(_directory, "a", "stdout.md"), "done");
        File.WriteAllText(Path.Combine(_directory, "a", "synthetic.json"), """
            { "error": null, "mutants": [
              { "target": "A.One", "inconclusive": false, "selectedShare": 0.1, "killed": ["T: X"], "missed": [],
                "others": { "projectLevel": { "selectedShare": 1.0, "missed": [] } } },
              { "target": "A.Two", "inconclusive": false, "selectedShare": 0.3, "killed": ["T: Y"], "missed": [],
                "others": { "projectLevel": { "selectedShare": 0.6, "missed": [] } } } ] }
            """);
        // A report from before the comparison existed shows no project-level figure.
        Directory.CreateDirectory(Path.Combine(_directory, "old"));
        File.WriteAllText(Path.Combine(_directory, "old", "stdout.md"), "done");
        File.WriteAllText(Path.Combine(_directory, "old", "synthetic.json"), """
            { "error": null, "mutants": [ { "target": "B.One", "inconclusive": false, "selectedShare": 0.5, "killed": ["T: Z"], "missed": [] } ] }
            """);

        var summary = BatchSummary.Write(_directory, corpus);

        Assert.Contains("on 1 repositories TestDetta ran 20 % of the test classes on average, project level 80 %", summary, StringComparison.Ordinal);
        Assert.Contains("| a |  | done | 2 | 0 | 0 | 20 % | 80 % |", summary, StringComparison.Ordinal);
        Assert.Contains("| old |  | done | 1 | 0 | 0 | 50 % | – |", summary, StringComparison.Ordinal);
        var totals = BatchSummary.ReadSummary(_directory)!;
        Assert.Equal(0.8, totals.Single(static t => t.Entry.Name == "a").ProjectLevelShare!.Value, 4);
    }

    [Fact]
    public void A_pinned_commit_is_evaluated_and_the_cache_lives_outside_the_round()
    {
        var corpusPath = Write("""
            { "repositories": [
              { "name": "a", "url": "x", "commit": "f9dd7546cc3d", "synthetic": 10 },
              { "name": "b", "url": "y", "branch": "dev", "synthetic": 10 } ] }
            """);
        var corpus = Corpus.Load(corpusPath);
        Assert.True(BatchOptions.TryParse(["--corpus", corpusPath, "--out", Path.Combine(_directory, "round"), "--cache", Path.Combine(_directory, "cache")], out var options, out var error), error);

        var (pinned, shared) = BatchRunner.SettingsFor(corpus.Resolve(corpus.Repositories[0]), options);
        var (branch, local) = BatchRunner.SettingsFor(corpus.Resolve(corpus.Repositories[1]), options with { Cache = null });

        Assert.Equal("f9dd7546cc3d", pinned.Branch);
        Assert.Equal(Path.Combine(_directory, "cache", "outcomes", "a"), shared.OutcomeCache);
        Assert.Equal(Path.Combine(_directory, "cache", "maps", "a"), shared.RecordingCache);
        Assert.Equal("dev", branch.Branch);
        Assert.Equal(Path.Combine(_directory, "round", "outcome-cache", "b"), local.OutcomeCache);
        Assert.Equal(Path.Combine(_directory, "round", "map-cache", "b"), local.RecordingCache);
    }

    private string Write(string json)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
