using Microsoft.Extensions.Logging;
using Sift.Cli;
using Sift.Core;

namespace Sift.Eval;

/// <summary>Entry point of <c>sift-eval</c>: clones the repository and evaluates its latest merges.</summary>
internal static partial class EvalApp
{
    /// <summary>Runs the evaluation and returns the exit code: 0 when nothing was missed, 3 when a test class was, 2 for usage errors.</summary>
    /// <param name="args">Raw command line.</param>
    public static int Run(string[] args)
    {
        if (args is ["-h" or "--help", ..])
        {
            Console.Error.WriteLine(EvalOptions.Usage);
            return 0;
        }

        if (!EvalOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"{error}\n\n{EvalOptions.Usage}");
            return 2;
        }

        Directory.CreateDirectory(options.Output);
        using var loggerFactory = new StderrLoggerFactory(Console.Error, options.LogLevel);
        var logger = loggerFactory.CreateLogger(typeof(EvalApp).FullName!);

        // An existing clone is reused, so an interrupted evaluation can continue with its cached maps.
        var clone = Path.Combine(options.Output, "repo");
        if (!Directory.Exists(clone))
        {
            EvalGit.Clone(options.Repository, clone);
        }

        // The root as git reports it, which is what Sift uses: on macOS the temp folder is reached
        // through a symlink, and the two spellings of one path would never match.
        var root = new GitClient(loggerFactory.CreateLogger<GitClient>()).TopLevel(clone);
        var git = new EvalGit(root);
        var logs = Path.Combine(options.Output, "logs");
        Directory.CreateDirectory(logs);

        var executor = new TestExecutor(
            new ExecutionContext(root, DotnetTestModeDetector.Detect(root), options.Run, logs),
            loggerFactory.CreateLogger<TestExecutor>());
        var workspace = new EvalWorkspace(git, root, Path.Combine(options.Output, "maps"), executor, loggerFactory);
        var evaluator = new MergeEvaluator(workspace, options.Run);

        var results = new List<MergeResult>();
        foreach (var commit in git.History(options.Target.Branch, options.Target.Merges))
        {
            LogEvaluating(logger, commit.Sha[..7], commit.Subject);
            results.Add(evaluator.Evaluate(commit));

            // Written after every merge, so a long evaluation can be read while it runs.
            EvalReport.Write(options.Output, options.Repository, results);
        }

        Console.Out.Write(EvalReport.Markdown(options.Repository, results));
        Console.Out.WriteLine($"\nReports: {Path.Combine(options.Output, "results.md")}");
        return results.All(static r => r.Safety?.Passed ?? true) ? 0 : 3;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluating {Sha} {Subject}")]
    private static partial void LogEvaluating(ILogger logger, string sha, string subject);
}
