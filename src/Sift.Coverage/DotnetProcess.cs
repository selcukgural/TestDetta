using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sift.Coverage;

/// <summary>Runs the <c>dotnet</c> executable for builds, property queries and test runs.</summary>
/// <param name="logger">Receives every command line and exit code.</param>
internal sealed partial class DotnetProcess(ILogger logger)
{
    /// <summary>
    /// How long the output of an exited process is still read. Its end normally arrives at once; a
    /// child left behind (an MSBuild node or compiler server kept for reuse, a background process of a
    /// test) may hold it open for minutes or for ever, and waiting for it hung a run at 0 % CPU.
    /// </summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(10);

    /// <summary>A wait that does not include the end of redirected output, unlike an infinite one.</summary>
    private static readonly TimeSpan Forever = TimeSpan.FromMilliseconds(int.MaxValue);

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
        var reader = new LineReader(process, output.WriteLine);
        if (!process.WaitForExit(timeout ?? Forever))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(Forever);
            Drain(reader);
            LogTimedOut((timeout ?? Forever).TotalSeconds);
            return TimedOut;
        }

        Drain(reader);
        LogFinished(process.ExitCode);
        return process.ExitCode;
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
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var reader = new LineReader(process, line => stdout.AppendLine(line), line => stderr.AppendLine(line));
        process.WaitForExit(Forever);
        Drain(reader);
        LogFinished(process.ExitCode);

        string text;
        string errors;
        lock (reader.Gate)
        {
            text = stdout.ToString();
            errors = stderr.ToString();
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} failed: {errors.Trim()} {text.Trim()}");
        }

        return text;
    }

    /// <summary>Waits a short while for the end of an exited process's output, then stops reading it.</summary>
    /// <param name="reader">The process's reader.</param>
    private void Drain(LineReader reader)
    {
        if (!reader.WaitForEnd(OutputGrace))
        {
            LogOutputLeftOpen(OutputGrace.TotalSeconds);
        }

        reader.Close();
    }

    /// <summary>
    /// Reads a process's stdout and stderr line by line into callbacks, one line at a time, until both
    /// end or <see cref="Close"/> is called; lines that arrive after that are dropped.
    /// </summary>
    private sealed class LineReader
    {
        /// <summary>Set when stdout ends.</summary>
        private readonly TaskCompletionSource _outputEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set when stderr ends.</summary>
        private readonly TaskCompletionSource _errorEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True once the caller stopped reading.</summary>
        private bool _closed;

        /// <summary>Starts reading.</summary>
        /// <param name="process">Started process with redirected output.</param>
        /// <param name="onOutput">Receives each stdout line.</param>
        /// <param name="onError">Receives each stderr line; null sends them to <paramref name="onOutput"/>.</param>
        public LineReader(Process process, Action<string> onOutput, Action<string>? onError = null)
        {
            process.OutputDataReceived += (_, e) => Receive(e.Data, onOutput, _outputEnded);
            process.ErrorDataReceived += (_, e) => Receive(e.Data, onError ?? onOutput, _errorEnded);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        /// <summary>Serializes the callbacks and the reading of what they collected.</summary>
        public Lock Gate { get; } = new();

        /// <summary>True when both streams ended within <paramref name="limit"/>.</summary>
        /// <param name="limit">Longest wait.</param>
        public bool WaitForEnd(TimeSpan limit) => Task.WaitAll([_outputEnded.Task, _errorEnded.Task], limit);

        /// <summary>Stops passing lines on.</summary>
        public void Close()
        {
            lock (Gate)
            {
                _closed = true;
            }
        }

        /// <summary>Passes a line on, or records the end of its stream (a null line).</summary>
        /// <param name="line">The line, or null at the end.</param>
        /// <param name="target">Receives the line.</param>
        /// <param name="ended">Set at the end of the stream.</param>
        private void Receive(string? line, Action<string> target, TaskCompletionSource ended)
        {
            if (line is null)
            {
                ended.TrySetResult();
                return;
            }

            lock (Gate)
            {
                if (!_closed)
                {
                    target(line);
                }
            }
        }
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "dotnet exited but its output stayed open for {Seconds:0} s (a child process kept it); reading stopped")]
    private partial void LogOutputLeftOpen(double seconds);
}
