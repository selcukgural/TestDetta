using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Coverage;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A process TestDetta starts may leave a child behind that keeps its output open: an MSBuild node or a
/// compiler server kept for reuse. Waiting for the output to close then never ends (a test of TestDetta
/// hung for ten minutes at 0 % CPU). Once the process itself has exited, its output is drained for a
/// short while only. The fake dotnet starts a long-running child sharing its output and exits: a shell script with
/// <c>sleep</c>, or on Windows a batch file with <c>ping</c> (<c>timeout</c> refuses redirected input), started in
/// another folder: Windows does not delete a folder a running process has as its working directory.
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

    /// <summary>Runs <paramref name="action"/> with dotnet replaced by the script; fails if it takes a minute.</summary>
    private async Task<T> WithFakeDotnet<T>(Func<T> action)
    {
        Directory.CreateDirectory(_directory);
        var script = Path.Combine(_directory, OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet");
        await File.WriteAllTextAsync(script, OperatingSystem.IsWindows()
            ? "@echo off\r\nstart \"\" /b /d \"%SystemRoot%\" ping -n 121 127.0.0.1\r\necho built\r\nexit /b 0\r\n"
            : "#!/bin/sh\nsleep 120 &\necho built\nexit 0\n");
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
            Paths.DeleteTree(_directory);
        }
    }
}
