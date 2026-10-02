using System.Diagnostics;
using System.Text.Json;
using Sift.Core;

namespace Sift.Eval;

/// <summary>
/// dotnet-affected as one more selector: the free project-level tool .NET teams use. It is run on
/// the clone with the synthetic change applied (without <c>--from</c>, version 6 compares the working
/// tree with HEAD), and every affected test project runs whole, as <c>dotnet test affected.proj</c> would.
/// </summary>
internal static class AffectedSelector
{
    /// <summary>Exit code for "nothing changed and nothing is affected".</summary>
    private const int NothingAffected = 166;

    /// <summary>Longest one run may take; it evaluates every project with MSBuild.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Runs dotnet-affected on <paramref name="repoRoot"/>. The report goes to a temporary directory
    /// outside the clone: version 6 counts files it finds in the clone, its own output included, as changes.
    /// </summary>
    /// <param name="affectedCli">The <c>dotnet-affected</c> executable.</param>
    /// <param name="repoRoot">Clone with the change applied, at the commit it is made on.</param>
    /// <param name="graph">Sift's project graph of the clone, to match projects by path.</param>
    /// <param name="log">Receives the tool's output.</param>
    /// <exception cref="InvalidOperationException">The tool failed.</exception>
    public static IReadOnlyList<TestProjectSelection> Analyze(string affectedCli, string repoRoot, ProjectGraph graph, TextWriter log)
    {
        var output = Directory.CreateTempSubdirectory("sift-affected-").FullName;
        try
        {
            var psi = new ProcessStartInfo(affectedCli)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in (string[])["--repository-path", repoRoot, "--format", "json", "--output-dir", output])
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet-affected.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"dotnet-affected did not finish within {Timeout.TotalMinutes:0} minutes.");
            }

            log.Write(stdout.GetAwaiter().GetResult());
            log.Write(stderr.GetAwaiter().GetResult());
            if (process.ExitCode == NothingAffected)
            {
                return FromReport("[]", graph);
            }

            var report = Path.Combine(output, "affected.json");
            if (process.ExitCode != 0 || !File.Exists(report))
            {
                throw new InvalidOperationException($"dotnet-affected exited with {process.ExitCode} and no report.");
            }

            return FromReport(File.ReadAllText(report), graph);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>
    /// Decisions from an <c>affected.json</c> report (an array of <c>{ Name, FilePath }</c>): a test
    /// project in it runs whole, any other runs nothing.
    /// </summary>
    /// <param name="json">The report.</param>
    /// <param name="graph">Sift's project graph.</param>
    internal static IReadOnlyList<TestProjectSelection> FromReport(string json, ProjectGraph graph)
    {
        using var document = JsonDocument.Parse(json);
        var affected = new HashSet<string>(Paths.Comparer);
        foreach (var project in document.RootElement.EnumerateArray())
        {
            affected.Add(Paths.Normalize(project.GetProperty("FilePath").GetString()!));
        }

        return
        [
            .. graph.Projects.Where(static p => p.IsTestProject).Select(p => affected.Contains(p.Path)
                ? new TestProjectSelection(p, TestRunMode.All, [], "dotnet-affected: affected")
                : new TestProjectSelection(p, TestRunMode.Skipped, [], "dotnet-affected: not affected")),
        ];
    }
}
