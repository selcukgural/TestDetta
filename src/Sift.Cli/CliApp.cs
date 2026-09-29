using Microsoft.Extensions.Logging;
using Sift.Core;

namespace Sift.Cli;

/// <summary>Entry point of the <c>sift</c> command: parses arguments, runs the analysis, then prints or runs the result.</summary>
internal static partial class CliApp
{
    /// <summary>Exit code for success, including "no tests needed".</summary>
    private const int Success = 0;

    /// <summary>Exit code when git, the file system or a project file fails.</summary>
    private const int Failure = 1;

    /// <summary>Exit code for an invalid command line.</summary>
    private const int UsageError = 2;

    /// <summary>Exit code of <c>sift test</c> when at least one <c>dotnet test</c> failed.</summary>
    private const int TestsFailed = 3;

    /// <summary>Runs the command and returns the process exit code.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="stdout">Receives the result.</param>
    /// <param name="stderr">Receives usage help, errors and diagnostic logs.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is ["-h" or "--help", ..])
        {
            stderr.WriteLine(CliOptions.Usage);
            return Success;
        }

        if (!CliOptions.TryParse(args, Directory.GetCurrentDirectory(), out var options, out var error))
        {
            stderr.WriteLine($"{error}\n\n{CliOptions.Usage}");
            return UsageError;
        }

        using var loggerFactory = new StderrLoggerFactory(stderr, options.Analysis.LogLevel);
        var logger = loggerFactory.CreateLogger(typeof(CliApp).FullName!);
        LogOptions(logger, options);

        AnalysisResult analysis;
        try
        {
            analysis = new AnalysisPipeline(loggerFactory).Run(options.Analysis.RepoPath, options.Analysis.BaseRef);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
                                       or UnauthorizedAccessException)
        {
            LogFailed(logger, ex);
            stderr.WriteLine($"sift: {ex.Message}");
            return Failure;
        }

        if (options.Command == CliCommand.Test)
        {
            return RunTests(options, analysis, loggerFactory, stdout);
        }

        var stats = new RunStats(analysis.ChangedFiles.Count, analysis.Graph.Projects.Count);
        ResultWriter.Write(new ResultReport(options, analysis.RepoRoot, stats, analysis.Impact, analysis.Tests), stdout);
        return Success;
    }

    /// <summary>Carries out <c>sift test</c>: prints the plan, runs (or only prints) the invocations, prints a summary.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="loggerFactory">Creates the runner's logger.</param>
    /// <param name="stdout">Receives the plan and the summary.</param>
    private static int RunTests(CliOptions options, AnalysisResult analysis, ILoggerFactory loggerFactory, TextWriter stdout)
    {
        var invocations = TestInvocationBuilder.Build(analysis.Tests, analysis.RepoRoot, options.Test.DotnetTestArguments);
        ResultWriter.WritePlan(analysis, stdout);

        if (invocations.Count == 0)
        {
            stdout.WriteLine("No tests need to run for these changes.");
            return Success;
        }

        if (options.Test.DryRun)
        {
            foreach (var invocation in invocations)
            {
                stdout.WriteLine(ResultWriter.CommandLine(invocation));
            }

            return Success;
        }

        stdout.Flush();
        var results = new TestRunner(loggerFactory.CreateLogger<TestRunner>()).RunAll(invocations, analysis.RepoRoot, stdout);
        ResultWriter.WriteRunSummary(analysis.RepoRoot, analysis.Tests, results, stdout);
        return results.Any(static r => r.ExitCode != 0) ? TestsFailed : Success;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Options: {Options}")]
    private static partial void LogOptions(ILogger logger, CliOptions options);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analysis failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
