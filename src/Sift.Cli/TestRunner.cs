using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sift.Core;

namespace Sift.Cli;

/// <summary>Result of running one <see cref="TestInvocation"/>.</summary>
/// <param name="Invocation">What was run.</param>
/// <param name="ExitCode">Exit code of the final <c>dotnet test</c> run.</param>
/// <param name="Elapsed">Wall-clock duration, including a fallback run.</param>
/// <param name="RanUnfiltered">True when the filter matched no test and the project was run again without it.</param>
/// <param name="NotATestProject">
/// True when <c>dotnet test</c> found no test project to run (a project named like a test project
/// that is not one); the exit code is then reported as 0.
/// </param>
internal sealed record TestRunResult(TestInvocation Invocation, int ExitCode, TimeSpan Elapsed, bool RanUnfiltered, bool NotATestProject = false);

/// <summary>
/// Runs <c>dotnet test</c> invocations one after another, forwarding their output line by line.
/// A filter that matches no test is treated as a Sift mistake, never as a pass: the project is
/// run again without the filter.
/// </summary>
/// <param name="logger">Receives each command line, exit code and fallback.</param>
internal sealed partial class TestRunner(ILogger<TestRunner> logger)
{
    /// <summary>What VSTest prints when <c>--filter</c> matches nothing; it still exits with 0.</summary>
    private const string VsTestNoMatch = "No test matches the given testcase filter";

    /// <summary>What <c>dotnet test</c> prints, under Microsoft.Testing.Platform, for a project that is not a test project.</summary>
    private const string NoTestProjects = "No test projects were found";

    /// <summary>Exit code of Microsoft.Testing.Platform when zero tests ran.</summary>
    private const int TestingPlatformZeroTests = 8;

    /// <summary>Runs every invocation, even after a failure, so one run reports all broken projects.</summary>
    /// <param name="invocations">What to run.</param>
    /// <param name="workingDirectory">Repository root; the invocations use paths relative to it.</param>
    /// <param name="stdout">Receives the forwarded test output and fallback warnings.</param>
    public IReadOnlyList<TestRunResult> RunAll(IReadOnlyList<TestInvocation> invocations, string workingDirectory, TextWriter stdout)
    {
        var results = new List<TestRunResult>(invocations.Count);
        foreach (var invocation in invocations)
        {
            results.Add(RunWithFallback(invocation, workingDirectory, stdout));
        }

        return results;
    }

    /// <summary>Runs one invocation; when its filter matched no test, runs the project again unfiltered.</summary>
    /// <param name="invocation">What to run.</param>
    /// <param name="workingDirectory">Directory the process runs in.</param>
    /// <param name="stdout">Receives the forwarded output.</param>
    private TestRunResult RunWithFallback(TestInvocation invocation, string workingDirectory, TextWriter stdout)
    {
        var stopwatch = Stopwatch.StartNew();
        var (exitCode, matchedNothing, notATestProject) = Run(invocation.Arguments, workingDirectory, stdout);
        if (notATestProject)
        {
            LogNotATestProject(invocation.Selection.Project.Path);
            stdout.WriteLine("sift: dotnet test found no test project here; it is skipped.");
            return new TestRunResult(invocation, 0, stopwatch.Elapsed, RanUnfiltered: false, NotATestProject: true);
        }

        var filtered = invocation.Selection.Mode == TestRunMode.Filtered;
        if (!filtered || !(matchedNothing || exitCode == TestingPlatformZeroTests))
        {
            return new TestRunResult(invocation, exitCode, stopwatch.Elapsed, RanUnfiltered: false);
        }

        LogFilterMatchedNothing(invocation.Selection.Project.Path);
        stdout.WriteLine("sift: the filter matched no test, which should not happen; running the whole project instead.");
        stdout.WriteLine("sift: please report this with the output of 'sift affected --format json'.");

        var unfiltered = WithoutFilter(invocation.Arguments);
        (exitCode, _, _) = Run(unfiltered, workingDirectory, stdout);
        return new TestRunResult(invocation, exitCode, stopwatch.Elapsed, RanUnfiltered: true);
    }

    /// <summary>Runs dotnet once, forwarding stdout and stderr, and reports whether VSTest said the filter matched nothing.</summary>
    /// <param name="arguments">Arguments for the dotnet executable.</param>
    /// <param name="workingDirectory">Directory the process runs in.</param>
    /// <param name="stdout">Receives the forwarded output.</param>
    /// <exception cref="InvalidOperationException">The dotnet executable could not be started.</exception>
    private (int ExitCode, bool MatchedNothing, bool NotATestProject) Run(IReadOnlyList<string> arguments, string workingDirectory, TextWriter stdout)
    {
        var psi = new ProcessStartInfo(DotnetExecutable())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // Built once per run, which is cheap next to starting dotnet test.
        var commandLine = string.Join(' ', arguments);
        LogStarting(commandLine);
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        var matchedNothing = false;
        var notATestProject = false;
        var gate = new Lock();

        // Both streams go to our stdout, in arrival order, so the log reads like a normal dotnet test run.
        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start dotnet.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        LogFinished(commandLine, process.ExitCode, stopwatch.ElapsedMilliseconds);
        return (process.ExitCode, matchedNothing, notATestProject);

        void Forward(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                matchedNothing |= line.Contains(VsTestNoMatch, StringComparison.Ordinal);
                notATestProject |= line.Contains(NoTestProjects, StringComparison.Ordinal);
                stdout.WriteLine(line);
            }
        }
    }

    /// <summary>The same arguments without <c>--filter</c> and its value.</summary>
    /// <param name="arguments">Arguments of a filtered invocation.</param>
    private static List<string> WithoutFilter(IReadOnlyList<string> arguments)
    {
        var result = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "--filter")
            {
                i++;
                continue;
            }

            result.Add(arguments[i]);
        }

        return result;
    }

    /// <summary>
    /// The dotnet host that started this tool when it runs as a .NET tool (<c>DOTNET_HOST_PATH</c>),
    /// so the tests use the same SDK installation; otherwise <c>dotnet</c> from PATH.
    /// </summary>
    private static string DotnetExecutable() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running dotnet {Arguments}")]
    private partial void LogStarting(string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "dotnet {Arguments} exited with {ExitCode} after {ElapsedMs} ms")]
    private partial void LogFinished(string arguments, int exitCode, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} is not a test project according to dotnet test; skipped")]
    private partial void LogNotATestProject(string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The filter for {Project} matched no test; running it unfiltered")]
    private partial void LogFilterMatchedNothing(string project);
}
