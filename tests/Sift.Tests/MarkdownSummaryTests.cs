using Sift.Cli;
using Sift.Core;

namespace Sift.Tests;

public sealed class MarkdownSummaryTests
{
    [Theory]
    [InlineData("tests/A.Tests", "`tests/A.Tests`")]
    [InlineData("a`b", "`` a`b ``")]
    [InlineData("``x", "``` ``x ```")]
    [InlineData("line\nbreak", "`line break`")]
    public void Code_span_cannot_be_closed_by_its_content(string value, string expected)
    {
        Assert.Equal(expected, MarkdownSummary.Code(value));
    }

    [Fact]
    public void Repository_values_end_up_inside_code_spans()
    {
        // A pull request can name a file or class anything; none of it may render as Markdown or HTML.
        const string hostile = "[x](https://evil.example) <img src=x> @org/team `";
        var project = new ProjectNode($"/repo/tests/{hostile}/T.csproj", "/repo/tests", true, []);
        var selection = new TestProjectSelection(project, TestRunMode.Filtered, [hostile], hostile);
        var analysis = new AnalysisResult("/repo", ["f.cs"], null!, null!, new TestPlan([selection], null, 0));

        var writer = new StringWriter();
        MarkdownSummary.Write(new SummaryInput(analysis, "origin/main", null), writer);

        // Remove every code span (a backtick run, content, the same run); nothing hostile may remain outside.
        var outside = System.Text.RegularExpressions.Regex.Replace(writer.ToString(), "(?<!`)(`+)(?!`).+?(?<!`)\\1(?!`)", "");

        Assert.Contains("[x]", writer.ToString());
        Assert.DoesNotContain("[x]", outside);
        Assert.DoesNotContain("<img", outside);
        Assert.DoesNotContain("@org", outside);
    }

    [Theory]
    [InlineData(12.34, "12.3 s")]
    [InlineData(250, "4 m 10 s")]
    [InlineData(3900, "1 h 5 m")]
    public void Durations_read_naturally(double seconds, string expected)
    {
        Assert.Equal(expected, Durations.Format(seconds));
    }

    [Fact]
    public void Plan_summary_names_projects_left_out_of_the_estimate()
    {
        var plan = new TestPlan([], new TimeEstimate(250, 12.34), ProjectsWithoutTimings: 1);

        Assert.Equal(
            "Recorded test time: 12.3 s of 4 m 10 s (95% saved); 1 test project(s) without timings are not counted.",
            Durations.Summarize(plan));
        Assert.Null(Durations.Summarize(new TestPlan([], null, 2)));
    }

    [Fact]
    public void Record_accepts_several_projects_and_test_accepts_a_summary_file()
    {
        Assert.True(CliOptions.TryParse(["record", "--project", "a.csproj", "--project", "b.csproj"], "/r", out var record, out _));
        Assert.Equal(["a.csproj", "b.csproj"], record.Record.Projects);

        Assert.True(CliOptions.TryParse(["record", "--parallel", "4"], "/r", out var parallel, out _));
        Assert.Equal(4, parallel.Record.Parallelism);
        Assert.False(CliOptions.TryParse(["record", "--parallel", "0"], "/r", out _, out _));

        Assert.True(CliOptions.TryParse(["test", "--summary-file", "/tmp/s.md"], "/r", out var test, out _));
        Assert.Equal("/tmp/s.md", test.Test.SummaryFile);
    }
}
