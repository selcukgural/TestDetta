using Microsoft.Extensions.Logging;

namespace Sift.Cli;

/// <summary>The sub-command given as the first argument.</summary>
internal enum CliCommand
{
    /// <summary><c>sift affected</c>: print what a change affects.</summary>
    Affected,

    /// <summary><c>sift test</c>: run the tests a change affects.</summary>
    Test,

    /// <summary><c>sift record</c>: run all tests with coverage and store which code each test class runs.</summary>
    Record,

    /// <summary><c>sift license status</c>: check the license and show what it found.</summary>
    LicenseStatus,

    /// <summary><c>sift license install &lt;license&gt;</c>: store a license in <c>~/.sift/license</c>.</summary>
    LicenseInstall,
}

/// <summary>Options every command shares: what to compare and how much to log.</summary>
/// <param name="BaseRef">Git ref to compare against.</param>
/// <param name="RepoPath">Any path inside the repository.</param>
/// <param name="LogLevel">Minimum level of the diagnostic log written to stderr.</param>
internal sealed record AnalysisSettings(string BaseRef, string RepoPath, LogLevel LogLevel)
{
    /// <summary>How many commits a coverage map may be behind the merge base and still be used through the drift bridge.</summary>
    public int MaxMapAge { get; init; } = Sift.Core.AnalysisOptions.DefaultMaxMapAge;

    /// <summary>What a test project does when its older map's drift cannot be bridged.</summary>
    public Sift.Core.DriftFallback DriftFallback { get; init; } = Sift.Core.DriftFallback.RunAll;
}

/// <summary>Options of <c>sift affected</c>.</summary>
/// <param name="TestsOnly">Print only test projects.</param>
/// <param name="Format">Output format for stdout.</param>
internal sealed record AffectedSettings(bool TestsOnly, OutputFormat Format);

/// <summary>Options of <c>sift test</c>.</summary>
/// <param name="DryRun">Print the <c>dotnet test</c> commands instead of running them.</param>
/// <param name="DotnetTestArguments">Arguments after <c>--</c>, passed to every <c>dotnet test</c> as given.</param>
/// <param name="SummaryFile">File to append a Markdown summary to (for example <c>$GITHUB_STEP_SUMMARY</c>), or null.</param>
internal sealed record TestSettings(bool DryRun, IReadOnlyList<string> DotnetTestArguments, string? SummaryFile);

/// <summary>Options of <c>sift record</c>.</summary>
/// <param name="Configuration">Build configuration used for building and running the tests.</param>
/// <param name="Projects">Only record these test projects (.csproj paths); empty records every test project.</param>
/// <param name="Parallelism">Test host processes per test project; each still runs its tests one at a time.</param>
/// <param name="Framework">Target framework to record for multi-targeted projects, or null for the first one.</param>
internal sealed record RecordSettings(string Configuration, IReadOnlyList<string> Projects, int Parallelism, string? Framework = null)
{
    /// <summary>True to record from the existing build output instead of building.</summary>
    public bool NoBuild { get; init; }
}

/// <summary>Options of <c>sift license</c>.</summary>
/// <param name="Install">For <c>install</c>: a license string, or the path of a file holding one.</param>
internal sealed record LicenseSettings(string? Install);

/// <summary>Parsed command line.</summary>
/// <param name="Command">Which sub-command to run.</param>
/// <param name="Analysis">Options shared by all commands.</param>
/// <param name="Affected">Options of <c>sift affected</c>; defaults for other commands.</param>
/// <param name="Test">Options of <c>sift test</c>; defaults for other commands.</param>
/// <param name="Record">Options of <c>sift record</c>; defaults for other commands.</param>
internal sealed record CliOptions(
    CliCommand Command,
    AnalysisSettings Analysis,
    AffectedSettings Affected,
    TestSettings Test,
    RecordSettings Record)
{
    /// <summary>Options of <c>sift license</c>; defaults for other commands.</summary>
    public LicenseSettings License { get; init; } = new(Install: null);

    /// <summary>Help text printed for <c>--help</c> and after a usage error.</summary>
    public const string Usage = """
        Usage:
          sift affected [options]              Print the projects and tests a change affects
          sift test [options] [-- <args>]      Run only the tests a change affects
          sift record [options]                Run all tests with coverage and store the map in .sift/coverage
          sift license status [options]        Check the Sift license and show what it covers
          sift license install <license|file>  Store a license in ~/.sift/license (when no CI secret can hold it)

        Options:
          --base <ref>         Git ref to compare against (default: origin/main)
          --repo <path>        Any path inside the repository (default: current directory)
          -v, --verbose        Write debug logs to stderr
          --log-level <level>  trace, debug, information, warning (default), error or none
          --max-map-age <n>    Use coverage maps recorded up to n commits before the merge base,
                               bridging the changes in between (default: 50; 0 = only exact maps)
          --drift-fallback <all|name>  When the changes since an older map include a build file,
                               configuration or a global using, run the affected test projects
                               completely (all, the default) or decide them by name (name: faster,
                               blind to calls made through configuration or reflection)

        sift affected:
          --tests-only         Only list affected test projects
          --format <format>    text (default), json, paths (one .csproj per line),
                               or commands (one dotnet test command per test project)

        sift test:
          --dry-run            Print the dotnet test commands instead of running them
          --summary-file <path>  Append a Markdown summary, e.g. to $GITHUB_STEP_SUMMARY
          -- <args>            Pass the remaining arguments to every dotnet test, e.g. -- -c Release

        sift record:
          -c, --configuration <name>  Build configuration (default: Debug)
          --project <path>     Record only this test project (relative to the repository root); repeat for several
          -f, --framework <tfm>  Target framework to record, e.g. net10.0 (default: the first one)
          --parallel <n>       Spread each project's test classes over n processes (default: 1).
                               The suite must tolerate several test processes at once.
          --no-build           Use the existing build output (built with the same -c) instead of building

        License: set SIFT_LICENSE (or SIFT_LICENSE_FILE) in CI. Public repositories need none.
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
                error = "sift license needs status or install <license|file>";
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
                    error = "'--' is only valid for sift test";
                    return false;
                }

                options = options with { Test = options.Test with { DotnetTestArguments = [.. args[(i + 1)..]] } };
                return true;
            }

            if (!TryParseOption(args, ref i, ref options, out error))
            {
                return false;
            }
        }

        return true;
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

            case "--dry-run" when options.Command == CliCommand.Test:
                options = options with { Test = options.Test with { DryRun = true } };
                return true;

            case "--no-build" when options.Command == CliCommand.Record:
                options = options with { Record = options.Record with { NoBuild = true } };
                return true;

            case "--base" or "--repo" or "--log-level" or "--max-map-age" or "--drift-fallback":
            case "--format" when options.Command == CliCommand.Affected:
            case "-c" or "--configuration" or "--project" or "--parallel" or "-f" or "--framework" when options.Command == CliCommand.Record:
            case "--summary-file" when options.Command == CliCommand.Test:
                break;

            default:
                error = $"Unknown option for sift {options.Command.ToString().ToLowerInvariant()}: {arg}";
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

            case "--drift-fallback":
                if (value is not ("all" or "name"))
                {
                    error = $"--drift-fallback is all or name, not {value}";
                    return false;
                }

                options = options with
                {
                    Analysis = options.Analysis with { DriftFallback = value == "all" ? Sift.Core.DriftFallback.RunAll : Sift.Core.DriftFallback.ByName },
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
