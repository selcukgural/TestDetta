using Sift.Eval;

namespace Sift.Tests;

/// <summary>Options of the evaluation tool.</summary>
public sealed class EvalOptionsTests
{
    [Fact]
    public void Schema_is_on_unless_turned_off_and_the_flag_takes_no_value()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--synthetic", "3"], out var defaults, out _));
        Assert.True(defaults.Run.UseSchema);

        Assert.True(EvalOptions.TryParse(["--repo", "r", "--no-schema", "--synthetic", "3"], out var options, out var error), error);
        Assert.False(options.Run.UseSchema);
        Assert.Equal(3, options.Target.Synthetic);
    }

    [Fact]
    public void Analysis_timeout_takes_a_positive_time_span()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), EvalOptions.TryParse(["--repo", "r"], out var defaults, out _) ? defaults.Run.AnalysisTimeout : default);
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--analysis-timeout", "00:02:30"], out var options, out _));
        Assert.Equal(TimeSpan.FromSeconds(150), options.Run.AnalysisTimeout);
        Assert.False(EvalOptions.TryParse(["--repo", "r", "--analysis-timeout", "0"], out _, out _));
    }

    [Fact]
    public void Corpus_run_can_replace_mutants_and_parallelism_for_every_repository()
    {
        Assert.True(BatchOptions.TryParse(["--corpus", "c.json", "--only", "scriban,fluid", "--synthetic", "20", "--mutant-parallelism", "2", "--parallel", "2"], out var options, out var error), error);
        Assert.Equal(new BatchOverrides(20, 2, 2), options.Overrides);
        Assert.Equal(["fluid", "scriban"], options.Only.Order(StringComparer.Ordinal));

        Assert.True(BatchOptions.TryParse(["--corpus", "c.json"], out var defaults, out _));
        Assert.Equal(new BatchOverrides(), defaults.Overrides);
        Assert.False(BatchOptions.TryParse(["--corpus", "c.json", "--synthetic", "0"], out _, out _));
    }

    [Fact]
    public void Map_age_is_zero_unless_given_for_one_repository_or_a_corpus_run()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r"], out var plain, out _));
        Assert.Equal(0, plain.Run.MapAge);
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--map-age", "10"], out var aged, out _));
        Assert.Equal(10, aged.Run.MapAge);
        Assert.False(EvalOptions.TryParse(["--repo", "r", "--map-age", "-1"], out _, out _));

        var entry = new CorpusEntry { Name = "scriban", Url = "https://example.org/scriban.git", Synthetic = 100 };
        Assert.True(BatchOptions.TryParse(["--corpus", "c.json", "--map-age", "3"], out var batch, out var error), error);
        Assert.Equal(3, BatchRunner.SettingsFor(entry, batch).Run.MapAge);
        Assert.False(BatchOptions.TryParse(["--corpus", "c.json", "--map-age", "x"], out _, out _));
    }

    [Fact]
    public void Explain_reuses_given_maps_and_takes_no_value()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r"], out var plain, out _));
        Assert.False(plain.Run.ExplainOnly);
        Assert.Null(plain.Run.Maps);

        Assert.True(EvalOptions.TryParse(["--repo", "r", "--explain", "--maps", "m", "--synthetic", "5"], out var options, out var error), error);
        Assert.True(options.Run.ExplainOnly);
        Assert.Equal(Path.GetFullPath("m"), options.Run.Maps);
        Assert.Equal(5, options.Target.Synthetic);
        Assert.False(EvalOptions.TryParse(["--repo", "r", "--maps"], out _, out _));
    }

    [Fact]
    public void Corpus_run_can_replace_mutant_kinds_and_rejects_unknown_ones()
    {
        var entry = new CorpusEntry { Name = "scriban", Url = "https://example.org/scriban.git", Synthetic = 100, MutantKinds = "all" };
        Assert.True(BatchOptions.TryParse(["--corpus", "c.json", "--mutant-kinds", "throw"], out var options, out var error), error);

        var (_, run) = BatchRunner.SettingsFor(entry, options);
        Assert.Equal([MutantKind.Throw], run.MutantKinds);

        Assert.True(BatchOptions.TryParse(["--corpus", "c.json"], out var plain, out _));
        Assert.Equal(4, BatchRunner.SettingsFor(entry, plain).Run.MutantKinds.Count);

        Assert.False(BatchOptions.TryParse(["--corpus", "c.json", "--mutant-kinds", "throw,nonsense"], out _, out var unknown));
        Assert.Contains("nonsense", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void Overrides_replace_each_repository_setting_and_leave_the_rest()
    {
        var entry = new CorpusEntry { Name = "scriban", Url = "https://example.org/scriban.git", Parallel = 4, Synthetic = 100, MutantParallelism = 1, Framework = "net10.0" };
        Assert.True(BatchOptions.TryParse(["--corpus", "c.json", "--synthetic", "20", "--mutant-parallelism", "2", "--parallel", "2"], out var options, out _));

        var (target, run) = BatchRunner.SettingsFor(entry, options);

        Assert.Equal(20, target.Synthetic);
        Assert.Equal(2, run.Parallelism);
        Assert.Equal(2, run.MutantParallelism);
        Assert.Equal("net10.0", run.Framework);

        Assert.True(BatchOptions.TryParse(["--corpus", "c.json"], out var plain, out _));
        var (plainTarget, plainRun) = BatchRunner.SettingsFor(entry, plain);
        Assert.Equal(100, plainTarget.Synthetic);
        Assert.Equal(4, plainRun.Parallelism);
        Assert.Equal(1, plainRun.MutantParallelism);
    }

    [Fact]
    public void A_pinned_toolchain_needs_an_sdk_and_a_directory_to_install_in()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--sdk", "10.0.401", "--runtime", "8.0.31", "--runtime", "9.0.12", "--architecture", "x64", "--toolchains", "t"], out var options, out var error), error);
        Assert.Equal(new Toolchain("10.0.401", ["8.0.31", "9.0.12"], "x64").DirectoryName, options.Run.Toolchain?.DirectoryName);
        Assert.Equal(Path.GetFullPath("t"), options.Run.ToolchainRoot);

        Assert.True(EvalOptions.TryParse(["--repo", "r"], out var ambient, out _));
        Assert.Null(ambient.Run.Toolchain);

        Assert.False(EvalOptions.TryParse(["--repo", "r", "--sdk", "10.0.401"], out _, out _));
        Assert.False(EvalOptions.TryParse(["--repo", "r", "--runtime", "8.0.31", "--toolchains", "t"], out _, out _));
        Assert.False(EvalOptions.TryParse(["--repo", "r", "--sdk", "../10", "--toolchains", "t"], out _, out _));
    }

    [Fact]
    public void Corpus_toolchain_is_installed_under_the_run_directory_with_the_lock_next_to_the_corpus()
    {
        var entry = new CorpusEntry { Name = "x", Url = "https://example.org/x.git", Toolchain = new CorpusToolchain { Sdk = "10.0.401", Runtimes = ["8.0.31"] } };
        var corpus = Path.GetFullPath(Path.Combine("eval", "corpus.json"));
        Assert.True(BatchOptions.TryParse(["--corpus", corpus, "--toolchains", "t"], out var options, out var error), error);

        var (_, run) = BatchRunner.SettingsFor(entry, options);

        Assert.Equal("sdk-10.0.401+8.0.31", run.Toolchain?.DirectoryName);
        Assert.Equal(Path.GetFullPath("t"), run.ToolchainRoot);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(corpus)!, "toolchains.lock.json"), run.ToolchainLock);

        Assert.True(BatchOptions.TryParse(["--corpus", corpus], out var without, out _));
        Assert.Throws<InvalidDataException>(() => BatchRunner.SettingsFor(entry, without));
    }

    [Fact]
    public void Environment_variables_come_from_the_command_line_and_the_corpus()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--env", "DOTNET_DefaultStackSize=1000000", "--env", "A=b=c"], out var options, out var error), error);
        Assert.Equal("1000000", options.Run.Environment["DOTNET_DefaultStackSize"]);
        Assert.Equal("b=c", options.Run.Environment["A"]);
        Assert.False(EvalOptions.TryParse(["--repo", "r", "--env", "novalue"], out _, out _));

        var entry = new CorpusEntry { Name = "x", Url = "u", Environment = new Dictionary<string, string> { ["X"] = "1" } };
        Assert.True(BatchOptions.TryParse(["--corpus", "c.json"], out var batch, out _));
        Assert.Equal("1", BatchRunner.SettingsFor(entry, batch).Run.Environment["X"]);
    }
}

/// <summary>An environment scope sets its variables and puts the previous values back.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class EnvironmentScopeTests
{
    [Fact]
    public void Scope_sets_and_restores_variables()
    {
        const string name = "SIFT_TEST_ENVIRONMENT_SCOPE";
        Environment.SetEnvironmentVariable(name, "before");
        using (new EnvironmentScope(new Dictionary<string, string> { [name] = "during" }))
        {
            Assert.Equal("during", Environment.GetEnvironmentVariable(name));
        }

        Assert.Equal("before", Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, null);
    }
}
