using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TestDetta.Coverage;

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
        TimeSpan? timeout = null) =>
        Run(arguments, workingDirectory, environment, output, new RunLimits(timeout));

    /// <summary>Runs dotnet within <paramref name="limits"/> and forwards its output line by line.</summary>
    /// <param name="arguments">Arguments for the dotnet executable.</param>
    /// <param name="workingDirectory">Directory the process runs in.</param>
    /// <param name="environment">Extra environment variables for the process.</param>
    /// <param name="output">Receives stdout and stderr of the process.</param>
    /// <param name="limits">How long it may run, how long its process tree may sit idle, and how much memory it may hold.</param>
    /// <returns>The exit code, or <see cref="TimedOut"/> when it was killed for any limit.</returns>
    public int Run(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TextWriter output,
        RunLimits limits)
    {
        using var process = Start(arguments, workingDirectory, environment);
        var reader = new LineReader(process, output.WriteLine);
        if (!WaitWithin(process, limits))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(Forever);
            Drain(reader);
            return TimedOut;
        }

        Drain(reader);
        LogFinished(process.ExitCode);
        return process.ExitCode;
    }

    /// <summary>
    /// Waits for <paramref name="process"/> to exit; false when it ran past the timeout, when its whole
    /// process tree used almost no CPU for the idle limit (a deadlock: waiting longer changes nothing),
    /// or when the tree crossed the memory limit (an allocation without end, which would take the
    /// machine down with it).
    /// </summary>
    /// <param name="process">Started process.</param>
    /// <param name="limits">Timeout, idle and memory limits.</param>
    internal bool WaitWithin(Process process, RunLimits limits)
    {
        var deadline = limits.Timeout is { } timeout ? Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;
        if (limits.Idle is null && limits.Memory is null || !ProcessTreeWatch.IsSupported)
        {
            if (process.WaitForExit(limits.Timeout ?? Forever))
            {
                return true;
            }

            LogTimedOut((limits.Timeout ?? Forever).TotalSeconds);
            return false;
        }

        // Idleness needs minutes to show, memory can fill in seconds.
        var sample = limits.Idle is { } idle
            ? TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond / 4, Math.Min(idle.Ticks / 10, TimeSpan.FromSeconds(30).Ticks)))
            : TimeSpan.FromSeconds(30);
        if (limits.Memory is not null && sample > MemorySample)
        {
            sample = MemorySample;
        }

        var started = Stopwatch.GetTimestamp();
        var detector = limits.Idle is { } window ? new IdleDetector(window) : null;
        if (detector is not null && ProcessTreeWatch.Measure(process.Id) is { } initial)
        {
            detector.Observe(TimeSpan.Zero, initial.CpuSeconds);
        }

        while (!process.WaitForExit(sample))
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= deadline)
            {
                LogTimedOut(limits.Timeout!.Value.TotalSeconds);
                return false;
            }

            if (ProcessTreeWatch.Measure(process.Id) is not { } usage)
            {
                continue;
            }

            if (limits.Memory is { } memory && OverMemory(memory, usage.ResidentBytes))
            {
                return false;
            }

            if (detector is not null && detector.Observe(Stopwatch.GetElapsedTime(started, now), usage.CpuSeconds))
            {
                LogIdle(limits.Idle!.Value.TotalSeconds, IdleDetector.IdleCpuShare * 100);
                return false;
            }
        }

        return true;
    }

    /// <summary>How often a tree with a memory limit is measured: fast enough to stop one that allocates gigabytes a second.</summary>
    private static readonly TimeSpan MemorySample = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True (and logged) when a tree holding <paramref name="treeBytes"/> must be killed under
    /// <paramref name="memory"/>. The machine's free memory is only read for a tree large enough to
    /// matter, which keeps the check to one <c>ps</c> call for most samples.
    /// </summary>
    /// <param name="memory">The limit.</param>
    /// <param name="treeBytes">Memory the tree holds.</param>
    private bool OverMemory(MemoryLimit memory, long treeBytes)
    {
        if (memory.Exceeds(treeBytes))
        {
            LogOverMemory(treeBytes / GigaByte, memory.TreeBytes / GigaByte);
            return true;
        }

        if (!memory.CountsUnderPressure(treeBytes) || ProcessTreeWatch.FreeMemoryPercent() is not { } free || !memory.UnderPressure(free))
        {
            return false;
        }

        LogMemoryPressure(treeBytes / GigaByte, free);
        return true;
    }

    /// <summary>Bytes in a gigabyte, for log messages.</summary>
    private const double GigaByte = 1024d * 1024 * 1024;

    /// <summary>What <see cref="Run(IReadOnlyList{string}, string, IReadOnlyDictionary{string, string}, TextWriter, TimeSpan?)"/> returns for a process it killed after its timeout or idle limit; no real exit code is negative on Unix.</summary>
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "dotnet and its children used less than {Percent:0.#} % of a core for {Seconds:0} s and were killed as stuck")]
    private partial void LogIdle(double seconds, double percent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dotnet and its children held {Gigabytes:0.0} GB, more than the limit of {Limit:0.0} GB, and were killed")]
    private partial void LogOverMemory(double gigabytes, double limit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dotnet and its children held {Gigabytes:0.0} GB while only {Free} % of the machine's memory was free, and were killed")]
    private partial void LogMemoryPressure(double gigabytes, int free);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dotnet did not finish within {Seconds:0} s and was killed")]
    private partial void LogTimedOut(double seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "dotnet exited but its output stayed open for {Seconds:0} s (a child process kept it); reading stopped")]
    private partial void LogOutputLeftOpen(double seconds);
}

/// <summary>Limits of one <see cref="DotnetProcess"/> run.</summary>
/// <param name="Timeout">Longest the process may run; null for no limit.</param>
/// <param name="Idle">
/// Longest its process tree may use almost no CPU (<see cref="IdleDetector.IdleCpuShare"/> of a core)
/// before it is killed as stuck; null to wait for the timeout only.
/// </param>
/// <param name="Memory">When its process tree is killed for the memory it holds; null for never.</param>
internal sealed record RunLimits(TimeSpan? Timeout, TimeSpan? Idle = null, MemoryLimit? Memory = null);
