using Microsoft.Extensions.Logging;
using TestDetta.Cli;

namespace TestDetta.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Version_prints_the_informational_version_to_stdout()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(0, CliApp.Run(["--version"], stdout, stderr));
        Assert.Equal($"td {CliApp.Version}", stdout.ToString().Trim());
        Assert.NotEqual("unknown", CliApp.Version);
        Assert.Empty(stderr.ToString());
    }

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

    [Theory]
    [InlineData(new string[0], "Debug")]
    [InlineData(new[] { "-c", "Release" }, "Release")]
    [InlineData(new[] { "--no-build", "--configuration", "Ci" }, "Ci")]
    [InlineData(new[] { "-c:Release" }, "Release")]
    [InlineData(new[] { "--configuration=Release" }, "Release")]
    public void Test_command_reads_the_configuration_dotnet_test_builds(string[] dotnetTest, string configuration)
    {
        Assert.True(CliOptions.TryParse(["test", "--", .. dotnetTest], "/repo", out var options, out _));

        Assert.Equal(configuration, options.Test.Configuration);
    }

    [Fact]
    public void Affected_command_builds_for_changed_generators_only_when_asked()
    {
        Assert.True(CliOptions.TryParse(["affected"], "/repo", out var plain, out _));
        Assert.True(CliOptions.TryParse(["affected", "--build"], "/repo", out var building, out _));

        Assert.False(plain.Affected.Build);
        Assert.True(building.Affected.Build);
        Assert.False(CliOptions.TryParse(["record", "--build"], "/repo", out _, out _));
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
    public void Record_command_reads_incremental_and_its_thresholds()
    {
        var ok = CliOptions.TryParse(
            ["record", "--incremental", "--max-map-age", "10", "--full-record-every", "5", "--full-record-days", "3", "--full-record-share", "40"],
            "/repo", out var options, out var error);

        Assert.True(ok, error);
        Assert.True(options.Record.Incremental);
        Assert.Equal(10, options.Analysis.MaxMapAge);
        Assert.Equal(new IncrementalThresholds { FullEvery = 5, FullDays = 3, FullShare = 40 }, options.Record.Thresholds);
    }

    [Theory]
    [InlineData("--full-record-share", "101")]
    [InlineData("--full-record-every", "-1")]
    [InlineData("--full-record-days", "soon")]
    public void Record_command_rejects_a_threshold_out_of_range(string option, string value)
    {
        Assert.False(CliOptions.TryParse(["record", "--incremental", option, value], "/repo", out _, out var error));
        Assert.Contains(option, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Incremental_belongs_to_record_only()
    {
        Assert.False(CliOptions.TryParse(["test", "--incremental"], "/repo", out _, out _));
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

    [Fact]
    public void Record_takes_the_teams_dotnet_test_arguments_after_a_double_dash()
    {
        Assert.True(CliOptions.TryParse(["record", "--parallel", "2", "--", "--filter", "Category!=Integration"], "/repo", out var options, out var error), error);

        Assert.Equal(["--filter", "Category!=Integration"], options.Record.DotnetTestArguments);
        Assert.Equal(2, options.Record.Parallelism);
        Assert.False(CliOptions.TryParse(["affected", "--", "--filter", "x"], "/repo", out _, out _));
    }

    [Fact]
    public void Non_code_files_default_to_readers_and_the_developer_can_choose_the_project_rule()
    {
        Assert.True(CliOptions.TryParse(["test"], "/repo", out var defaults, out _));
        Assert.Equal(TestDetta.Core.NonCodeFileRule.Readers, defaults.Analysis.NonCodeFiles);

        foreach (var command in (string[])["affected", "test", "record", "audit"])
        {
            Assert.True(CliOptions.TryParse([command, "--non-code-files", "project"], "/repo", out var chosen, out var error), error);
            Assert.Equal(TestDetta.Core.NonCodeFileRule.Project, chosen.Analysis.NonCodeFiles);
        }

        Assert.False(CliOptions.TryParse(["test", "--non-code-files", "none"], "/repo", out _, out var wrong));
        Assert.Contains("readers or project", wrong, StringComparison.Ordinal);
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
