using System.Diagnostics;
using System.Text.Json;
using Sift.Core;

namespace Sift.Eval;

/// <summary>
/// dotnet-tia as one more selector (docs/design/tia-comparison.md): runs <c>dotnet tia analyze</c>
/// on the clone with the synthetic change applied and turns its report into decisions per test
/// project, in the terms the evaluation judges Sift's selection in.
/// </summary>
internal static class TiaSelector
{
    /// <summary>Longest one analysis may take; a cold graph of a large solution takes minutes.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>What one analysis returned.</summary>
    /// <param name="Tests">Decision per test project of the graph.</param>
    /// <param name="FullRun">True when dotnet-tia ran everything, with <paramref name="Reason"/>.</param>
    /// <param name="Reason">Why it ran everything; null for a selective run.</param>
    /// <param name="Seconds">dotnet-tia's own elapsed time.</param>
    internal sealed record TiaResult(IReadOnlyList<TestProjectSelection> Tests, bool FullRun, string? Reason, double Seconds);

    /// <summary>
    /// Runs <c>analyze</c> against <paramref name="sha"/> in <paramref name="repoRoot"/>. Its cache goes
    /// to <c>.tia/</c> in the clone, which is excluded through <c>.git/info/exclude</c> so that Sift, which
    /// counts untracked files as changes, never sees it.
    /// </summary>
    /// <param name="tiaCli">Path of <c>Tia.Cli.dll</c>.</param>
    /// <param name="repoRoot">Clone with the change applied.</param>
    /// <param name="sha">Commit the change is made on.</param>
    /// <param name="solution">Solution to analyse, relative to the clone; null to let dotnet-tia discover it.</param>
    /// <param name="graph">Sift's project graph of the clone, to match projects by path.</param>
    /// <param name="log">Receives dotnet-tia's stderr.</param>
    /// <exception cref="InvalidOperationException">dotnet-tia failed or wrote no report.</exception>
    public static TiaResult Analyze(string tiaCli, string repoRoot, string sha, string? solution, ProjectGraph graph, TextWriter log)
    {
        ExcludeCache(repoRoot);
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Filter whenever a project's selection can be expressed, as Sift does, instead of running
        // projects unfiltered above dotnet-tia's default 60 %: the comparison is about what is selected.
        foreach (var argument in (string[])[tiaCli, "analyze", "--base", sha, "--json", "--path", repoRoot, "--coverage-threshold", "1"])
        {
            psi.ArgumentList.Add(argument);
        }

        if (solution is not null)
        {
            psi.ArgumentList.Add("--solution");
            psi.ArgumentList.Add(Path.Combine(repoRoot, solution));
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet-tia.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"dotnet-tia did not finish within {Timeout.TotalMinutes:0} minutes.");
        }

        log.Write(stderr.GetAwaiter().GetResult());
        var json = stdout.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || json.IndexOf('{', StringComparison.Ordinal) is not (>= 0 and var start))
        {
            throw new InvalidOperationException($"dotnet-tia exited with {process.ExitCode} and no report.");
        }

        return FromReport(json[start..], graph);
    }

    /// <summary>
    /// Decisions from an <c>analyze --json</c> report. A full run selects every test project. A listed
    /// project runs completely when unfiltered and otherwise runs the classes of its tests (a test's
    /// fully qualified name is <c>Namespace.Class.Method</c>, nested classes joined with <c>+</c>, as
    /// runtime names are). A test project the report does not list runs nothing.
    /// </summary>
    /// <param name="json">The report.</param>
    /// <param name="graph">Sift's project graph, to match projects by path.</param>
    internal static TiaResult FromReport(string json, ProjectGraph graph)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var fullRun = root.GetProperty("mode").GetString() == "full";
        var reason = fullRun && root.TryGetProperty("fullRunReasons", out var reasons) && reasons.GetArrayLength() > 0
            ? string.Join("; ", reasons.EnumerateArray().Select(static r => r.GetString()))
            : null;
        var seconds = root.TryGetProperty("elapsedSeconds", out var elapsed) ? elapsed.GetDouble() : 0;
        var listed = new Dictionary<string, JsonElement>(Paths.Comparer);
        if (root.TryGetProperty("projects", out var projects))
        {
            foreach (var project in projects.EnumerateArray())
            {
                listed[Paths.Normalize(project.GetProperty("projectPath").GetString()!)] = project;
            }
        }

        var tests = new List<TestProjectSelection>();
        foreach (var node in graph.Projects.Where(static p => p.IsTestProject))
        {
            if (fullRun)
            {
                tests.Add(new TestProjectSelection(node, TestRunMode.All, [], $"dotnet-tia ran everything: {reason}"));
                continue;
            }

            if (!listed.TryGetValue(node.Path, out var project))
            {
                tests.Add(new TestProjectSelection(node, TestRunMode.Skipped, [], "dotnet-tia selected nothing"));
                continue;
            }

            if (!project.GetProperty("filtered").GetBoolean())
            {
                tests.Add(new TestProjectSelection(node, TestRunMode.All, [], "dotnet-tia ran the project unfiltered"));
                continue;
            }

            var classes = project.GetProperty("tests").EnumerateArray()
                .Select(static t => t.GetString()!)
                .Select(static name => name[..Math.Max(0, name.LastIndexOf('.'))])
                .Where(static name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            tests.Add(classes.Count == 0
                ? new TestProjectSelection(node, TestRunMode.Skipped, [], "dotnet-tia selected nothing")
                : new TestProjectSelection(node, TestRunMode.Filtered, classes, $"dotnet-tia selected {classes.Count} class(es)"));
        }

        return new TiaResult(tests, fullRun, reason, seconds);
    }

    /// <summary>Adds <c>/.tia/</c> to the clone's <c>.git/info/exclude</c> once.</summary>
    /// <param name="repoRoot">Clone.</param>
    private static void ExcludeCache(string repoRoot)
    {
        var exclude = Path.Combine(repoRoot, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        if (File.Exists(exclude) && File.ReadLines(exclude).Contains("/.tia/"))
        {
            return;
        }

        File.AppendAllText(exclude, Environment.NewLine + "/.tia/" + Environment.NewLine);
    }
}
