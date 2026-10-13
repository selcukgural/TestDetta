using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>Processes with temporary directories of their own keep the testing platform's sockets short.</summary>
public sealed class TestingPlatformPipesTests
{
    [Fact]
    public void The_pipe_folder_is_the_system_temporary_directory_and_a_given_one_is_kept()
    {
        var expected = OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(TestingPlatformPipes.Variable) is { Length: > 0 }
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var shard = new Dictionary<string, string> { ["TMPDIR"] = Path.Combine(Path.GetTempPath(), "td1234ab") + Path.DirectorySeparatorChar };

        TestingPlatformPipes.KeepShared(shard);

        Assert.Equal(expected, shard.GetValueOrDefault(TestingPlatformPipes.Variable));

        var named = new Dictionary<string, string> { [TestingPlatformPipes.Variable] = "/team/pipes" };
        TestingPlatformPipes.KeepShared(named);
        Assert.Equal("/team/pipes", named[TestingPlatformPipes.Variable]);
    }
}
