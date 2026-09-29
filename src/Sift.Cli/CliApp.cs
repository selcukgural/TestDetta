using Microsoft.Extensions.Logging;
using Sift.Core;

namespace Sift.Cli;

/// <summary>Entry point of the <c>sift</c> command: parses arguments, runs the analysis, prints the result.</summary>
internal static partial class CliApp
{
    /// <summary>Exit code for success.</summary>
    private const int Success = 0;

    /// <summary>Exit code when git, the file system or a project file fails.</summary>
    private const int Failure = 1;

    /// <summary>Exit code for an invalid command line.</summary>
    private const int UsageError = 2;

    /// <summary>Runs the command and returns the process exit code.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="stdout">Receives the result.</param>
    /// <param name="stderr">Receives usage help, errors and diagnostic logs.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is [] or ["-h" or "--help", ..])
        {
            stderr.WriteLine(CliOptions.Usage);
            return args.Length == 0 ? UsageError : Success;
        }

        if (args[0] != "affected")
        {
            stderr.WriteLine($"Unknown command: {args[0]}\n\n{CliOptions.Usage}");
            return UsageError;
        }

        if (!CliOptions.TryParse(args.AsSpan(1), Directory.GetCurrentDirectory(), out var options, out var error))
        {
            stderr.WriteLine($"{error}\n\n{CliOptions.Usage}");
            return UsageError;
        }

        using var loggerFactory = new StderrLoggerFactory(stderr, options.LogLevel);
        var logger = loggerFactory.CreateLogger(typeof(CliApp).FullName!);
        LogOptions(logger, options);

        try
        {
            var analysis = new AnalysisPipeline(loggerFactory).Run(options.RepoPath, options.BaseRef);
            var stats = new RunStats(analysis.ChangedFiles.Count, analysis.Graph.Projects.Count);
            ResultWriter.Write(new ResultReport(options, analysis.RepoRoot, stats, analysis.Impact, analysis.Tests), stdout);
            return Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
                                       or UnauthorizedAccessException)
        {
            LogFailed(logger, ex);
            stderr.WriteLine($"sift: {ex.Message}");
            return Failure;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Options: {Options}")]
    private static partial void LogOptions(ILogger logger, CliOptions options);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analysis failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
