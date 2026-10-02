using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Sift.Eval;

/// <summary>Parsed command line of a corpus run.</summary>
/// <param name="CorpusPath">Corpus JSON file.</param>
/// <param name="Only">Names of the repositories to evaluate; empty for all.</param>
/// <param name="Output">Directory with one subdirectory per repository and the summary.</param>
/// <param name="SummarizeOnly">True to rebuild the summary from existing results without evaluating.</param>
/// <param name="LogLevel">Minimum level of the log on stderr.</param>
internal sealed record BatchOptions(string CorpusPath, IReadOnlySet<string> Only, string Output, bool SummarizeOnly, LogLevel LogLevel)
{
    /// <summary>
    /// Settings that replace every repository's own for this run, such as a nightly smoke run with
    /// fewer mutants on a small machine; null keeps the corpus value.
    /// </summary>
    public BatchOverrides Overrides { get; init; } = new();

    /// <summary>True when the command line asks for a corpus run or a summary.</summary>
    /// <param name="args">Raw arguments.</param>
    public static bool IsBatch(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (arg is "--corpus" or "--summarize")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the command line of a corpus run.</summary>
    /// <param name="args">Raw arguments.</param>
    /// <param name="options">The options when parsing succeeds.</param>
    /// <param name="error">A message for the user when parsing fails.</param>
    public static bool TryParse(ReadOnlySpan<string> args, out BatchOptions options, out string? error)
    {
        string? corpus = null;
        string? output = null;
        var summarize = false;
        var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logLevel = LogLevel.Information;
        var overrides = new BatchOverrides();
        options = null!;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-v" or "--verbose")
            {
                logLevel = LogLevel.Debug;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"{arg} needs a value";
                return false;
            }

            var value = args[++i];
            switch (arg)
            {
                case "--corpus":
                    corpus = Path.GetFullPath(value);
                    break;

                case "--summarize":
                    summarize = true;
                    output = Path.GetFullPath(value);
                    break;

                case "--out":
                    output = Path.GetFullPath(value);
                    break;

                case "--only":
                    only.UnionWith(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--synthetic" or "--mutant-parallelism" or "--parallel":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var number) || number < 1)
                    {
                        error = $"{arg} needs a positive number, not {value}";
                        return false;
                    }

                    overrides = arg switch
                    {
                        "--synthetic" => overrides with { Synthetic = number },
                        "--mutant-parallelism" => overrides with { MutantParallelism = number },
                        _ => overrides with { Parallel = number },
                    };
                    break;

                default:
                    error = $"Unknown option for a corpus run: {arg}";
                    return false;
            }
        }

        if (corpus is null)
        {
            error = "--corpus is required";
            return false;
        }

        output ??= Path.Combine(Path.GetTempPath(), "sift-corpus-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        options = new BatchOptions(corpus, only, output, summarize, logLevel) { Overrides = overrides };
        return true;
    }
}

/// <summary>Corpus settings a run replaces for every repository; null keeps the corpus value.</summary>
/// <param name="Synthetic">Synthetic changes per repository.</param>
/// <param name="MutantParallelism">Mutants run at once.</param>
/// <param name="Parallel">Test processes per project when recording.</param>
internal sealed record BatchOverrides(int? Synthetic = null, int? MutantParallelism = null, int? Parallel = null);

/// <summary>
/// Evaluates every repository of a corpus, one after another, and adds the results up. A repository
/// that fails (clone, build, a crash of the tool) is reported and the run goes on with the next.
/// </summary>
internal static partial class BatchRunner
{
    /// <summary>Runs the corpus; returns 0 when nothing was missed, 3 when a test class was, 1 when a repository failed.</summary>
    /// <param name="options">Parsed command line.</param>
    public static int Run(BatchOptions options)
    {
        Corpus corpus;
        try
        {
            corpus = Corpus.Load(options.CorpusPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var unknown = options.Only.Where(n => !corpus.Repositories.Any(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Not in the corpus: {string.Join(", ", unknown)}");
            return 2;
        }

        Directory.CreateDirectory(options.Output);
        using var loggerFactory = new Sift.Cli.StderrLoggerFactory(Console.Error, options.LogLevel);
        var logger = loggerFactory.CreateLogger(typeof(BatchRunner).FullName!);

        var exitCode = 0;
        if (!options.SummarizeOnly)
        {
            foreach (var entry in corpus.Repositories)
            {
                if (options.Only.Count > 0 && !options.Only.Contains(entry.Name))
                {
                    continue;
                }

                exitCode = Math.Max(exitCode, RunEntry(corpus.Resolve(entry), options, logger));
                BatchSummary.Write(options.Output, corpus, options.Only);
            }
        }

        // A run of a few repositories (a nightly smoke run) reports on those, not on the whole corpus.
        var summary = BatchSummary.Write(options.Output, corpus, options.Only);
        Console.Out.Write(summary);
        Console.Out.WriteLine($"\nSummary: {Path.Combine(options.Output, "summary.md")}");
        return exitCode;
    }

    /// <summary>The evaluation settings of one repository: its corpus entry, with the run's overrides on top.</summary>
    /// <param name="entry">Repository with defaults applied.</param>
    /// <param name="options">Corpus run options.</param>
    internal static (EvalTarget Target, RunSettings Run) SettingsFor(CorpusEntry entry, BatchOptions options)
    {
        var synthetic = options.Overrides.Synthetic ?? entry.Synthetic ?? 0;
        var run = new RunSettings(
            options.Overrides.Parallel ?? entry.Parallel ?? 1,
            entry.Mutants ?? 2,
            entry.Projects ?? [],
            new RepoCommands(entry.Setup, entry.Cleanup),
            entry.Framework)
        {
            Excluded = entry.Exclude ?? [],
            // Outside the repository's directory, which is moved aside for a rerun after a fix.
            OutcomeCache = Path.Combine(options.Output, "outcome-cache", entry.Name),
            RecordingCache = Path.Combine(options.Output, "map-cache", entry.Name),
            MutantParallelism = options.Overrides.MutantParallelism ?? entry.MutantParallelism ?? 1,
        };
        if (entry.MutantKinds is { } kindList)
        {
            if (!MutantKinds.TryParse(kindList, out var kinds, out var kindError))
            {
                throw new InvalidDataException($"{entry.Name}: {kindError}");
            }

            run = run with { MutantKinds = kinds };
        }

        // Without a branch, the repository's default branch, whatever it is called.
        var target = new EvalTarget(entry.Url, entry.Branch ?? "origin/HEAD", entry.Merges ?? (synthetic > 0 ? 0 : 8), synthetic, entry.Seed ?? 1);
        return (target, run);
    }

    /// <summary>Evaluates one repository into its own directory; a failure is written to <c>error.txt</c>.</summary>
    /// <param name="entry">Repository with defaults applied.</param>
    /// <param name="options">Corpus run options.</param>
    /// <param name="logger">Receives progress.</param>
    private static int RunEntry(CorpusEntry entry, BatchOptions options, ILogger logger)
    {
        var directory = Path.Combine(options.Output, entry.Name);
        Directory.CreateDirectory(directory);
        var errorFile = Path.Combine(directory, "error.txt");
        File.Delete(errorFile);

        var (target, run) = SettingsFor(entry, options);
        var evalOptions = new EvalOptions(target, run, directory, options.LogLevel);

        LogStarting(logger, entry.Name, entry.Url);
        var started = DateTime.UtcNow;
        try
        {
            using var stdout = File.CreateText(Path.Combine(directory, "stdout.md"));
            var code = EvalApp.RunOne(evalOptions, stdout);
            LogFinished(logger, entry.Name, code, (DateTime.UtcNow - started).TotalMinutes);
            return code;
        }
        catch (Exception ex)
        {
            // One broken repository must not end a night-long run; the summary shows the reason.
            File.WriteAllText(errorFile, ex.ToString());
            LogRepositoryFailed(logger, entry.Name, ex);
            return 1;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Corpus: evaluating {Name} ({Url})")]
    private static partial void LogStarting(ILogger logger, string name, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Corpus: {Name} finished with exit code {ExitCode} after {Minutes:0} min")]
    private static partial void LogFinished(ILogger logger, string name, int exitCode, double minutes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Corpus: {Name} failed")]
    private static partial void LogRepositoryFailed(ILogger logger, string name, Exception exception);
}
