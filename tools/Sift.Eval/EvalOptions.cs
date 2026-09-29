using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Sift.Eval;

/// <summary>What to evaluate.</summary>
/// <param name="Repository">Path or URL of the repository; it is cloned, never changed.</param>
/// <param name="Branch">Branch whose first-parent history holds the merges to evaluate.</param>
/// <param name="Merges">How many of the latest merges (or commits) to evaluate.</param>
internal sealed record EvalTarget(string Repository, string Branch, int Merges);

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

        Clones the repository and, for each of the latest merges on a branch, records coverage at the
        merge's first parent, lets Sift select tests for the merge, runs the selection and the full
        suite, and checks the selection against injected faults (mutants).

          --repo <path-or-url>   Repository to evaluate (required); it is cloned, never changed
          --branch <name>        Branch to walk (default: main)
          --merges <n>           Latest first-parent commits to evaluate (default: 8)
          --parallel <n>         Test processes per project when recording (default: 1)
          --mutants <n>          Changed members to mutate per merge (default: 3, 0 turns it off)
          --project <csproj>     Only use this test project (path in the repository); repeat for several
          -f, --framework <tfm>  Build, record and test only this target framework, e.g. net10.0
          --cleanup <command>    Shell command run after every test run, e.g. to remove test containers
          --out <dir>            Work and report directory (default: a new directory under the temp folder)
          -v, --verbose          Debug logs on stderr
        """;

    /// <summary>Parses the command line.</summary>
    /// <param name="args">Raw arguments.</param>
    /// <param name="options">The options when parsing succeeds.</param>
    /// <param name="error">A message for the user when parsing fails.</param>
    public static bool TryParse(ReadOnlySpan<string> args, out EvalOptions options, out string? error)
    {
        string? repository = null;
        var branch = "main";
        var merges = 8;
        var run = new RunSettings(Parallelism: 1, Mutants: 3, Projects: [], Cleanup: null, Framework: null);
        var logLevel = LogLevel.Information;
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
                    run = run with { Cleanup = value };
                    break;

                case "-f" or "--framework":
                    run = run with { Framework = value };
                    break;

                case "--project":
                    run = run with { Projects = [.. run.Projects, value] };
                    break;

                case "--merges" or "--parallel" or "--mutants":
                    if (!int.TryParse(value, CultureInfo.InvariantCulture, out var number) || number < 0 || (number == 0 && arg != "--mutants"))
                    {
                        error = $"{arg} needs a positive number, not {value}";
                        return false;
                    }

                    merges = arg == "--merges" ? number : merges;
                    run = arg switch
                    {
                        "--parallel" => run with { Parallelism = number },
                        "--mutants" => run with { Mutants = number },
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

        options = new EvalOptions(new EvalTarget(repository, branch, merges), run, output, logLevel);
        return true;
    }
}

/// <summary>Settings shared by every recording and test run of an evaluation.</summary>
/// <param name="Parallelism">Test processes per project when recording.</param>
/// <param name="Mutants">Changed members to mutate per merge.</param>
/// <param name="Projects">Test projects to use (paths in the repository); empty means all.</param>
/// <param name="Cleanup">Shell command run after every test run, or null.</param>
/// <param name="Framework">Target framework to build, record and test, or null for the projects' defaults.</param>
internal sealed record RunSettings(int Parallelism, int Mutants, IReadOnlyList<string> Projects, string? Cleanup, string? Framework);
