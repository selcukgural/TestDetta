using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sift.Coverage;

/// <summary>Runs the <c>dotnet</c> executable for builds, property queries and test runs.</summary>
/// <param name="logger">Receives every command line and exit code.</param>
internal sealed partial class DotnetProcess(ILogger logger)
{
    /// <summary>Runs dotnet and forwards its output line by line.</summary>
    /// <param name="arguments">Arguments for the dotnet executable.</param>
    /// <param name="workingDirectory">Directory the process runs in.</param>
    /// <param name="environment">Extra environment variables for the process.</param>
    /// <param name="output">Receives stdout and stderr of the process.</param>
    /// <param name="timeout">
    /// Longest the process may run; it is then killed with its children (a test host that hangs,
    /// say) and <see cref="TimedOut"/> is returned. Null waits forever.
    /// </param>
    /// <returns>The exit code, or <see cref="TimedOut"/>.</returns>
    public int Run(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TextWriter output,
        TimeSpan? timeout = null)
    {
        using var process = Start(arguments, workingDirectory, environment);
        var gate = new Lock();
        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (timeout is { } limit && !process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            LogTimedOut(limit.TotalSeconds);
            return TimedOut;
        }

        process.WaitForExit();
        LogFinished(process.ExitCode);
        return process.ExitCode;

        void Forward(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                output.WriteLine(line);
            }
        }
    }

    /// <summary>What <see cref="Run"/> returns for a process it killed after its timeout; no real exit code is negative on Unix.</summary>
    public const int TimedOut = -1;

    /// <summary>Runs dotnet and returns its stdout.</summary>
    /// <param name="arguments">Arguments for the dotnet executable.</param>
    /// <param name="workingDirectory">Directory the process runs in.</param>
    /// <exception cref="InvalidOperationException">The process exited with a non-zero code.</exception>
    public string Capture(IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = Start(arguments, workingDirectory, new Dictionary<string, string>());
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        LogFinished(process.ExitCode);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', arguments)} failed: {stderrTask.GetAwaiter().GetResult().Trim()} {stdout.Trim()}");
        }

        return stdout;
    }

    /// <summary>Starts dotnet with redirected output.</summary>
    /// <param name="arguments">Arguments for the dotnet executable.</param>
    /// <param name="workingDirectory">Directory the process runs in.</param>
    /// <param name="environment">Extra environment variables.</param>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    private Process Start(IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo(Executable())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            psi.Environment[key] = value;
        }

        // Built once per process, which is cheap next to starting dotnet.
        var commandLine = string.Join(' ', arguments);
        LogStarting(commandLine);
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
    }

    /// <summary>
    /// The dotnet host that started this tool when it runs as a .NET tool (<c>DOTNET_HOST_PATH</c>),
    /// so builds and tests use the same SDK installation; otherwise <c>dotnet</c> from PATH.
    /// </summary>
    private static string Executable() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running dotnet {Arguments}")]
    private partial void LogStarting(string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "dotnet exited with {ExitCode}")]
    private partial void LogFinished(int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dotnet did not finish within {Seconds:0} s and was killed")]
    private partial void LogTimedOut(double seconds);
}
