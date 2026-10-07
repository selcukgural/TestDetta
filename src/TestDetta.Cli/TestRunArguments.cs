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
            var hasTrx = options.Select((a, i) => (a, i)).Any(p => p.a == "--logger" && p.i + 1 < options.Count && options[p.i + 1].StartsWith("trx", StringComparison.OrdinalIgnoreCase));
            List<string> arguments = hasTrx ? [] : ["--logger", "trx"];
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
        userResults is not null && Directory.Exists(userResults)
            ? Directory.GetFiles(userResults, "*.trx", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal)
            : [];

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
            foreach (var file in Directory.GetFiles(userResults, "*.trx", SearchOption.AllDirectories).Where(f => !before.Contains(f)))
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

    /// <summary>An argument as a shell would need it: in double quotes when it holds a space or a quote.</summary>
    /// <param name="argument">The argument.</param>
    public static string Quoted(string argument) =>
        argument.Length > 0 && !argument.AsSpan().ContainsAny(" \t\"'") ? argument : $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    /// <param name="path">File.</param>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
