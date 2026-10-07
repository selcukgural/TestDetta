using System.Security.Cryptography;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Cli;

/// <summary>
/// The parts of a <c>dotnet test</c> command line that TestDetta adds or reads when it runs the team's tests the full
/// way, shared by shadow mode (<see cref="ShadowRun"/>) and the audit (<see cref="AuditRun"/>): the report options
/// that leave the user's own as they are, finding the report afterwards, and quoting for the reports.
/// </summary>
internal static class TestRunArguments
{
    /// <summary>
    /// The arguments that make a run write a report TestDetta can read (null when it can write none), and the
    /// user's own results directory if they name one. Nothing the user asked for is replaced: a VSTest run with the user's own TRX logger or
    /// results directory keeps them and the report is found there; a testing-platform run that already asks
    /// for a TRX report gets none from TestDetta (its file name is the user's to choose), and is not read.
    /// </summary>
    /// <param name="mode">Runner.</param>
    /// <param name="outputDirectory">Build output of a testing-platform project, or null.</param>
    /// <param name="results">TestDetta's folder for the report.</param>
    /// <param name="reportName">Unique report file name (testing platform).</param>
    /// <param name="userArguments">The user's arguments.</param>
    /// <param name="runDirectory">Directory the run starts in, for a relative results directory.</param>
    public static (List<string>? Arguments, string? UserResults) ReportArguments(DotnetTestMode mode, string? outputDirectory, string results, string reportName, IReadOnlyList<string> userArguments, string runDirectory)
    {
        var options = BeforeRunSettings(userArguments);
        if (mode == DotnetTestMode.VSTest)
        {
            var userResults = ValueOf(options, "--results-directory");
            List<string> arguments = HasTrxLogger(options) ? [] : ["--logger", "trx"];
            if (userResults is null)
            {
                arguments.AddRange(["--results-directory", results]);
                return (arguments, null);
            }

            // The user's folder stays; the report is found there among the files the run added.
            return (arguments, Path.GetFullPath(userResults, runDirectory));
        }

        if (outputDirectory is null || options.Any(static a => a.StartsWith("--report-trx", StringComparison.Ordinal) || a.StartsWith("--report-xunit-trx", StringComparison.Ordinal)))
        {
            return (null, null);
        }

        return TestReports.Arguments(mode, outputDirectory, results, reportName) is { Count: > 0 } platform ? (platform, null) : (null, null);
    }

    /// <summary>Report files already in the user's results directory before a run, so only the run's own are read.</summary>
    /// <param name="userResults">The user's results directory, or null.</param>
    public static HashSet<string> Snapshot(string? userResults) =>
        userResults is not null ? [.. TrxFiles(userResults)] : [];

    /// <summary>
    /// Every <c>.trx</c> file under <paramref name="folder"/>, recursively; folders that cannot be read, links and
    /// <c>.git</c> are skipped, so a repository with an unreadable or looping folder is searched without an error.
    /// </summary>
    /// <param name="folder">The folder; nothing when it does not exist.</param>
    public static IEnumerable<string> TrxFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new System.IO.Enumeration.FileSystemEnumerable<string>(folder, static (ref entry) => entry.ToFullPath(), options)
        {
            ShouldIncludePredicate = static (ref entry) => !entry.IsDirectory && entry.FileName.EndsWith(".trx", StringComparison.OrdinalIgnoreCase),
            ShouldRecursePredicate = static (ref entry) => !entry.FileName.Equals(".git", StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// When each <c>.trx</c> file under the folders was last written, to tell the reports a run wrote or rewrote
    /// from those that were there before (<see cref="Changed"/>). A file that vanishes while it is read is skipped.
    /// </summary>
    /// <param name="folders">Folders to search (<see cref="TrxFiles"/>); a folder inside another is searched once.</param>
    public static Dictionary<string, DateTime> ReportTimes(IEnumerable<string> folders)
    {
        var times = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var folder in folders.Distinct(StringComparer.Ordinal))
        {
            foreach (var file in TrxFiles(folder))
            {
                try
                {
                    times[file] = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable: not a report this run can be judged by.
                }
            }
        }

        return times;
    }

    /// <summary>The reports in <paramref name="after"/> that are new or were rewritten since <paramref name="before"/>, ordered by path.</summary>
    /// <param name="before">Report times before the run.</param>
    /// <param name="after">Report times after it.</param>
    public static List<string> Changed(Dictionary<string, DateTime> before, Dictionary<string, DateTime> after) =>
        [.. after.Where(f => !before.TryGetValue(f.Key, out var time) || time != f.Value).Select(static f => f.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// True when the arguments name a TRX logger in any spelling <c>dotnet test</c> takes: <c>--logger trx</c>,
    /// <c>-l trx</c>, <c>--logger:trx</c>, <c>--logger=trx</c> (and <c>-l:</c>, <c>-l=</c>), with or without its
    /// parameters (<c>trx;LogFileName=…</c>), by its friendly name or its URI.
    /// </summary>
    /// <param name="options">The user's arguments before a <c>--</c>.</param>
    public static bool HasTrxLogger(List<string> options)
    {
        for (var i = 0; i < options.Count; i++)
        {
            var value = options[i] is "--logger" or "-l" && i + 1 < options.Count ? options[i + 1] : InlineValue(options[i], "--logger") ?? InlineValue(options[i], "-l");
            if (value is not null && (value.StartsWith("trx", StringComparison.OrdinalIgnoreCase) || value.Contains("TrxLogger", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The value of <paramref name="option"/> written in one argument (<c>--option=value</c>, <c>--option:value</c>), or null.</summary>
    /// <param name="argument">One argument.</param>
    /// <param name="option">Option name.</param>
    private static string? InlineValue(string argument, string option) =>
        argument.Length > option.Length + 1 && argument.StartsWith(option, StringComparison.Ordinal) && argument[option.Length] is '=' or ':'
            ? argument[(option.Length + 1)..]
            : null;

    /// <summary>
    /// The arguments without the given options, in every spelling (<c>--option value</c>, <c>--option=value</c>,
    /// <c>--option:value</c>, and a flag on its own). Only the arguments before a <c>--</c> are touched; run settings
    /// after it stay.
    /// </summary>
    /// <param name="arguments">A command line.</param>
    /// <param name="withValue">Options that take a value.</param>
    /// <param name="flags">Options without a value.</param>
    public static List<string> WithoutOptions(IReadOnlyList<string> arguments, IReadOnlyCollection<string> withValue, IReadOnlyCollection<string> flags)
    {
        var result = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument == "--")
            {
                result.AddRange(arguments.Skip(i));
                break;
            }

            if (flags.Contains(argument))
            {
                continue;
            }

            if (withValue.Contains(argument))
            {
                i++;
                continue;
            }

            if (withValue.Any(option => InlineValue(argument, option) is not null))
            {
                continue;
            }

            result.Add(argument);
        }

        return result;
    }

    /// <summary>Reads the report of a run, wherever it went.</summary>
    /// <param name="mode">Runner.</param>
    /// <param name="treeRoot">Root searched for a testing-platform report.</param>
    /// <param name="reportName">Its unique file name.</param>
    /// <param name="results">TestDetta's folder for the report.</param>
    /// <param name="userResults">The user's results directory, or null.</param>
    /// <param name="before">Reports that were in the user's directory before the run.</param>
    public static TrxOutcome? Collect(DotnetTestMode mode, string treeRoot, string reportName, string results, string? userResults, HashSet<string> before)
    {
        TestReports.Collect(mode, treeRoot, reportName, results);
        if (userResults is not null && Directory.Exists(userResults))
        {
            Directory.CreateDirectory(results);
            foreach (var file in TrxFiles(userResults).Where(f => !before.Contains(f)).ToList())
            {
                File.Copy(file, Path.Combine(results, Guid.NewGuid().ToString("N") + ".trx"));
            }
        }

        return TrxReport.Read(results);
    }

    /// <summary>The user's arguments before a <c>--</c>; those after it are run settings.</summary>
    /// <param name="userArguments">The user's arguments.</param>
    public static List<string> BeforeRunSettings(IReadOnlyList<string> userArguments) => [.. userArguments.TakeWhile(static a => a != "--")];

    /// <summary>The value after <paramref name="option"/> (or in its <c>=</c> or <c>:</c> form), or null.</summary>
    /// <param name="options">Arguments.</param>
    /// <param name="option">Option name.</param>
    public static string? ValueOf(List<string> options, string option)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i] == option && i + 1 < options.Count)
            {
                return options[i + 1];
            }

            foreach (var separator in (ReadOnlySpan<char>)['=', ':'])
            {
                if (options[i].StartsWith(option + separator, StringComparison.Ordinal))
                {
                    return options[i][(option.Length + 1)..];
                }
            }
        }

        return null;
    }

    /// <summary><paramref name="extra"/> placed before a <c>--</c> in <paramref name="arguments"/>, or at the end.</summary>
    /// <param name="arguments">A command line.</param>
    /// <param name="extra">Options to add.</param>
    public static List<string> Inserted(IReadOnlyList<string> arguments, IReadOnlyList<string> extra)
    {
        var separator = arguments.ToList().IndexOf("--");
        List<string> result = [.. arguments];
        result.InsertRange(separator < 0 ? result.Count : separator, extra);
        return result;
    }

    /// <summary>A command line for the reports, each argument quoted when it holds a space or a quote.</summary>
    /// <param name="arguments">The arguments.</param>
    public static string CommandLine(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quoted));

    /// <summary>Characters that make a shell read an argument as something else, or split it.</summary>
    private static readonly System.Buffers.SearchValues<char> ShellSpecial = System.Buffers.SearchValues.Create(" \t\"';&|<>()$`*?[]{}\\");

    /// <summary>
    /// An argument as a shell would need it: in double quotes when it holds a space, a quote or a character the shell
    /// would act on (<c>;</c>, <c>&amp;</c>, <c>|</c>, redirections, globs, variables), with <c>"</c>, <c>\</c>,
    /// <c>$</c> and the backtick escaped inside them, so a command copied from a report runs as it ran.
    /// </summary>
    /// <param name="argument">The argument.</param>
    public static string Quoted(string argument)
    {
        if (argument.Length > 0 && !argument.AsSpan().ContainsAny(ShellSpecial))
        {
            return argument;
        }

        var escaped = new System.Text.StringBuilder(argument.Length + 2).Append('"');
        foreach (var c in argument)
        {
            if (c is '"' or '\\' or '$' or '`')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.Append('"').ToString();
    }

    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    /// <param name="path">File.</param>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
