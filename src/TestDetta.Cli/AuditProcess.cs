using System.Diagnostics;

namespace TestDetta.Cli;

/// <summary>
/// Starts the <c>dotnet</c> processes of <c>td audit</c> (builds, test runs, MSBuild queries in the working copy) and
/// ends them: at their time limit, or at once when the audit is cancelled (Ctrl+C, SIGTERM), always with their
/// whole process tree, so no test host keeps running against a copy that is about to be deleted.
/// </summary>
internal static class AuditProcess
{
    /// <summary>What <see cref="Run"/> returns for a process killed at its time limit; no real exit code is negative on Unix.</summary>
    public const int TimedOut = -1;

    /// <summary>What <see cref="Run"/> returns for a process killed because the audit was cancelled.</summary>
    public const int Cancelled = -2;

    /// <summary>
    /// How long the output of an exited process is still read. A child it left behind (a compiler server, a node kept
    /// for reuse) may hold the pipes open for good; the variables below keep most of them from staying.
    /// </summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Variables of every process besides the unattended ones (<see cref="TestDetta.Coverage.UnattendedRun"/>): no
    /// MSBuild node, build server or compiler server outlives its command, since each would hold files of a copy
    /// that is deleted at the end.
    /// </summary>
    private static readonly Dictionary<string, string> Quiet = new(StringComparer.Ordinal)
    {
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["UseSharedCompilation"] = "false",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",

        // A copy of a linked worktree has a .git file that points into the team's git directory: git run by a test in
        // the copy must not refresh that worktree's index on the side.
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    /// <summary>
    /// Variables set last, over the worker's and the inherited ones: the SDK and VSTest write their messages in
    /// English, whatever the runner's language, so the lines that say a test host crashed can be recognized
    /// (<see cref="AuditTestRun"/> reads them next to the report's own outcome and the exit code).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["VSLANG"] = "1033",
    };

    /// <summary>Runs dotnet and writes its output to <paramref name="log"/>.</summary>
    /// <param name="arguments">Arguments of the dotnet executable.</param>
    /// <param name="directory">Directory it runs in.</param>
    /// <param name="environment">Extra variables, such as a worker's own temporary directory.</param>
    /// <param name="log">Receives stdout and stderr, line by line.</param>
    /// <param name="limit">Longest it may run; null for no limit.</param>
    /// <param name="cancel">Kills it when cancelled.</param>
    /// <returns>The exit code, <see cref="TimedOut"/> or <see cref="Cancelled"/>.</returns>
    /// <exception cref="InvalidOperationException">dotnet could not be started.</exception>
    /// <exception cref="OperationCanceledException">The audit was cancelled before it started.</exception>
    public static int Run(IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string> environment, TextWriter log, TimeSpan? limit, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(Dotnet()) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in TestDetta.Coverage.UnattendedRun.Variables.Concat(Quiet).Concat(environment).Concat(English))
        {
            psi.Environment[name] = value;
        }

        using var process = new Process { StartInfo = psi };
        var gate = new Lock();
        CountdownEvent? ended = new(2);
        process.OutputDataReceived += (_, e) => Receive(e.Data);
        process.ErrorDataReceived += (_, e) => Receive(e.Data);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start dotnet.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // dotnet is not on PATH: an error the audit reports, not one that ends it without a report.
            throw new InvalidOperationException($"Could not start {psi.FileName}: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        int result;
        using (cancel.Register(() => Kill(process)))
        {
            var exited = process.WaitForExit(limit ?? Timeout.InfiniteTimeSpan);
            if (!exited)
            {
                Kill(process);
                process.WaitForExit();
            }

            result = cancel.IsCancellationRequested ? Cancelled : exited ? process.ExitCode : TimedOut;
        }

        ended.Wait(OutputGrace, CancellationToken.None);
        lock (gate)
        {
            ended.Dispose();
            ended = null;
        }

        return result;

        void Receive(string? line)
        {
            lock (gate)
            {
                if (ended is null)
                {
                    return;
                }

                if (line is null)
                {
                    ended.Signal();
                    return;
                }

                log.WriteLine(line);
            }
        }
    }

    /// <summary>Runs dotnet and returns its stdout; throws when it fails, times out or is cancelled.</summary>
    /// <param name="arguments">Arguments of the dotnet executable.</param>
    /// <param name="directory">Directory it runs in.</param>
    /// <param name="cancel">Kills it when cancelled.</param>
    /// <exception cref="InvalidOperationException">dotnet failed.</exception>
    /// <exception cref="OperationCanceledException">The audit was cancelled.</exception>
    public static string Capture(IReadOnlyList<string> arguments, string directory, CancellationToken cancel)
    {
        using var output = new StringWriter();
        var exitCode = Run(arguments, directory, new Dictionary<string, string>(), output, TimeSpan.FromMinutes(10), cancel);
        cancel.ThrowIfCancellationRequested();
        return exitCode == 0
            ? output.ToString()
            : throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} failed ({exitCode}): {output.ToString().Trim()}");
    }

    /// <summary>Kills a process and its children; one that already exited is left alone.</summary>
    /// <param name="process">The process.</param>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited in the meantime.
        }
    }

    /// <summary>
    /// The dotnet host that started this tool when it runs as a .NET tool (<c>DOTNET_HOST_PATH</c>), so the tests use
    /// the same SDK installation; otherwise <c>dotnet</c> from PATH.
    /// </summary>
    private static string Dotnet() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
}
