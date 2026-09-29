using Microsoft.Extensions.Logging;
using Sift.Cli;

namespace Sift.Tests;

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

    [Theory]
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
