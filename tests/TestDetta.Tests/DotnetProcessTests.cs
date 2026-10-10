using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A process TestDetta starts may leave a child behind that keeps its output open: an MSBuild node or a
/// compiler server kept for reuse. Waiting for the output to close then never ends (a test of TestDetta
/// hung for ten minutes at 0 % CPU). Once the process itself has exited, its output is drained for a
/// short while only. The fake dotnet is a shell script that starts a long sleep sharing its output and
/// exits, so the tests need a Unix shell.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class DotnetProcessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-dotnet-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Run_returns_when_dotnet_exits_although_a_child_keeps_its_output_open()
    {
        var output = new StringWriter();
        var exitCode = await WithFakeDotnet(() => new DotnetProcess(NullLogger.Instance).Run(["build"], _directory, new Dictionary<string, string>(), output));

        Assert.Equal(0, exitCode);
        Assert.Contains("built", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_returns_when_dotnet_exits_although_a_child_keeps_its_output_open()
    {
        var text = await WithFakeDotnet(() => new DotnetProcess(NullLogger.Instance).Capture(["msbuild"], _directory));

        Assert.Contains("built", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Processes_TestDetta_starts_are_not_handed_the_license()
    {
        // A team's tests and its packages' build steps run in these processes; the license is TestDetta's alone.
        string[] names = ["TESTDETTA_LICENSE", "TESTDETTA_LICENSE_FILE", "TESTDETTA_LICENSE_SCOPE"];
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable("TESTDETTA_LICENSE", "testdetta1.secret");
            Environment.SetEnvironmentVariable("TESTDETTA_LICENSE_FILE", "/secret/license");
            Environment.SetEnvironmentVariable("TESTDETTA_LICENSE_SCOPE", "github.com/team/repo");

            var environment = new DotnetProcess(NullLogger.Instance, "/usr/bin/env").Capture([], Path.GetTempPath());

            Assert.DoesNotContain("testdetta1.secret", environment, StringComparison.Ordinal);
            Assert.DoesNotContain("/secret/license", environment, StringComparison.Ordinal);
            Assert.Contains("TESTDETTA_LICENSE_SCOPE=github.com/team/repo", environment, StringComparison.Ordinal);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++)
            {
                Environment.SetEnvironmentVariable(names[i], previous[i]);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> with dotnet replaced by the script; fails if it takes a minute.</summary>
    private async Task<T> WithFakeDotnet<T>(Func<T> action)
    {
        Directory.CreateDirectory(_directory);
        var script = Path.Combine(_directory, "dotnet");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 120 &\necho built\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var previous = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", script);
        try
        {
            return await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previous);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
