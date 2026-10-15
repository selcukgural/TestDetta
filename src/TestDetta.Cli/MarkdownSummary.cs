using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>What <see cref="MarkdownSummary"/> reports.</summary>
/// <param name="Analysis">Finished analysis.</param>
/// <param name="BaseRef">Ref the changes were compared with.</param>
/// <param name="Results">Results of the test runs, or null when nothing ran (no tests needed, or a dry run).</param>
internal sealed record SummaryInput(AnalysisResult Analysis, string BaseRef, IReadOnlyList<TestRunResult>? Results);

/// <summary>
/// Writes the test selection as Markdown for a CI job summary. Every value that comes from the
/// repository (paths, class names, reasons) goes into a code span, so file names in a pull request
/// cannot inject links, images, HTML or @mentions into the page.
/// </summary>
internal static class MarkdownSummary
{
    /// <summary>Writes the summary.</summary>
    /// <param name="input">What to report.</param>
    /// <param name="writer">Destination, usually the file named by <c>GITHUB_STEP_SUMMARY</c>.</param>
    public static void Write(SummaryInput input, TextWriter writer)
    {
        var analysis = input.Analysis;
        var tests = analysis.Tests;
        var run = tests.Count(static t => t.Mode != TestRunMode.Skipped);
        var skipped = tests.Count - run;

        writer.WriteLine("### TestDetta test selection");
        writer.WriteLine();
        writer.WriteLine($"{analysis.ChangedFiles.Count} changed file(s) compared with {Code(input.BaseRef)}.");
        writer.WriteLine();
        if (ResultWriter.LicenseLine(analysis.License, analysis.LicenseEnforced) is not null)
        {
            // The message holds the repository's identity, which comes from its remote: a code span.
            writer.WriteLine($"> **License:** {Code(analysis.License!.Message)}");
            writer.WriteLine();
        }
        foreach (var drift in analysis.Drifts)
        {
            writer.WriteLine(ResultWriter.DriftLine(drift));
            writer.WriteLine();
        }

        // Paths come from the pull request: code spans, so a file name cannot inject links or mentions.
        if (ResultWriter.NotBuildInputsLine(analysis.NotBuildInputs, Code) is { } notBuilt)
        {
            writer.WriteLine(notBuilt);
            writer.WriteLine();
        }

        if (tests.Count == 0)
        {
            writer.WriteLine("No test project is affected; no tests ran.");
            writer.WriteLine();
            return;
        }

        if (ResultWriter.TeamConfiguration(tests, analysis.RepoRoot) is { Count: > 0 } kept)
        {
            // The team's own values, filters included, go into code spans.
            writer.WriteLine("Your own test configuration is kept in every run TestDetta starts:");
            writer.WriteLine();
            foreach (var (project, configuration) in kept)
            {
                writer.WriteLine($"- {Code(project)}: {Code(configuration)}");
            }

            writer.WriteLine();
        }

        writer.WriteLine($"- **{run}** of {tests.Count} affected test project(s) ran, **{skipped}** skipped.");
        if (Durations.Summarize(analysis.Plan) is { } total)
        {
            writer.WriteLine($"- {total}");
        }
        if (input.Results is { } results)
        {
            var failed = results.Count(static r => r.ExitCode != 0);
            writer.WriteLine(failed == 0 ? "- All selected tests passed." : $"- **{failed}** test project(s) failed.");
        }

        writer.WriteLine();
        foreach (var test in tests)
        {
            WriteProject(input, test, writer);
        }
    }

    /// <summary>One section per affected test project: outcome, scope, reason, and the selected classes.</summary>
    /// <param name="input">What to report.</param>
    /// <param name="test">Decision for the project.</param>
    /// <param name="writer">Destination.</param>
    private static void WriteProject(SummaryInput input, TestProjectSelection test, TextWriter writer)
    {
        var path = Path.GetRelativePath(input.Analysis.RepoRoot, test.Project.Path).Replace('\\', '/');
        var result = input.Results?.FirstOrDefault(r => ReferenceEquals(r.Invocation.Selection, test));
        var outcome = (test.Mode, result) switch
        {
            (TestRunMode.Skipped, _) => "skipped",
            (_, null) => "not run",
            (_, { ExitCode: 0 }) => "passed",
            _ => "**failed**",
        };

        var scope = test.Mode switch
        {
            TestRunMode.All => result is { RanUnfiltered: true } ? "all tests (the filter matched nothing)" : "all tests",
            TestRunMode.Filtered => $"{test.TestClasses.Count} test class(es)",
            _ => "none",
        };

        var estimate = test.Estimate is { } e ? $", recorded time {Durations.Describe(e)}" : string.Empty;
        writer.WriteLine($"- {Code(path)}: {outcome}, {scope}{estimate}");
        writer.WriteLine($"  - why: {Code(test.Reason)}");

        if (test.TestClasses.Count == 0)
        {
            return;
        }

        // Each selected class with what selected it: the changed code it ran (and the map), or the
        // chain of names that leads from it to the change.
        var evidence = test.Decisions.Where(static d => d.Selected).ToDictionary(static d => d.TestClass, static d => d.Evidence, StringComparer.Ordinal);
        writer.WriteLine("  - classes:");
        foreach (var testClass in test.TestClasses)
        {
            writer.WriteLine(evidence.TryGetValue(testClass, out var why) ? $"    - {Code(testClass)}: {Code(why)}" : $"    - {Code(testClass)}");
        }

        var skippedClasses = test.Decisions.Count(static d => !d.Selected);
        if (skippedClasses > 0)
        {
            writer.WriteLine($"  - skipped: {skippedClasses} class(es) that ran none of the changed code and name none of it");
        }
    }

    /// <summary>
    /// A Markdown code span whose content is shown literally. The fence is one backtick longer than
    /// the longest backtick run in <paramref name="value"/>, and line breaks become spaces, so no
    /// value can close the span early.
    /// </summary>
    /// <param name="value">Text from the repository.</param>
    internal static string Code(string value)
    {
        var text = value.ReplaceLineEndings(" ");
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', longest + 1);

        // A space on each side keeps a leading or trailing backtick from merging with the fence.
        return longest == 0 ? $"{fence}{text}{fence}" : $"{fence} {text} {fence}";
    }

    /// <summary>
    /// <see cref="Code"/> for a table cell: a pipe ends a cell even inside a code span, so each is escaped as
    /// <c>\|</c>, which GitHub's tables turn back into a pipe inside the span.
    /// </summary>
    /// <param name="value">Text from the repository.</param>
    internal static string TableCode(string value) => Code(value).Replace("|", "\\|", StringComparison.Ordinal);
}
