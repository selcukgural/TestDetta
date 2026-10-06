using System.Globalization;
using Microsoft.Extensions.Logging;
using TestDetta.Coverage;

namespace TestDetta.Eval;

/// <summary>What to evaluate.</summary>
/// <param name="Repository">Path or URL of the repository; it is cloned, never changed.</param>
/// <param name="Branch">Branch whose first-parent history holds the merges to evaluate.</param>
/// <param name="Merges">How many of the latest merges (or commits) to evaluate; 0 for none.</param>
/// <param name="Synthetic">Members to change synthetically at the branch head; 0 for none.</param>
/// <param name="Seed">Seed for picking the synthetic changes.</param>
internal sealed record EvalTarget(string Repository, string Branch, int Merges, int Synthetic = 0, int Seed = 1);

/// <summary>Parsed command line of <c>testdetta-eval</c>.</summary>
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
        Usage: testdetta-eval --repo <path-or-url> [options]
               testdetta-eval --corpus <file> [--only <name,...>] [--out <dir>] [--synthetic <n>]
                         [--mutant-parallelism <n>] [--mutant-kinds <list>] [--map-age <n>] [--parallel <n>]
                         [--compare-tia <dll>] [--compare-affected <exe>] [--toolchains <dir>] [--compare <dir>]
                         [--history <dir>] [--cache <dir>] [--explain] [--keep-clones] [-v]
               testdetta-eval --summarize <dir> --corpus <file> [--compare <dir>] [--history <dir>]

        Clones the repository and, for each of the latest merges on a branch, records coverage at the
        merge's first parent, lets TestDetta select tests for the merge, runs the selection and the full
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
          --map-age <n>          Also select each synthetic change with maps recorded n first-parent commits
                                 earlier: through the drift bridge, without maps, and with the old maps as
                                 if current (docs/design/map-drift-bridge.md, measurement 2); default 0
          --compare-tia <dll>    Also select each synthetic change with dotnet-tia (path of its Tia.Cli.dll)
                                 and judge it on the same mutants (docs/design/tia-comparison.md)
          --idle-timeout <span>  Kill a test run whose processes use almost no CPU for this long, as stuck
                                 (default 00:05:00; 00:00:00 waits for the run's timeout only)
          --memory-limit <GB>    Kill a test run whose processes hold more than this, as hung (default half
                                 the machine's memory; 0 turns it off). Large runs are also killed while
                                 less than 10 % of the machine's memory is free.
          --maps <dir>           Use these maps of the evaluated commit instead of recording
          --explain              Only write TestDetta's decisions and evidence for every sampled change to
                                 explain.json (no build, no test run; for the precision round)
          --compare-affected <exe>  Also select each synthetic change with dotnet-affected (its executable),
                                 running every affected test project whole
          --tia-solution <path>  Solution dotnet-tia analyses, relative to the repository (default: discovered)
          --focus <what>         Quick check: keep only the sampled changes in a source generator
                                 (generator), the data files (data-files) or a project referenced
                                 without compiling against it (output) of the usual sample, so
                                 stored outcomes are reused; all (default) keeps every change
          --max-mutants <n>      Evaluate at most n changes, in sample order after --focus
          --data-files <n>       Also empty n data files outside every project, one change each, to check that
                                 the tests reading them are selected (needs maps with file reads)
          --keep-clones          Keep the clone and every test report when the evaluation ends (default: a
                                 clone is deleted when nothing was missed, and the test reports of mutants
                                 that were neither missed nor crashed once they are read)
          --no-schema            Write each synthetic mutant in alone instead of switching it on in a
                                 schema (to compare both, or for a suite that notices the switches)
          --analysis-timeout <span>  Longest TestDetta may analyse one synthetic change (default 00:10:00); beyond
                                 it the evaluation stops and reports a hang in TestDetta
          --recording-cache <dir>  Keep recorded coverage maps in <dir> and reuse them while the commit, the
                                 recording settings and TestDetta's recording code are the same (corpus runs
                                 use <out>/map-cache/<name>, or <cache>/maps/<name> with --cache)
          --outcome-cache <dir>  Keep what the tests did against each synthetic mutant in <dir> and reuse it
                                 next time, so only TestDetta's selection is computed again (corpus runs use
                                 <out>/outcome-cache/<name>, or <cache>/outcomes/<name> with --cache)
          --sdk <version>        Build, record and test with this exact .NET SDK, installed under --toolchains
                                 from archives whose SHA-512 is in the toolchain lock (default: dotnet on PATH)
          --runtime <version>    Add this ASP.NET Core runtime to the toolchain; repeat for several
          --architecture <arch>  arm64 or x64 (x64 runs under Rosetta on Apple silicon); default the machine's
          --toolchains <dir>     Where toolchains are installed, one directory each, never changed afterwards
          --toolchain-lock <file>  Archive hashes (default: <toolchains>/toolchains.lock.json); read-only with
                                 CI=true, otherwise missing versions are added from Microsoft's release metadata
          --env <name=value>     Environment variable for every build and test run; repeat for several
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
        --compare <dir> compares the run with an earlier one (its summary.json) into comparison.md;
        --history <dir> keeps the run's summary there and redraws <dir>/dashboard.html from all runs.
        --cache <dir> keeps test outcomes and coverage maps for every round there instead of in <out>;
        with a corpus entry's "commit" pinned, a later round reuses them (docs/design/eval-result-cache.md).
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
        var output = Path.Combine(Path.GetTempPath(), "testdetta-eval-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
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

            if (arg == "--explain")
            {
                run = run with { ExplainOnly = true };
                continue;
            }

            if (arg == "--keep-clones")
            {
                run = run with { KeepClones = true };
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

                case "--env":
                    if (value.IndexOf('=', StringComparison.Ordinal) is not (> 0 and var equals))
                    {
                        error = $"--env needs name=value, not {value}";
                        return false;
                    }

                    run = run with { Environment = new Dictionary<string, string>(run.Environment) { [value[..equals]] = value[(equals + 1)..] } };
                    break;

                case "--toolchain-lock":
                    run = run with { ToolchainLock = Path.GetFullPath(value) };
                    break;

                case "--mutant-kinds":
                    if (!TestDetta.Mutants.MutantKinds.TryParse(value, out var kinds, out error))
                    {
                        return false;
                    }

                    run = run with { MutantKinds = kinds };
                    break;

                case "--compare-tia":
                    run = run with { TiaCli = Path.GetFullPath(value) };
                    break;

                case "--idle-timeout":
                    if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var idle) || idle < TimeSpan.Zero)
                    {
                        error = $"--idle-timeout needs a time span such as 00:05:00 (00:00:00 turns it off), not {value}";
                        return false;
                    }

                    run = run with { IdleTimeout = idle == TimeSpan.Zero ? null : idle };
                    break;

                case "--memory-limit":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gigabytes) || gigabytes < 0)
                    {
                        error = $"--memory-limit needs gigabytes such as 24 (0 turns it off), not {value}";
                        return false;
                    }

                    run = run with { MemoryLimit = gigabytes == 0 ? null : MemoryLimit.ForThisMachine() with { TreeBytes = (long)(gigabytes * (1L << 30)) } };
                    break;

                case "--compare-affected":
                    run = run with { AffectedCli = Path.GetFullPath(value) };
                    break;

                case "--maps":
                    run = run with { Maps = Path.GetFullPath(value) };
                    break;

                case "--tia-solution":
                    run = run with { TiaSolution = value };
                    break;

                case "--map-age":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var mapAge) || mapAge < 0)
                    {
                        error = $"--map-age needs a number of commits (0 or more), not {value}";
                        return false;
                    }

                    run = run with { MapAge = mapAge };
                    break;

                case "--focus":
                    if (!EvalFocusNames.TryParse(value, out var focus))
                    {
                        error = $"--focus takes {EvalFocusNames.All}, not {value}";
                        return false;
                    }

                    run = run with { Focus = focus };
                    break;

                case "--max-mutants":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var most) || most < 1)
                    {
                        error = $"--max-mutants needs a positive number, not {value}";
                        return false;
                    }

                    run = run with { MaxMutants = most };
                    break;

                case "--data-files":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var files) || files < 0)
                    {
                        error = $"--data-files needs a number, not {value}";
                        return false;
                    }

                    run = run with { DataFiles = files };
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
    /// When above 0, each synthetic change is also selected with maps recorded this many first-parent
    /// commits before the evaluated commit, in every <see cref="OtherSelector"/>, to measure the drift bridge.
    /// </summary>
    public int MapAge { get; init; }

    /// <summary>
    /// Path of dotnet-tia's <c>Tia.Cli.dll</c>: each synthetic change is also selected by
    /// <c>dotnet tia analyze</c> and judged on the same mutants (docs/design/tia-comparison.md); null for none.
    /// </summary>
    public string? TiaCli { get; init; }

    /// <summary>
    /// Maps recorded at the evaluated commit to use instead of recording (a directory of <c>*.json</c>,
    /// such as a corpus run's <c>map-cache/&lt;repo&gt;/&lt;sha&gt;-&lt;key&gt;</c>); null to record.
    /// </summary>
    public string? Maps { get; init; }

    /// <summary>
    /// True to only explain TestDetta's selection of every sampled change (<c>explain.json</c>): no build,
    /// no test run. For the precision round.
    /// </summary>
    public bool ExplainOnly { get; init; }

    /// <summary>
    /// True to keep the clone and every test report when the evaluation ends. Otherwise a clone whose
    /// evaluation missed nothing and did not fail is deleted, and so are the reports of mutants that were
    /// neither missed nor crashed: their outcomes are in the results and the outcome cache.
    /// </summary>
    public bool KeepClones { get; init; }

    /// <summary>
    /// Data files outside every project to empty, one change each, after the member changes
    /// (<see cref="DataFileSampler"/>); 0 for none.
    /// </summary>
    public int DataFiles { get; init; }

    /// <summary>The changes a quick check keeps of the sample (<c>--focus</c>); <see cref="EvalFocus.All"/> keeps all.</summary>
    public EvalFocus Focus { get; init; }

    /// <summary>Most changes evaluated, taken in sample order after <see cref="Focus"/> (<c>--max-mutants</c>); null for all.</summary>
    public int? MaxMutants { get; init; }

    /// <summary>Solution dotnet-tia analyses, relative to the repository root; null to let it discover one.</summary>
    public string? TiaSolution { get; init; }

    /// <summary>
    /// The <c>dotnet-affected</c> executable: each synthetic change is also selected by it and judged on
    /// the same mutants; null for none.
    /// </summary>
    public string? AffectedCli { get; init; }

    /// <summary>
    /// Longest TestDetta may take to analyse one synthetic change; beyond it the evaluation stops and reports
    /// a hang in TestDetta instead of waiting for ever. Analyses take seconds; the default leaves wide room.
    /// </summary>
    public TimeSpan AnalysisTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a test run's process tree may use almost no CPU before it is killed as stuck (a mutant
    /// that deadlocks the host), instead of waiting for the run's timeout; null to wait for the timeout.
    /// The outcome is the same as a timeout: the run hung.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When a test run's process tree is killed for its memory (<see cref="MemoryLimit"/>); null for
    /// never. The outcome is the same as a hang. The default keeps a mutant that allocates without end
    /// from resetting the machine, which also stops the other evaluations running on it.
    /// </summary>
    public MemoryLimit? MemoryLimit { get; init; } = MemoryLimit.ForThisMachine();

    /// <summary>
    /// The .NET the repository is built, recorded and tested with, installed under
    /// <see cref="ToolchainRoot"/> (docs/design/hermetic-toolchains.md); null for the <c>dotnet</c> on PATH.
    /// </summary>
    public Toolchain? Toolchain { get; init; }

    /// <summary>Directory toolchains are installed in; needed with <see cref="Toolchain"/>.</summary>
    public string? ToolchainRoot { get; init; }

    /// <summary>Lock file with the archives' hashes; null for <c>toolchains.lock.json</c> in <see cref="ToolchainRoot"/>.</summary>
    public string? ToolchainLock { get; init; }

    /// <summary>
    /// Environment variables every process of the evaluation gets (builds, test runs, recording),
    /// for what a repository needs on this machine, such as a larger default thread stack.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>Shell commands a repository needs besides dotnet.</summary>
/// <param name="Setup">Run after every checkout, before building: restoring local tools, for example. Null for none.</param>
/// <param name="Cleanup">Run after every test run: removing test containers, for example. Null for none.</param>
internal sealed record RepoCommands(string? Setup, string? Cleanup);
