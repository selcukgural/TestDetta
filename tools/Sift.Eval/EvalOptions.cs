using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Sift.Eval;

/// <summary>What to evaluate.</summary>
/// <param name="Repository">Path or URL of the repository; it is cloned, never changed.</param>
/// <param name="Branch">Branch whose first-parent history holds the merges to evaluate.</param>
/// <param name="Merges">How many of the latest merges (or commits) to evaluate; 0 for none.</param>
/// <param name="Synthetic">Members to change synthetically at the branch head; 0 for none.</param>
/// <param name="Seed">Seed for picking the synthetic changes.</param>
internal sealed record EvalTarget(string Repository, string Branch, int Merges, int Synthetic = 0, int Seed = 1);

/// <summary>Parsed command line of <c>sift-eval</c>.</summary>
/// <param name="Target">What to evaluate.</param>
/// <param name="Run">Settings of every test run.</param>
/// <param name="Output">Directory for the work clone, coverage maps and reports.</param>
/// <param name="LogLevel">Minimum level of the log on stderr.</param>
internal sealed record EvalOptions(EvalTarget Target, RunSettings Run, string Output, LogLevel LogLevel)
{
    /// <summary>Repository to evaluate.</summary>
    public string Repository => Target.Repository;

    /// <summary>Help text.</summary>
    public const string Usage = """
        Usage: sift-eval --repo <path-or-url> [options]
               sift-eval --corpus <file> [--only <name,...>] [--out <dir>] [--synthetic <n>]
                         [--mutant-parallelism <n>] [--parallel <n>] [--toolchains <dir>] [-v]
               sift-eval --summarize <dir> --corpus <file>

        Clones the repository and, for each of the latest merges on a branch, records coverage at the
        merge's first parent, lets Sift select tests for the merge, runs the selection and the full
        suite, and checks the selection against injected faults (mutants).

          --repo <path-or-url>   Repository to evaluate (required); it is cloned, never changed
          --branch <name>        Branch to walk (default: main)
          --merges <n>           Latest first-parent commits to evaluate (default: 8, or 0 with --synthetic)
          --synthetic <n>        Also change <n> sampled members at the branch head, one at a time, and
                                 check each selection against a mutant of that member
          --seed <n>             Seed for sampling the synthetic changes (default: 1)
          --mutant-kinds <list>  throw, return-default, flip-branch, field-value, or all (default: throw)
          --mutant-parallelism <n>  Run n synthetic mutants at once on the clone (default: 1); needs the
                                 schema, and falls back to 1 when parallel runs of the suite interfere
          --no-schema            Write each synthetic mutant in alone instead of switching it on in a
                                 schema (to compare both, or for a suite that notices the switches)
          --analysis-timeout <span>  Longest Sift may analyse one synthetic change (default 00:10:00); beyond
                                 it the evaluation stops and reports a hang in Sift
          --recording-cache <dir>  Keep recorded coverage maps in <dir> and reuse them while the commit, the
                                 recording settings and Sift's recording code are the same (corpus runs
                                 use <out>/map-cache/<name>)
          --outcome-cache <dir>  Keep what the tests did against each synthetic mutant in <dir> and reuse it
                                 next time, so only Sift's selection is computed again (corpus runs use
                                 <out>/outcome-cache/<name>)
          --sdk <version>        Build, record and test with this exact .NET SDK, installed under --toolchains
                                 from archives whose SHA-512 is in the toolchain lock (default: dotnet on PATH)
          --runtime <version>    Add this ASP.NET Core runtime to the toolchain; repeat for several
          --architecture <arch>  arm64 or x64 (x64 runs under Rosetta on Apple silicon); default the machine's
          --toolchains <dir>     Where toolchains are installed, one directory each, never changed afterwards
          --toolchain-lock <file>  Archive hashes (default: <toolchains>/toolchains.lock.json); read-only with
                                 CI=true, otherwise missing versions are added from Microsoft's release metadata
          --parallel <n>         Test processes per project when recording (default: 1)
          --mutants <n>          Changed members to mutate per merge (default: 3, 0 turns it off)
          --project <csproj>     Only use this test project (path in the repository); repeat for several
          --exclude <csproj>     Leave this test project out, e.g. one that needs a database; repeat for several
          -f, --framework <tfm>  Build, record and test only this target framework, e.g. net10.0
          --setup <command>      Shell command run after every checkout, e.g. dotnet tool restore
          --cleanup <command>    Shell command run after every test run, e.g. to remove test containers
          --out <dir>            Work and report directory (default: a new directory under the temp folder)
          -v, --verbose          Debug logs on stderr

        With --corpus, every repository of the corpus file is evaluated with its own settings into
        <out>/<name>, and <out>/summary.md adds them up. --summarize only rebuilds the summary. A corpus
        whose entries name a toolchain needs --toolchains; the lock is toolchains.lock.json next to it.
        """;

    /// <summary>Parses the command line.</summary>
    /// <param name="args">Raw arguments.</param>
    /// <param name="options">The options when parsing succeeds.</param>
    /// <param name="error">A message for the user when parsing fails.</param>
    public static bool TryParse(ReadOnlySpan<string> args, out EvalOptions options, out string? error)
    {
        string? repository = null;
        var branch = "main";
        int? merges = null;
        var synthetic = 0;
        var seed = 1;
        var run = new RunSettings(Parallelism: 1, Mutants: 3, Projects: [], new RepoCommands(Setup: null, Cleanup: null), Framework: null);
        var logLevel = LogLevel.Information;
        string? sdk = null;
        var runtimes = new List<string>();
        string? architecture = null;
        var output = Path.Combine(Path.GetTempPath(), "sift-eval-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
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

            if (arg == "--no-schema")
            {
                run = run with { UseSchema = false };
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
                case "--repo":
                    repository = value;
                    break;

                case "--branch":
                    branch = value;
                    break;

                case "--out":
                    output = Path.GetFullPath(value);
                    break;

                case "--cleanup":
                    run = run with { Commands = run.Commands with { Cleanup = value } };
                    break;

                case "--setup":
                    run = run with { Commands = run.Commands with { Setup = value } };
                    break;

                case "-f" or "--framework":
                    run = run with { Framework = value };
                    break;

                case "--project":
                    run = run with { Projects = [.. run.Projects, value] };
                    break;

                case "--exclude":
                    run = run with { Excluded = [.. run.Excluded, value] };
                    break;

                case "--outcome-cache":
                    run = run with { OutcomeCache = Path.GetFullPath(value) };
                    break;

                case "--analysis-timeout":
                    if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var limit) || limit <= TimeSpan.Zero)
                    {
                        error = $"--analysis-timeout needs a positive time span such as 00:10:00, not {value}";
                        return false;
                    }

                    run = run with { AnalysisTimeout = limit };
                    break;

                case "--recording-cache":
                    run = run with { RecordingCache = Path.GetFullPath(value) };
                    break;

                case "--sdk":
                    sdk = value;
                    break;

                case "--runtime":
                    runtimes.Add(value);
                    break;

                case "--architecture":
                    architecture = value;
                    break;

                case "--toolchains":
                    run = run with { ToolchainRoot = Path.GetFullPath(value) };
                    break;

                case "--toolchain-lock":
                    run = run with { ToolchainLock = Path.GetFullPath(value) };
                    break;

                case "--mutant-kinds":
                    if (!Eval.MutantKinds.TryParse(value, out var kinds, out error))
                    {
                        return false;
                    }

                    run = run with { MutantKinds = kinds };
                    break;

                case "--merges" or "--parallel" or "--mutants" or "--synthetic" or "--seed" or "--mutant-parallelism":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var number) || number < 0 || (number == 0 && arg is "--parallel" or "--mutant-parallelism"))
                    {
                        error = $"{arg} needs a positive number, not {value}";
                        return false;
                    }

                    merges = arg == "--merges" ? number : merges;
                    synthetic = arg == "--synthetic" ? number : synthetic;
                    seed = arg == "--seed" ? number : seed;
                    run = arg switch
                    {
                        "--parallel" => run with { Parallelism = number },
                        "--mutants" => run with { Mutants = number },
                        "--mutant-parallelism" => run with { MutantParallelism = number },
                        _ => run,
                    };
                    break;

                default:
                    error = $"Unknown option: {arg}";
                    return false;
            }
        }

        if (repository is null)
        {
            error = "--repo is required";
            return false;
        }

        if (sdk is null && (runtimes.Count > 0 || architecture is not null))
        {
            error = "--runtime and --architecture belong to a toolchain: give --sdk too";
            return false;
        }

        if (sdk is not null)
        {
            var toolchain = new Toolchain(sdk, runtimes, architecture);
            if (!toolchain.IsValid(out error))
            {
                return false;
            }

            if (run.ToolchainRoot is null)
            {
                error = "--sdk needs --toolchains <dir> to install the toolchain in";
                return false;
            }

            run = run with { Toolchain = toolchain };
        }

        var target = new EvalTarget(repository, branch, merges ?? (synthetic > 0 ? 0 : 8), synthetic, seed);
        if (target is { Merges: 0, Synthetic: 0 })
        {
            error = "Nothing to evaluate: give --merges or --synthetic a positive number";
            return false;
        }

        options = new EvalOptions(target, run, output, logLevel);
        return true;
    }
}

/// <summary>Settings shared by every recording and test run of an evaluation.</summary>
/// <param name="Parallelism">Test processes per project when recording.</param>
/// <param name="Mutants">Changed members to mutate per merge.</param>
/// <param name="Projects">Test projects to use (paths in the repository); empty means all.</param>
/// <param name="Commands">Repository-specific shell commands around checkouts and test runs.</param>
/// <param name="Framework">Target framework to build, record and test, or null for the projects' defaults.</param>
internal sealed record RunSettings(int Parallelism, int Mutants, IReadOnlyList<string> Projects, RepoCommands Commands, string? Framework)
{
    /// <summary>Test projects (paths in the repository) to leave out, such as ones that need a database server.</summary>
    public IReadOnlyList<string> Excluded { get; init; } = [];

    /// <summary>Kinds of synthetic mutants; throwing at the start of a member unless told otherwise.</summary>
    public IReadOnlySet<MutantKind> MutantKinds { get; init; } = new HashSet<MutantKind> { MutantKind.Throw };

    /// <summary>
    /// Directory where what the tests did against each synthetic mutant is kept between evaluations
    /// of the repository (see <see cref="OutcomeCache"/>); null to run every mutant's tests.
    /// </summary>
    public string? OutcomeCache { get; init; }

    /// <summary>
    /// Directory where recorded coverage maps are kept between evaluations, keyed by commit and by
    /// everything that decides a recording (see <c>EvalSteps.RecordingKey</c>); null to record every time.
    /// </summary>
    public string? RecordingCache { get; init; }

    /// <summary>
    /// True (the default) to switch synthetic mutants on in a schema; false to write each one in
    /// alone, as before schemata, which is also what a schema that changes the suite falls back to.
    /// </summary>
    public bool UseSchema { get; init; } = true;

    /// <summary>
    /// Synthetic mutants run at once on one clone (needs a schema; a repository with a cleanup command
    /// always runs one). The evaluation lowers it to 1 when parallel runs of the suite interfere.
    /// </summary>
    public int MutantParallelism { get; init; } = 1;

    /// <summary>
    /// Longest Sift may take to analyse one synthetic change; beyond it the evaluation stops and reports
    /// a hang in Sift instead of waiting for ever. Analyses take seconds; the default leaves wide room.
    /// </summary>
    public TimeSpan AnalysisTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The .NET the repository is built, recorded and tested with, installed under
    /// <see cref="ToolchainRoot"/> (docs/design/hermetic-toolchains.md); null for the <c>dotnet</c> on PATH.
    /// </summary>
    public Toolchain? Toolchain { get; init; }

    /// <summary>Directory toolchains are installed in; needed with <see cref="Toolchain"/>.</summary>
    public string? ToolchainRoot { get; init; }

    /// <summary>Lock file with the archives' hashes; null for <c>toolchains.lock.json</c> in <see cref="ToolchainRoot"/>.</summary>
    public string? ToolchainLock { get; init; }
}

/// <summary>Shell commands a repository needs besides dotnet.</summary>
/// <param name="Setup">Run after every checkout, before building: restoring local tools, for example. Null for none.</param>
/// <param name="Cleanup">Run after every test run: removing test containers, for example. Null for none.</param>
internal sealed record RepoCommands(string? Setup, string? Cleanup);
