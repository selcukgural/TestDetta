using Microsoft.Extensions.Logging;
using Sift.Cli;
using Sift.Core;

namespace Sift.Eval;

/// <summary>Entry point of <c>sift-eval</c>: clones the repository, evaluates its latest merges and, if asked, synthetic changes.</summary>
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

        if (BatchOptions.IsBatch(args))
        {
            if (!BatchOptions.TryParse(args, out var batch, out var batchError))
            {
                Console.Error.WriteLine($"{batchError}\n\n{EvalOptions.Usage}");
                return 2;
            }

            return BatchRunner.Run(batch);
        }

        if (!EvalOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"{error}\n\n{EvalOptions.Usage}");
            return 2;
        }

        return RunOne(options, Console.Out);
    }

    /// <summary>Evaluates one repository; returns 0 when nothing was missed, 3 when a test class was.</summary>
    /// <param name="options">What to evaluate and where to write.</param>
    /// <param name="output">Receives the Markdown summaries.</param>
    public static int RunOne(EvalOptions options, TextWriter output)
    {
        Directory.CreateDirectory(options.Output);
        using var loggerFactory = new StderrLoggerFactory(Console.Error, options.LogLevel);
        var logger = loggerFactory.CreateLogger(typeof(EvalApp).FullName!);
        using var toolchain = EnterToolchain(options.Run, loggerFactory);
        using var environment = new EnvironmentScope(options.Run.Environment);

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

        var passed = true;
        if (options.Target.Merges > 0)
        {
            var results = new List<MergeResult>();
            foreach (var commit in git.History(options.Target.Branch, options.Target.Merges))
            {
                LogEvaluating(logger, commit.Sha[..7], commit.Subject);
                results.Add(evaluator.Evaluate(commit));

                // Written after every merge, so a long evaluation can be read while it runs.
                EvalReport.Write(options.Output, options.Repository, results);
            }

            output.Write(EvalReport.Markdown(options.Repository, results));
            output.WriteLine($"\nReports: {Path.Combine(options.Output, "results.md")}");
            passed = results.All(static r => r.Safety?.Passed ?? true);
        }

        if (options.Target.Synthetic > 0)
        {
            var head = git.Head(options.Target.Branch);
            LogSynthetic(logger, options.Target.Synthetic, head[..7]);
            var run = new SyntheticEvaluator(workspace, options.Run).Evaluate(
                head,
                new SampleRequest(options.Target.Synthetic, options.Target.Seed) { Kinds = options.Run.MutantKinds },
                progress => SyntheticReport.Write(options.Output, options.Repository, progress));
            SyntheticReport.Write(options.Output, options.Repository, run);

            output.Write(SyntheticReport.Markdown(options.Repository, run).Split("\n## Mutants", 2)[0]);
            output.WriteLine($"\nReports: {Path.Combine(options.Output, "synthetic.md")}");
            passed &= run.Error is null && run.Mutants.All(static m => m.MissedClasses.Count == 0);
        }

        return passed ? 0 : 3;
    }

    /// <summary>
    /// Installs the pinned toolchain if needed and points this process's environment at it until the
    /// result is disposed; null when no toolchain is pinned and the <c>dotnet</c> on PATH is used.
    /// </summary>
    /// <param name="run">Evaluation settings.</param>
    /// <param name="loggerFactory">Creates the installer's logger.</param>
    /// <exception cref="InvalidOperationException">The toolchain cannot be installed here or its archives do not match the lock.</exception>
    private static ToolchainScope? EnterToolchain(RunSettings run, ILoggerFactory loggerFactory)
    {
        if (run.Toolchain is not { } toolchain)
        {
            return null;
        }

        if (OperatingSystem.IsWindows() || run.ToolchainRoot is not { } root)
        {
            throw new InvalidOperationException("A pinned toolchain needs a toolchain directory, on macOS or Linux.");
        }

        var locked = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        var lockFile = new ToolchainLock(run.ToolchainLock ?? Path.Combine(root, "toolchains.lock.json"), locked);
        using var source = new MicrosoftReleaseSource();
        var logger = loggerFactory.CreateLogger<ToolchainInstaller>();
        var directory = new ToolchainInstaller(root, lockFile, source, logger).Install(toolchain);
        LogToolchain(logger, toolchain.Description, directory);
        return new ToolchainScope(directory, toolchain.EffectiveArchitecture);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Using {Toolchain} from {Directory}")]
    private static partial void LogToolchain(ILogger logger, string toolchain, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Changing {Count} sampled member(s) at {Sha}")]
    private static partial void LogSynthetic(ILogger logger, int count, string sha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluating {Sha} {Subject}")]
    private static partial void LogEvaluating(ILogger logger, string sha, string subject);
}
