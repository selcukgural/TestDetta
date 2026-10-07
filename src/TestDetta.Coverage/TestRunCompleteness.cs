namespace TestDetta.Coverage;

/// <summary>
/// Whether a <c>dotnet test</c> run completed, so that a class missing from its failures passed rather than never
/// ran. Shared by <c>td audit</c> and the corpus evaluator, which judge mutants by the same rules
/// (docs/design/audit.md, "Judging"; docs/evaluation.md): a run that does not complete must never count as passed.
/// </summary>
internal static class TestRunCompleteness
{
    /// <summary>
    /// Variables a judged run gets last, over every other: the SDK and VSTest write their messages in English, whatever
    /// the machine's language, so the lines that say a test host crashed can be recognized. The report's own outcome
    /// and the exit code are read as well, so a message in another language does not hide a crash either.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["VSLANG"] = "1033",
    };

    /// <summary>Lines with which VSTest says the test host died, so the report is partial (in English, see <see cref="English"/>).</summary>
    private static readonly string[] CrashLines = ["Test host process crashed", "Test Run Aborted", "The active test run was aborted"];

    /// <summary>Overall outcomes of a TRX report that mean the run did not complete.</summary>
    private static readonly string[] IncompleteOutcomes = ["Aborted", "Error", "Timeout"];

    /// <summary>True when one of <paramref name="lines"/> (a run's console output) says the test host crashed or the run was aborted.</summary>
    /// <param name="lines">The run's output, line by line.</param>
    public static bool HasCrashLine(IEnumerable<string> lines) =>
        lines.Any(static l => CrashLines.Any(c => l.Contains(c, StringComparison.Ordinal)));

    /// <summary>
    /// Why a run that wrote a report did not complete, or null when it did: the log says the test host crashed or the
    /// run was aborted; the report says so itself (<paramref name="reportProblem"/>); or the run failed (a non-zero exit
    /// code) without a failed test, as a cleanup failure at the run's level, a testing-platform run in which no test ran
    /// (exit code 8), or a crash whose message was not recognized does. A class that never ran then must not count as
    /// passed.
    /// </summary>
    /// <param name="exitCode">The run's exit code.</param>
    /// <param name="crashLine">True when the log holds a line that says the test host crashed.</param>
    /// <param name="reportProblem">What the report says went wrong (<see cref="ReportProblem"/>), or null.</param>
    /// <param name="failedClasses">Failed classes in the report.</param>
    public static string? Problem(int exitCode, bool crashLine, string? reportProblem, int failedClasses) =>
        crashLine ? "the test host crashed"
        : reportProblem is not null ? reportProblem
        : exitCode != 0 && failedClasses == 0 ? $"it failed (exit code {exitCode}) without a failed test"
        : null;

    /// <summary>
    /// What the reports in <paramref name="folder"/> say went wrong with the run as a whole, or null: a TRX report whose
    /// overall outcome is <c>Aborted</c>, <c>Error</c> or <c>Timeout</c>, or that holds a run message saying the run was
    /// aborted or the test host crashed (VSTest writes it there while the outcome stays <c>Failed</c>; in English, since
    /// the runs are made in English; other run messages of outcome <c>Error</c> are adapters' lines such as xUnit's
    /// "[FAIL]" and mean nothing); an NUnit result whose run was cancelled. A crash whose message is not recognized is
    /// still seen: by a failing exit code without a failed test (<see cref="Problem"/>), or, for a whole project's run,
    /// by test classes missing from its report.
    /// </summary>
    /// <param name="folder">The run's report folder.</param>
    public static string? ReportProblem(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!file.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            System.Xml.Linq.XDocument document;
            try
            {
                document = System.Xml.Linq.XDocument.Load(file);
            }
            catch (System.Xml.XmlException)
            {
                return $"its test report {Path.GetFileName(file)} could not be read";
            }

            if (document.Root is { Name.LocalName: "test-run" } nunit)
            {
                if (string.Equals(nunit.Attribute("label")?.Value, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return "the test run was cancelled";
                }

                continue;
            }

            if (document.Root is not { Name.LocalName: "TestRun" } run)
            {
                continue;
            }

            var ns = run.Name.Namespace;
            var summary = run.Element(ns + "ResultSummary");
            if (summary?.Attribute("outcome")?.Value is { } overall && IncompleteOutcomes.Contains(overall, StringComparer.OrdinalIgnoreCase))
            {
                return $"the test run ended as {overall}";
            }

            var aborted = summary?.Element(ns + "RunInfos")?.Elements(ns + "RunInfo")
                .Select(i => i.Element(ns + "Text")?.Value.Trim() ?? string.Empty)
                .FirstOrDefault(static text => CrashLines.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase)));
            if (aborted is not null)
            {
                var first = aborted.Split('\n', 2)[0].Trim();
                return $"the test run was aborted: {(first.Length > 200 ? first[..200] + "…" : first)}";
            }
        }

        return null;
    }
}
