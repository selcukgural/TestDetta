using Microsoft.Extensions.Logging;

namespace TestDetta.Cli;

/// <summary>The sub-command given as the first argument.</summary>
internal enum CliCommand
{
    /// <summary><c>td affected</c>: print what a change affects.</summary>
    Affected,

    /// <summary><c>td test</c>: run the tests a change affects.</summary>
    Test,

    /// <summary><c>td record</c>: run all tests with coverage and store which code each test class runs.</summary>
    Record,

    /// <summary><c>td license status</c>: check the license and show what it found.</summary>
    LicenseStatus,

    /// <summary><c>td license install &lt;license&gt;</c>: store a license in <c>~/.testdetta/license</c>.</summary>
    LicenseInstall,
}

/// <summary>Options every command shares: what to compare and how much to log.</summary>
/// <param name="BaseRef">Git ref to compare against.</param>
/// <param name="RepoPath">Any path inside the repository.</param>
/// <param name="LogLevel">Minimum level of the diagnostic log written to stderr.</param>
internal sealed record AnalysisSettings(string BaseRef, string RepoPath, LogLevel LogLevel)
{
    /// <summary>How many commits a coverage map may be behind the merge base and still be used through the drift bridge.</summary>
    public int MaxMapAge { get; init; } = TestDetta.Core.AnalysisOptions.DefaultMaxMapAge;

    /// <summary>What a test project does when its older map's drift cannot be bridged.</summary>
    public TestDetta.Core.DriftFallback DriftFallback { get; init; } = TestDetta.Core.DriftFallback.RunAll;

    /// <summary>Folder of per-commit coverage maps outside the repository (<see cref="MapDirectory"/>); null when unused.</summary>
    public string? MapsDir { get; init; }
}

/// <summary>Options of <c>td affected</c>.</summary>
/// <param name="TestsOnly">Print only test projects.</param>
/// <param name="Format">Output format for stdout.</param>
internal sealed record AffectedSettings(bool TestsOnly, OutputFormat Format)
{
    /// <summary>
    /// True to build the test projects that use a changed source generator, to compare what it
    /// generates; otherwise only a build newer than the sources is read, and without one such a
    /// project is listed as running completely.
    /// </summary>
    public bool Build { get; init; }
}

/// <summary>Options of <c>td test</c>.</summary>
/// <param name="DryRun">Print the <c>dotnet test</c> commands instead of running them.</param>
/// <param name="DotnetTestArguments">Arguments after <c>--</c>, passed to every <c>dotnet test</c> as given.</param>
/// <param name="SummaryFile">File to append a Markdown summary to (for example <c>$GITHUB_STEP_SUMMARY</c>), or null.</param>
internal sealed record TestSettings(bool DryRun, IReadOnlyList<string> DotnetTestArguments, string? SummaryFile)
{
    /// <summary>
    /// Shadow mode (docs/design/shadow-mode.md): select as usual, run every test project completely, and
    /// report what the selection would have left out. The exit code is the full run's.
    /// </summary>
    public bool Shadow { get; init; }

    /// <summary>In shadow mode, rerun each failed class the selection left out to tell a miss from a flaky or pre-existing failure.</summary>
    public bool ShadowClassify { get; init; } = true;

    /// <summary>In shadow mode, where the JSON report goes; null for <c>.testdetta/testdetta-shadow.json</c> in the repository.</summary>
    public string? ShadowReport { get; init; }

    /// <summary>
    /// In shadow mode, the classification's time limits; null when none was given on the command line, which
    /// means <see cref="TestDetta.Cli.ShadowLimits.Default"/> (kept apart so that giving one without
    /// <c>--shadow</c> is an error, even with its default value).
    /// </summary>
    public ShadowLimits? ShadowLimits { get; init; }

    /// <summary>
    /// The configuration <c>dotnet test</c> builds, from <c>-c</c> or <c>--configuration</c> among
    /// <see cref="DotnetTestArguments"/>; Debug when none is given. A changed source generator is read
    /// from a build of this configuration, the one the tests then run.
    /// </summary>
    public string Configuration
    {
        get
        {
            for (var i = 0; i < DotnetTestArguments.Count; i++)
            {
                var argument = DotnetTestArguments[i];
                if (argument is "-c" or "--configuration" && i + 1 < DotnetTestArguments.Count)
                {
                    return DotnetTestArguments[i + 1];
                }

                foreach (var prefix in (ReadOnlySpan<string>)["-c:", "--configuration:", "--configuration="])
                {
                    if (argument.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return argument[prefix.Length..];
                    }
                }
            }

            return "Debug";
        }
    }
}

/// <summary>
/// How long shadow mode's classification may take (docs/design/shadow-mode.md); every value is null for no
/// limit. The full run has none of these: it runs as the team's own run would.
/// </summary>
/// <param name="RerunTimeout">Longest one rerun of a project's left-out failed classes with the change may take.</param>
/// <param name="RerunIdle">Longest such a rerun's process tree may use almost no CPU before it is killed as stuck.</param>
/// <param name="BaseTimeout">Longest the rerun on the base commit may take, its restore and build included.</param>
/// <param name="BaseIdle">
/// The idle limit of the rerun on the base commit: longer, because its restore in a fresh checkout waits on
/// the network with the CPU nearly still.
/// </param>
/// <param name="Budget">
/// Longest the whole classification may take, every rerun and the base commit's checkout together; a rerun
/// is stopped where the budget ends, and the classes not classified by then are reported as not classified.
/// </param>
internal sealed record ShadowLimits(TimeSpan? RerunTimeout, TimeSpan? RerunIdle, TimeSpan? BaseTimeout, TimeSpan? BaseIdle, TimeSpan? Budget)
{
    /// <summary>
    /// The defaults: a rerun with the change needs no build, so 15 minutes (5 idle) is plenty; the base commit
    /// restores and builds first, 30 minutes (10 idle); 45 minutes in all keep a pull request's job from
    /// growing by more than that, however many projects have left-out failures.
    /// </summary>
    public static ShadowLimits Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(45));

    /// <summary>The limits as the command line gives them, for the reproduction block.</summary>
    public IReadOnlyList<string> Options =>
    [
        $"--shadow-rerun-timeout {Durations.Limit(RerunTimeout)}",
        $"--shadow-rerun-idle-timeout {Durations.Limit(RerunIdle)}",
        $"--shadow-base-timeout {Durations.Limit(BaseTimeout)}",
        $"--shadow-base-idle-timeout {Durations.Limit(BaseIdle)}",
        $"--shadow-classify-budget {Durations.Limit(Budget)}",
    ];

    /// <summary>The per-rerun limits in one phrase, for the reports.</summary>
    public string Describe() =>
        $"a rerun with the change at most {Durations.DescribeLimit(RerunTimeout)} (idle {Durations.DescribeLimit(RerunIdle)}), " +
        $"on the base commit at most {Durations.DescribeLimit(BaseTimeout)} (idle {Durations.DescribeLimit(BaseIdle)})";
}

/// <summary>Options of <c>td record</c>.</summary>
/// <param name="Configuration">Build configuration used for building and running the tests.</param>
/// <param name="Projects">Only record these test projects (.csproj paths); empty records every test project.</param>
/// <param name="Parallelism">Test host processes per test project; each still runs its tests one at a time.</param>
/// <param name="Framework">Target framework to record for multi-targeted projects, or null for the first one.</param>
internal sealed record RecordSettings(string Configuration, IReadOnlyList<string> Projects, int Parallelism, string? Framework = null)
{
    /// <summary>True to record from the existing build output instead of building.</summary>
    public bool NoBuild { get; init; }
}

/// <summary>Options of <c>td license</c>.</summary>
/// <param name="Install">For <c>install</c>: a license string, or the path of a file holding one.</param>
internal sealed record LicenseSettings(string? Install);

/// <summary>Parsed command line.</summary>
/// <param name="Command">Which sub-command to run.</param>
/// <param name="Analysis">Options shared by all commands.</param>
/// <param name="Affected">Options of <c>td affected</c>; defaults for other commands.</param>
/// <param name="Test">Options of <c>td test</c>; defaults for other commands.</param>
/// <param name="Record">Options of <c>td record</c>; defaults for other commands.</param>
internal sealed record CliOptions(
    CliCommand Command,
    AnalysisSettings Analysis,
    AffectedSettings Affected,
    TestSettings Test,
    RecordSettings Record)
{
    /// <summary>Options of <c>td license</c>; defaults for other commands.</summary>
    public LicenseSettings License { get; init; } = new(Install: null);

    /// <summary>Help text printed for <c>--help</c> and after a usage error.</summary>
    public const string Usage = """
        Usage:
          td affected [options]              Print the projects and tests a change affects
          td test [options] [-- <args>]      Run only the tests a change affects
          td record [options]                Run all tests with coverage and store the map in .testdetta/coverage
          td license status [options]        Check the TestDetta license and show what it covers
          td license install <license|file>  Store a license in ~/.testdetta/license (when no CI secret can hold it)

        Options:
          --base <ref>         Git ref to compare against (default: origin/main)
          --repo <path>        Any path inside the repository (default: current directory)
          -v, --verbose        Write debug logs to stderr
          --log-level <level>  trace, debug, information, warning (default), error or none
          --max-map-age <n>    Use coverage maps recorded up to n commits before the merge base,
                               bridging the changes in between (default: 50; 0 = only exact maps)
          --maps-dir <dir>     Coverage maps kept outside the repository, one folder per commit: record
                               saves there, affected and test restore the newest one within --max-map-age
                               (for CI systems other than GitHub Actions; see docs/ci.md)
          --drift-fallback <all|name>  When the changes since an older map include a build file,
                               configuration or a global using, run the affected test projects
                               completely (all, the default) or decide them by name (name: faster,
                               blind to calls made through configuration or reflection)

        td affected:
          --tests-only         Only list affected test projects
          --format <format>    text (default), json, paths (one .csproj per line),
                               or commands (one dotnet test command per test project)
          --build              When a change reaches a source generator in the repository, build the
                               test projects using it to compare what it generates (td test always
                               does); without it, an existing build newer than the sources is read

        td test:
          --dry-run            Print the dotnet test commands instead of running them
          --summary-file <path>  Append a Markdown summary, e.g. to $GITHUB_STEP_SUMMARY
          --shadow             Shadow mode: select as usual, but run every test project completely and
                               report what the selection would have left out; the exit code is the
                               full run's, and a TestDetta error never fails it
          --shadow-no-classify Do not rerun failed classes the selection left out to tell a miss from
                               a flaky or pre-existing failure
          --shadow-report <path>  Shadow report file (default: .testdetta/testdetta-shadow.json)
          --shadow-rerun-timeout <time>       Longest one rerun with the change may take (default: 15m)
          --shadow-rerun-idle-timeout <time>  Stop such a rerun after this long with almost no CPU (default: 5m)
          --shadow-base-timeout <time>        Longest the rerun on the base commit may take, build
                               included (default: 30m)
          --shadow-base-idle-timeout <time>   Idle limit of the rerun on the base commit (default: 10m)
          --shadow-classify-budget <time>     Longest all reruns together may take; what is left then
                               is reported as not classified (default: 45m)
                               A <time> is 90s, 15m, 1h or 1h30m; a plain number is minutes;
                               0 or none means no limit. The full run itself has no limit.
          -- <args>            Pass the remaining arguments to every dotnet test, e.g. -- -c Release

        td record:
          -c, --configuration <name>  Build configuration (default: Debug)
          --project <path>     Record only this test project (relative to the repository root); repeat for several
          -f, --framework <tfm>  Target framework to record, e.g. net10.0 (default: the first one)
          --parallel <n>       Spread each project's test classes over n processes (default: 1).
                               The suite must tolerate several test processes at once.
          --no-build           Use the existing build output (built with the same -c) instead of building

        License: set TESTDETTA_LICENSE (or TESTDETTA_LICENSE_FILE) in CI. Public repositories need none.
        Without a valid license every test runs; the build never fails because of it.

        Exit codes: 0 success, 1 analysis failed, 2 invalid command line, 3 tests failed.
        """;

    /// <summary>Parses the whole command line, including the command name.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="currentDirectory">Used as the repository path when <c>--repo</c> is not given.</param>
    /// <param name="options">The parsed options when parsing succeeds.</param>
    /// <param name="error">A message for the user when parsing fails.</param>
    /// <returns>True when all arguments were understood.</returns>
    public static bool TryParse(
        ReadOnlySpan<string> args,
        string currentDirectory,
        out CliOptions options,
        out string? error)
    {
        options = new CliOptions(
            CliCommand.Affected,
            new AnalysisSettings("origin/main", currentDirectory, LogLevel.Warning),
            new AffectedSettings(TestsOnly: false, OutputFormat.Text),
            new TestSettings(DryRun: false, [], SummaryFile: null),
            new RecordSettings("Debug", [], Parallelism: 1));
        error = null;
        var first = 1;

        switch (args)
        {
            case ["license", "status", ..]:
                options = options with { Command = CliCommand.LicenseStatus };
                first = 2;
                break;

            case ["license", "install", var license, ..]:
                options = options with { Command = CliCommand.LicenseInstall, License = new LicenseSettings(license) };
                first = 3;
                break;

            case ["license", ..]:
                error = "td license needs status or install <license|file>";
                return false;

            case ["affected", ..]:
                break;

            case ["test", ..]:
                options = options with { Command = CliCommand.Test };
                break;

            case ["record", ..]:
                options = options with { Command = CliCommand.Record };
                break;

            case []:
                error = "Missing command";
                return false;

            default:
                error = $"Unknown command: {args[0]}";
                return false;
        }

        for (var i = first; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "--")
            {
                if (options.Command != CliCommand.Test)
                {
                    error = "'--' is only valid for td test";
                    return false;
                }

                options = options with { Test = options.Test with { DotnetTestArguments = [.. args[(i + 1)..]] } };
                return Consistent(options, out error);
            }

            if (!TryParseOption(args, ref i, ref options, out error))
            {
                return false;
            }
        }

        return Consistent(options, out error);
    }

    /// <summary>False, with the reason, for options that contradict each other.</summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="error">Why they cannot go together.</param>
    private static bool Consistent(CliOptions options, out string? error)
    {
        var test = options.Test;
        error = test switch
        {
            { Shadow: true, DryRun: true } => "--shadow runs every test; it cannot be combined with --dry-run",
            { Shadow: false, ShadowClassify: false } or { Shadow: false, ShadowReport: not null } or { Shadow: false, ShadowLimits: not null } =>
                "--shadow-no-classify, --shadow-report, the --shadow-*-timeout options and --shadow-classify-budget need --shadow",
            _ => null,
        };
        return error is null;
    }

    /// <summary>Parses the option at <paramref name="i"/>, advancing past its value when it has one.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="i">Index of the option; moved to its value when the option takes one.</param>
    /// <param name="options">Options so far; updated in place.</param>
    /// <param name="error">A message for the user when the option is invalid.</param>
    private static bool TryParseOption(ReadOnlySpan<string> args, ref int i, ref CliOptions options, out string? error)
    {
        error = null;
        var arg = args[i];

        switch (arg)
        {
            case "-v" or "--verbose":
                options = options with { Analysis = options.Analysis with { LogLevel = LogLevel.Debug } };
                return true;

            case "--tests-only" when options.Command == CliCommand.Affected:
                options = options with { Affected = options.Affected with { TestsOnly = true } };
                return true;

            case "--build" when options.Command == CliCommand.Affected:
                options = options with { Affected = options.Affected with { Build = true } };
                return true;

            case "--dry-run" when options.Command == CliCommand.Test:
                options = options with { Test = options.Test with { DryRun = true } };
                return true;

            case "--shadow" when options.Command == CliCommand.Test:
                options = options with { Test = options.Test with { Shadow = true } };
                return true;

            case "--shadow-no-classify" when options.Command == CliCommand.Test:
                options = options with { Test = options.Test with { ShadowClassify = false } };
                return true;

            case "--no-build" when options.Command == CliCommand.Record:
                options = options with { Record = options.Record with { NoBuild = true } };
                return true;

            case "--base" or "--repo" or "--log-level" or "--max-map-age" or "--drift-fallback":
            case "--maps-dir" when options.Command is CliCommand.Affected or CliCommand.Test or CliCommand.Record:
            case "--format" when options.Command == CliCommand.Affected:
            case "-c" or "--configuration" or "--project" or "--parallel" or "-f" or "--framework" when options.Command == CliCommand.Record:
            case "--summary-file" or "--shadow-report" when options.Command == CliCommand.Test:
            case "--shadow-rerun-timeout" or "--shadow-rerun-idle-timeout" or "--shadow-base-timeout" or "--shadow-base-idle-timeout" or "--shadow-classify-budget" when options.Command == CliCommand.Test:
                break;

            default:
                error = $"Unknown option for td {options.Command.ToString().ToLowerInvariant()}: {arg}";
                return false;
        }

        if (i + 1 >= args.Length)
        {
            error = $"{arg} needs a value";
            return false;
        }

        var value = args[++i];
        switch (arg)
        {
            case "--base":
                options = options with { Analysis = options.Analysis with { BaseRef = value } };
                return true;

            case "--repo":
                options = options with { Analysis = options.Analysis with { RepoPath = value } };
                return true;

            case "--maps-dir":
                options = options with { Analysis = options.Analysis with { MapsDir = value } };
                return true;

            case "--drift-fallback":
                if (value is not ("all" or "name"))
                {
                    error = $"--drift-fallback is all or name, not {value}";
                    return false;
                }

                options = options with
                {
                    Analysis = options.Analysis with { DriftFallback = value == "all" ? TestDetta.Core.DriftFallback.RunAll : TestDetta.Core.DriftFallback.ByName },
                };
                return true;

            case "--max-map-age":
                if (!int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var maxMapAge) || maxMapAge < 0)
                {
                    error = $"--max-map-age needs a number of commits (0 or more), not {value}";
                    return false;
                }

                options = options with { Analysis = options.Analysis with { MaxMapAge = maxMapAge } };
                return true;

            case "-c" or "--configuration":
                options = options with { Record = options.Record with { Configuration = value } };
                return true;

            case "-f" or "--framework":
                options = options with { Record = options.Record with { Framework = value } };
                return true;

            case "--project":
                options = options with { Record = options.Record with { Projects = [.. options.Record.Projects, value] } };
                return true;

            case "--parallel":
                if (!int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parallelism) || parallelism < 1)
                {
                    error = $"--parallel needs a positive number, not {value}";
                    return false;
                }

                options = options with { Record = options.Record with { Parallelism = parallelism } };
                return true;

            case "--summary-file":
                options = options with { Test = options.Test with { SummaryFile = value } };
                return true;

            case "--shadow-report":
                options = options with { Test = options.Test with { ShadowReport = value } };
                return true;

            case "--shadow-rerun-timeout" or "--shadow-rerun-idle-timeout" or "--shadow-base-timeout" or "--shadow-base-idle-timeout" or "--shadow-classify-budget":
                if (!Durations.TryParseLimit(value, out var limit))
                {
                    error = $"{arg} needs a time such as 90s, 15m, 1h or 1h30m, at most 168h (a plain number is minutes; 0 or none means no limit), not {value}";
                    return false;
                }

                var limits = options.Test.ShadowLimits ?? ShadowLimits.Default;
                limits = arg switch
                {
                    "--shadow-rerun-timeout" => limits with { RerunTimeout = limit },
                    "--shadow-rerun-idle-timeout" => limits with { RerunIdle = limit },
                    "--shadow-base-timeout" => limits with { BaseTimeout = limit },
                    "--shadow-base-idle-timeout" => limits with { BaseIdle = limit },
                    _ => limits with { Budget = limit },
                };
                options = options with { Test = options.Test with { ShadowLimits = limits } };
                return true;

            case "--format":
                if (!Enum.TryParse<OutputFormat>(value, ignoreCase: true, out var format))
                {
                    error = $"Unknown format: {value}";
                    return false;
                }

                options = options with { Affected = options.Affected with { Format = format } };
                return true;

            default:
                if (!Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level))
                {
                    error = $"Unknown log level: {value}";
                    return false;
                }

                options = options with { Analysis = options.Analysis with { LogLevel = level } };
                return true;
        }
    }
}
