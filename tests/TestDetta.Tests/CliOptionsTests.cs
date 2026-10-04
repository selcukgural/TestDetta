using Microsoft.Extensions.Logging;
using TestDetta.Cli;

namespace TestDetta.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Test_command_passes_everything_after_double_dash_through()
    {
        var ok = CliOptions.TryParse(["test", "--base", "main", "--dry-run", "--", "-c", "Release", "--no-build"], "/repo", out var options, out _);

        Assert.True(ok);
        Assert.Equal(CliCommand.Test, options.Command);
        Assert.Equal("main", options.Analysis.BaseRef);
        Assert.True(options.Test.DryRun);
        Assert.Equal(["-c", "Release", "--no-build"], options.Test.DotnetTestArguments);
    }

    [Fact]
    public void Affected_command_reads_format_and_verbosity()
    {
        var ok = CliOptions.TryParse(["affected", "--format", "JSON", "-v", "--tests-only"], "/repo", out var options, out _);

        Assert.True(ok);
        Assert.Equal(OutputFormat.Json, options.Affected.Format);
        Assert.Equal(LogLevel.Debug, options.Analysis.LogLevel);
        Assert.True(options.Affected.TestsOnly);
        Assert.Equal("/repo", options.Analysis.RepoPath);
    }

    [Fact]
    public void Record_command_reads_no_build()
    {
        var ok = CliOptions.TryParse(["record", "--no-build", "-c", "Release"], "/repo", out var options, out _);

        Assert.True(ok);
        Assert.True(options.Record.NoBuild);
        Assert.Equal("Release", options.Record.Configuration);
    }

    [Fact]
    public void Max_map_age_takes_zero_or_more_commits()
    {
        Assert.True(CliOptions.TryParse(["affected"], "/repo", out var defaults, out _));
        Assert.Equal(TestDetta.Core.AnalysisOptions.DefaultMaxMapAge, defaults.Analysis.MaxMapAge);

        Assert.True(CliOptions.TryParse(["test", "--max-map-age", "0"], "/repo", out var exact, out _));
        Assert.Equal(0, exact.Analysis.MaxMapAge);
        Assert.False(CliOptions.TryParse(["affected", "--max-map-age", "-1"], "/repo", out _, out _));
    }

    [Fact]
    public void Drift_fallback_runs_all_unless_name_is_chosen()
    {
        Assert.True(CliOptions.TryParse(["test"], "/repo", out var defaults, out _));
        Assert.Equal(TestDetta.Core.DriftFallback.RunAll, defaults.Analysis.DriftFallback);

        Assert.True(CliOptions.TryParse(["affected", "--drift-fallback", "name"], "/repo", out var byName, out _));
        Assert.Equal(TestDetta.Core.DriftFallback.ByName, byName.Analysis.DriftFallback);
        Assert.False(CliOptions.TryParse(["affected", "--drift-fallback", "fast"], "/repo", out _, out _));
    }

    [Theory]
    [InlineData("affected", "--no-build")]
    [InlineData("affected", "--dry-run")]
    [InlineData("affected", "--")]
    [InlineData("test", "--format")]
    [InlineData("test", "--base")]
    [InlineData("deploy")]
    public void Options_of_other_commands_and_missing_values_are_rejected(params string[] args)
    {
        Assert.False(CliOptions.TryParse(args, "/repo", out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
