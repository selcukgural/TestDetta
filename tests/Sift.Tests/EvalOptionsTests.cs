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
}
