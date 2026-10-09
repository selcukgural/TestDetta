using System.Globalization;
using Microsoft.Extensions.Logging;

namespace TestDetta.Eval;

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

    /// <summary>Where toolchains are installed (<c>--toolchains</c>); needed when an entry names a toolchain.</summary>
    public string? Toolchains { get; init; }

    /// <summary>
    /// Tags whose repositories to evaluate (<c>--tag</c>), next to <see cref="Only"/>; empty for none. The quick tier
    /// (<c>app</c>) is a tag, so a small change can run it and a large one the whole corpus.
    /// </summary>
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folder for test outcomes and coverage maps shared by every round (<c>--cache</c>); null keeps
    /// them in the output folder. Useful with pinned commits (docs/design/eval-result-cache.md).
    /// </summary>
    public string? Cache { get; init; }

    /// <summary>Directory or <c>summary.json</c> of an earlier run to compare with (<c>--compare</c>); null for none.</summary>
    public string? Compare { get; init; }

    /// <summary>History directory that keeps every run's summary and the dashboard (<c>--history</c>); null for none.</summary>
    public string? History { get; init; }

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
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logLevel = LogLevel.Information;
        var overrides = new BatchOverrides();
        string? toolchains = null;
        string? cache = null;
        string? compare = null;
        string? history = null;
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

            if (arg == "--explain")
            {
                overrides = overrides with { Explain = true };
                continue;
            }

            if (arg == "--keep-clones")
            {
                overrides = overrides with { KeepClones = true };
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

                case "--history":
                    history = Path.GetFullPath(value);
                    break;

                case "--compare":
                    compare = Path.GetFullPath(value);
                    break;

                case "--toolchains":
                    toolchains = Path.GetFullPath(value);
                    break;

                case "--cache":
                    cache = Path.GetFullPath(value);
                    break;

                case "--only":
                    only.UnionWith(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--tag":
                    tags.UnionWith(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--focus":
                    if (!EvalFocusNames.TryParse(value, out var focus))
                    {
                        error = $"--focus takes {EvalFocusNames.All}, not {value}";
                        return false;
                    }

                    overrides = overrides with { Focus = focus };
                    break;

                case "--max-mutants":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var most) || most < 1)
                    {
                        error = $"--max-mutants needs a positive number, not {value}";
                        return false;
                    }

                    overrides = overrides with { MaxMutants = most };
                    break;

                case "--data-files":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var files) || files < 0)
                    {
                        error = $"--data-files needs a number, not {value}";
                        return false;
                    }

                    overrides = overrides with { DataFiles = files };
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

                case "--compare-tia":
                    overrides = overrides with { TiaCli = Path.GetFullPath(value) };
                    break;

                case "--compare-affected":
                    overrides = overrides with { AffectedCli = Path.GetFullPath(value) };
                    break;

                case "--map-age":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var mapAge) || mapAge < 0)
                    {
                        error = $"--map-age needs a number of commits (0 or more), not {value}";
                        return false;
                    }

                    overrides = overrides with { MapAge = mapAge };
                    break;

                case "--mutant-kinds":
                    if (!MutantKinds.TryParse(value, out _, out error))
                    {
                        return false;
                    }

                    overrides = overrides with { MutantKinds = value };
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
        options = new BatchOptions(corpus, only, output, summarize, logLevel) { Overrides = overrides, Toolchains = toolchains, Compare = compare, History = history, Cache = cache, Tags = tags };
        return true;
    }
}

/// <summary>Corpus settings a run replaces for every repository; null keeps the corpus value.</summary>
/// <param name="Synthetic">Synthetic changes per repository.</param>
/// <param name="MutantParallelism">Mutants run at once.</param>
/// <param name="Parallel">Test processes per project when recording.</param>
/// <param name="MutantKinds">Mutant kinds of the synthetic sample, as <c>--mutant-kinds</c> takes them (checked when parsed).</param>
/// <param name="MapAge">Commits back to record the maps the drift modes select with; see <see cref="RunSettings.MapAge"/>.</param>
/// <param name="TiaCli">dotnet-tia to compare with; see <see cref="RunSettings.TiaCli"/>.</param>
/// <param name="AffectedCli">dotnet-affected to compare with; see <see cref="RunSettings.AffectedCli"/>.</param>
/// <param name="Explain">
/// Only record (or reuse) the maps and write TestDetta's decisions for every sampled change, without
/// running a test; see <see cref="RunSettings.ExplainOnly"/>. For the precision round, with each
/// repository's own commit, toolchain and environment.
/// </param>
/// <param name="KeepClones">Keep every clone and test report; see <see cref="RunSettings.KeepClones"/>.</param>
/// <param name="DataFiles">Data files to empty per repository; see <see cref="RunSettings.DataFiles"/>.</param>
/// <param name="Focus">Changes a quick check keeps; see <see cref="RunSettings.Focus"/>.</param>
/// <param name="MaxMutants">Most changes per repository; see <see cref="RunSettings.MaxMutants"/>.</param>
internal sealed record BatchOverrides(int? Synthetic = null, int? MutantParallelism = null, int? Parallel = null, string? MutantKinds = null, int? MapAge = null, string? TiaCli = null, string? AffectedCli = null, bool Explain = false, bool KeepClones = false, int DataFiles = 0, EvalFocus Focus = EvalFocus.All, int? MaxMutants = null);

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

        if (options.Tags.Count > 0)
        {
            var tagged = corpus.Repositories.Where(r => r.Tags?.Any(options.Tags.Contains) == true).Select(static r => r.Name).ToList();
            if (tagged.Count == 0)
            {
                Console.Error.WriteLine($"No corpus repository has the tag {string.Join(" or ", options.Tags)}");
                return 2;
            }

            options = options with { Only = new HashSet<string>([.. options.Only, .. tagged], StringComparer.OrdinalIgnoreCase) };
        }

        var unknown = options.Only.Where(n => !corpus.Repositories.Any(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Not in the corpus: {string.Join(", ", unknown)}");
            return 2;
        }

        var pinned = corpus.Repositories
            .Where(r => options.Only.Count == 0 || options.Only.Contains(r.Name))
            .FirstOrDefault(r => corpus.Resolve(r).Toolchain is not null);
        if (!options.SummarizeOnly && pinned is not null && options.Toolchains is null)
        {
            Console.Error.WriteLine($"{pinned.Name} names a toolchain; give the corpus run --toolchains <dir>.");
            return 2;
        }

        Directory.CreateDirectory(options.Output);
        using var loggerFactory = new TestDetta.Cli.StderrLoggerFactory(Console.Error, options.LogLevel);
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
                BatchSummary.Write(options.Output, corpus, options.Only, options.Compare);
            }
        }

        // A run of a few repositories (a nightly smoke run) reports on those, not on the whole corpus.
        var summary = BatchSummary.Write(options.Output, corpus, options.Only, options.Compare);
        Console.Out.Write(summary);
        if (options.Compare is { } previous)
        {
            Console.Out.WriteLine();
            Console.Out.Write(BatchComparison.Write(options.Output, previous));
        }

        Console.Out.WriteLine($"\nSummary: {Path.Combine(options.Output, "summary.md")}");
        if (options.History is { } historyDirectory)
        {
            Console.Out.WriteLine($"Dashboard: {BatchHistory.Record(options.Output, historyDirectory, DateTimeOffset.UtcNow)}");
        }
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
            // Outside the repository's directory, which is moved aside for a rerun after a fix. With
            // --cache, shared by every round, so a pinned commit's outcomes and maps are paid for once.
            OutcomeCache = options.Cache is { } cache ? Path.Combine(cache, "outcomes", entry.Name) : Path.Combine(options.Output, "outcome-cache", entry.Name),
            RecordingCache = options.Cache is { } maps ? Path.Combine(maps, "maps", entry.Name) : Path.Combine(options.Output, "map-cache", entry.Name),
            MutantParallelism = options.Overrides.MutantParallelism ?? entry.MutantParallelism ?? 1,
            MapAge = options.Overrides.MapAge ?? 0,
            TiaCli = options.Overrides.TiaCli,
            AffectedCli = options.Overrides.AffectedCli,
            ExplainOnly = options.Overrides.Explain,
            KeepClones = options.Overrides.KeepClones,
            DataFiles = options.Overrides.DataFiles,
            Focus = options.Overrides.Focus,
            MaxMutants = options.Overrides.MaxMutants,
            TiaSolution = entry.TiaSolution,
            Toolchain = entry.Toolchain?.ToToolchain(),
            Environment = entry.Environment ?? new Dictionary<string, string>(),
            ToolchainRoot = options.Toolchains,
            // Committed next to the corpus, so the machine and CI install the same bits.
            ToolchainLock = Path.Combine(Path.GetDirectoryName(options.CorpusPath)!, "toolchains.lock.json"),
        };
        if (run.Toolchain is not null && run.ToolchainRoot is null)
        {
            throw new InvalidDataException($"{entry.Name} names a toolchain; give the corpus run --toolchains <dir>.");
        }

        if ((options.Overrides.MutantKinds ?? entry.MutantKinds) is { } kindList)
        {
            if (!MutantKinds.TryParse(kindList, out var kinds, out var kindError))
            {
                throw new InvalidDataException($"{entry.Name}: {kindError}");
            }

            run = run with { MutantKinds = kinds };
        }

        // A pinned commit first; without a branch, the repository's default branch, whatever it is called.
        var target = new EvalTarget(entry.Url, entry.Commit ?? entry.Branch ?? "origin/HEAD", entry.Merges ?? (synthetic > 0 ? 0 : 8), synthetic, entry.Seed ?? 1);
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
