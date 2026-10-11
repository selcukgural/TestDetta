using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// Seeds and replays of the synthetic sample (docs/evaluation.md, "Seeds and replays"): every run draws a new seed
/// unless one is given, reports the seed it used, and can evaluate the exact changes of an earlier run again.
/// </summary>
[Trait("Category", "Eval")]
public sealed class SampleSeedTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("testdetta-seed-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_run_draws_a_new_seed_unless_one_is_given()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--synthetic", "3"], out var drawn, out _));
        Assert.True(drawn.Target.SeedDrawn);
        Assert.InRange(drawn.Target.Seed, 1, 999_999);

        // Three draws alike would happen about once in 10^12 runs: the seeds come from chance, not a constant.
        Assert.True(Enumerable.Range(0, 3).Select(static _ => SampleSeed.Draw()).Append(drawn.Target.Seed).Distinct().Count() > 1);

        Assert.True(EvalOptions.TryParse(["--repo", "r", "--synthetic", "3", "--seed", "random"], out var random, out _));
        Assert.True(random.Target.SeedDrawn);

        Assert.True(EvalOptions.TryParse(["--repo", "r", "--synthetic", "3", "--seed", "7"], out var given, out var error), error);
        Assert.False(given.Target.SeedDrawn);
        Assert.Equal(7, given.Target.Seed);

        Assert.False(EvalOptions.TryParse(["--repo", "r", "--seed", "often"], out _, out error));
        Assert.Contains("random", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replay_takes_the_changes_and_the_seed_of_an_earlier_report()
    {
        var earlier = Report("earlier", seed: 42, ("Lib.Calc.Tax", "flip-branch"), ("Lib.Calc..ctor", "throw"), ("(file).data/rates.json", "empty-file"));

        Assert.True(EvalOptions.TryParse(["--repo", "r", "--replay", earlier], out var options, out var error), error);

        var replay = options.Run.Replay!;
        Assert.Equal(3, options.Target.Synthetic);
        Assert.Equal(42, options.Target.Seed);
        Assert.False(options.Target.SeedDrawn);
        Assert.Equal([new ReplayedMutant("Lib.Calc.Tax", MutantKind.FlipBranch), new ReplayedMutant("Lib.Calc..ctor", MutantKind.Throw), new ReplayedMutant("(file).data/rates.json", MutantKind.EmptyFile)], replay.Mutants);
        Assert.Equal(["data/rates.json"], SyntheticReplay.DataFilesOf(replay.Mutants));
        Assert.Equal("abc1234", replay.Sha);

        Assert.False(EvalOptions.TryParse(["--repo", "r", "--replay", Path.Combine(_directory, "none")], out _, out error));
        Assert.Contains("No synthetic report", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_carries_its_seed_so_a_replay_of_it_knows_the_seed()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_directory, "written")).FullName;
        var run = new SyntheticRun("abc1234def", 3, 1.5, [], null) { Seed = 314, SeedDrawn = true };

        SyntheticReport.Write(folder, "repo", run);

        Assert.Equal(314, SyntheticReplay.Load(folder).Seed);
        Assert.Contains("Sample seed: 314 (drawn", File.ReadAllText(Path.Combine(folder, "synthetic.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_corpus_run_seed_replaces_an_entry_seed_and_each_repository_otherwise_draws_its_own()
    {
        var corpusPath = Path.Combine(_directory, "corpus.json");
        File.WriteAllText(corpusPath, """
            { "repositories": [
              { "name": "pinned", "url": "x", "synthetic": 10, "seed": 5 },
              { "name": "free", "url": "y", "synthetic": 10 } ] }
            """);
        var corpus = Corpus.Load(corpusPath);
        var pinned = corpus.Resolve(corpus.Repositories[0]);
        var free = corpus.Resolve(corpus.Repositories[1]);

        Assert.True(BatchOptions.TryParse(["--corpus", corpusPath], out var plain, out _));
        Assert.Equal(5, BatchRunner.SettingsFor(pinned, plain).Target.Seed);
        Assert.True(BatchRunner.SettingsFor(free, plain).Target.SeedDrawn);

        Assert.True(BatchOptions.TryParse(["--corpus", corpusPath, "--seed", "9"], out var fixedSeed, out _));
        Assert.Equal(9, BatchRunner.SettingsFor(pinned, fixedSeed).Target.Seed);
        Assert.Equal(9, BatchRunner.SettingsFor(free, fixedSeed).Target.Seed);

        Assert.True(BatchOptions.TryParse(["--corpus", corpusPath, "--seed", "random"], out var random, out _));
        Assert.True(BatchRunner.SettingsFor(pinned, random).Target.SeedDrawn);
    }

    [Fact]
    public void A_corpus_replay_takes_each_repository_s_changes_from_the_earlier_run()
    {
        var corpusPath = Path.Combine(_directory, "corpus.json");
        File.WriteAllText(corpusPath, """{ "repositories": [ { "name": "a", "url": "x", "synthetic": 100 } ] }""");
        var corpus = Corpus.Load(corpusPath);
        var round = Path.Combine(_directory, "round");
        Report(Path.Combine("round", "a"), seed: 11, ("Lib.Calc.Tax", "return-default"));

        Assert.True(BatchOptions.TryParse(["--corpus", corpusPath, "--replay", round], out var options, out var error), error);
        var (target, run) = BatchRunner.SettingsFor(corpus.Resolve(corpus.Repositories[0]), options);

        Assert.Equal(1, target.Synthetic);
        Assert.Equal(11, target.Seed);
        Assert.Equal([new ReplayedMutant("Lib.Calc.Tax", MutantKind.ReturnDefault)], run.Replay!.Mutants);
        Assert.False(BatchOptions.TryParse(["--corpus", corpusPath, "--replay", Path.Combine(_directory, "none")], out _, out _));
    }

    /// <summary>Writes a minimal <c>synthetic.json</c> under <paramref name="folder"/> and returns its path.</summary>
    /// <param name="folder">Folder relative to the test's directory.</param>
    /// <param name="seed">Seed the report names.</param>
    /// <param name="mutants">Targets and kind names.</param>
    private string Report(string folder, int seed, params (string Target, string Kind)[] mutants)
    {
        var directory = Path.Combine(_directory, folder);
        Directory.CreateDirectory(directory);
        var list = string.Join(", ", mutants.Select(static m => $$"""{ "target": "{{m.Target}}", "kind": "{{m.Kind}}" }"""));
        var path = Path.Combine(directory, "synthetic.json");
        File.WriteAllText(path, $$"""{ "sha": "abc1234", "seed": {{seed}}, "mutants": [ {{list}} ] }""");
        return path;
    }
}
