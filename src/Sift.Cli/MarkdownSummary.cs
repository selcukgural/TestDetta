using Sift.Core;

namespace Sift.Cli;

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

        writer.WriteLine("### Sift test selection");
        writer.WriteLine();
        writer.WriteLine($"{analysis.ChangedFiles.Count} changed file(s) compared with {Code(input.BaseRef)}.");
        writer.WriteLine();

        if (tests.Count == 0)
        {
            writer.WriteLine("No test project is affected; no tests ran.");
            writer.WriteLine();
            return;
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

        writer.WriteLine("  - classes:");
        foreach (var testClass in test.TestClasses)
        {
            writer.WriteLine($"    - {Code(testClass)}");
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
}
