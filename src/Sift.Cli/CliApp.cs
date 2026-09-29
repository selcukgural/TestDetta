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

        if (options.Command == CliCommand.Record)
        {
            return RecordCoverage(options, loggerFactory, logger, stdout, stderr);
        }

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
            AppendSummary(options, analysis, results: null);
            return Success;
        }

        if (options.Test.DryRun)
        {
            foreach (var invocation in invocations)
            {
                stdout.WriteLine(ResultWriter.CommandLine(invocation));
            }

            AppendSummary(options, analysis, results: null);
            return Success;
        }

        stdout.Flush();
        var results = new TestRunner(loggerFactory.CreateLogger<TestRunner>()).RunAll(invocations, analysis.RepoRoot, stdout);
        ResultWriter.WriteRunSummary(analysis.RepoRoot, analysis.Tests, results, stdout);
        AppendSummary(options, analysis, results);
        return results.Any(static r => r.ExitCode != 0) ? TestsFailed : Success;
    }

    /// <summary>Appends the Markdown summary when <c>--summary-file</c> was given.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="results">Test results, or null when nothing ran.</param>
    private static void AppendSummary(CliOptions options, AnalysisResult analysis, IReadOnlyList<TestRunResult>? results)
    {
        if (options.Test.SummaryFile is not { } path)
        {
            return;
        }

        using var writer = File.AppendText(path);
        MarkdownSummary.Write(new SummaryInput(analysis, options.Analysis.BaseRef, results), writer);
    }

    /// <summary>
    /// Carries out <c>sift record</c>: records one coverage map per test project. A project whose
    /// tests fail is still recorded, because failing tests ran the code they cover.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="loggerFactory">Creates the recorder's loggers.</param>
    /// <param name="logger">Logger of the CLI.</param>
    /// <param name="stdout">Receives build and test output and the summary.</param>
    /// <param name="stderr">Receives errors.</param>
    private static int RecordCoverage(
        CliOptions options,
        ILoggerFactory loggerFactory,
        ILogger logger,
        TextWriter stdout,
        TextWriter stderr)
    {
        try
        {
            var root = new GitClient(loggerFactory.CreateLogger<GitClient>()).TopLevel(options.Analysis.RepoPath);
            var graph = new ProjectGraphLoader(loggerFactory.CreateLogger<ProjectGraphLoader>()).Load(root);
            var testProjects = SelectTestProjects(graph, options.Record.Projects);
            if (testProjects.Count == 0)
            {
                stderr.WriteLine("sift: no test project to record.");
                return Failure;
            }

            var recorder = new Sift.Coverage.CoverageRecorder(loggerFactory);
            var allPassed = true;
            var summary = new List<string>();

            foreach (var project in testProjects)
            {
                var (map, passed) = recorder.Record(
                    new Sift.Coverage.RecordRequest(root, graph, project, options.Record.Configuration, options.Record.Parallelism), stdout);
                var path = CoverageMapStore.PathFor(root, project.Path);
                CoverageMapStore.Write(path, map);

                allPassed &= passed;
                summary.Add($"  {(passed ? "recorded" : "recorded, tests FAILED")}  {map.TestProject}: " +
                            $"{map.Classes.Count} test class(es), {map.Methods.Count} method(s) -> {Path.GetRelativePath(root, path)}");
            }

            stdout.WriteLine();
            stdout.WriteLine("sift record summary:");
            foreach (var line in summary)
            {
                stdout.WriteLine(line);
            }

            return allPassed ? Success : TestsFailed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            LogFailed(logger, ex);
            stderr.WriteLine($"sift: {ex.Message}");
            return Failure;
        }
    }

    /// <summary>All test projects, or only those named on the command line.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="projects">Paths given with <c>--project</c>; empty means all.</param>
    /// <exception cref="InvalidOperationException">A named project is not a test project of the repository.</exception>
    private static List<ProjectNode> SelectTestProjects(ProjectGraph graph, IReadOnlyList<string> projects)
    {
        var result = new List<ProjectNode>();
        if (projects.Count == 0)
        {
            foreach (var node in graph.Projects)
            {
                if (node.IsTestProject)
                {
                    result.Add(node);
                }
            }
        }

        foreach (var project in projects)
        {
            var wanted = Path.GetFullPath(project).Replace('\\', '/');
            var node = graph.Projects.FirstOrDefault(n => n.IsTestProject && string.Equals(n.Path, wanted, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException($"{project} is not a test project of this repository.");
            result.Add(node);
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Options: {Options}")]
    private static partial void LogOptions(ILogger logger, CliOptions options);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analysis failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
