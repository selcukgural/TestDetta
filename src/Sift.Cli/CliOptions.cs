using Microsoft.Extensions.Logging;

namespace Sift.Cli;

/// <summary>Parsed command line of <c>sift affected</c>.</summary>
/// <param name="BaseRef">Git ref to compare against.</param>
/// <param name="RepoPath">Any path inside the repository.</param>
/// <param name="TestsOnly">Print only test projects.</param>
/// <param name="Format">Output format for stdout.</param>
/// <param name="LogLevel">Minimum level of the diagnostic log written to stderr.</param>
internal sealed record CliOptions(string BaseRef, string RepoPath, bool TestsOnly, OutputFormat Format, LogLevel LogLevel)
{
    /// <summary>Help text printed for <c>--help</c> and after a usage error.</summary>
    public const string Usage = """
        Usage: sift affected [options]

          --base <ref>         Git ref to compare against (default: origin/main)
          --repo <path>        Any path inside the repository (default: current directory)
          --tests-only         Only list affected test projects
          --format <format>    text (default), json, or paths (one .csproj per line)
          -v, --verbose        Write debug logs to stderr
          --log-level <level>  trace, debug, information, warning (default), error or none
        """;

    /// <summary>Parses the arguments that follow the <c>affected</c> command.</summary>
    /// <param name="args">Arguments after the command name.</param>
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
        options = new CliOptions("origin/main", currentDirectory, TestsOnly: false, OutputFormat.Text, LogLevel.Warning);
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "--tests-only")
            {
                options = options with { TestsOnly = true };
                continue;
            }

            if (arg is "-v" or "--verbose")
            {
                options = options with { LogLevel = LogLevel.Debug };
                continue;
            }

            if (arg is not ("--base" or "--repo" or "--format" or "--log-level"))
            {
                error = $"Unknown option: {arg}";
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
                    options = options with { BaseRef = value };
                    break;

                case "--repo":
                    options = options with { RepoPath = value };
                    break;

                case "--format":
                    if (!Enum.TryParse<OutputFormat>(value, ignoreCase: true, out var format))
                    {
                        error = $"Unknown format: {value}";
                        return false;
                    }

                    options = options with { Format = format };
                    break;

                case "--log-level":
                    if (!Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level))
                    {
                        error = $"Unknown log level: {value}";
                        return false;
                    }

                    options = options with { LogLevel = level };
                    break;
            }
        }

        return true;
    }
}
