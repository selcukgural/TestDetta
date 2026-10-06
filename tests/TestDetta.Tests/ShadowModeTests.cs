using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// <c>td test --shadow</c> (docs/design/shadow-mode.md) on a real repository: every test runs, the exit code is
/// that run's, and each failed class the selection left out is rerun before it is called a miss. Slow: builds
/// and runs the tests several times, and once on the base commit.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ShadowModeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public ShadowModeTests()
    {
        // A flaky class fails the first time it runs in a process tree and passes afterwards: a marker file per repository.
        var marker = Path.Combine(_repo.Root, ".testdetta", "flaky-marker").Replace("\\", "\\\\", StringComparison.Ordinal);
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\nTestResults/\n");
        _repo.Write("src/Shop/Shop.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Shop/Calc.cs", """
            namespace Shop;
            public class Calc { public decimal Tax(decimal net) => net * 0.2m; }
            public class Discount { public decimal Apply(decimal price) => price - 1m; }
            """);
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/Tests.cs", $$"""
            using Xunit;
            namespace Shop.Tests;
            public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2m, new Calc().Tax(10m)); }
            public class DiscountTests { [Fact] public void Discounts() => Assert.Equal(9m, new Discount().Apply(10m)); }
            public class FlakyTests
            {
                [Fact]
                public void FailsOnce()
                {
                    var marker = "{{marker}}";
                    var first = !System.IO.File.Exists(marker);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(marker)!);
                    System.IO.File.WriteAllText(marker, "ran");
                    Assert.False(first);
                }
            }
            public class BrokenTests { [Fact] [Trait("Category", "Broken")] public void AlwaysFails() => Assert.Fail("broken before the change"); }
            """);
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Every_failure_the_selection_left_out_is_classified()
    {
        // The map says nothing ran Discount.Apply, as a stale map might: the selection leaves DiscountTests out.
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.FlakyTests"] = [],
            ["Shop.Tests.BrokenTests"] = [],
        });
        _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.3m");
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        var summary = Path.Combine(_repo.Root, ".testdetta", "summary.md");
        var stdout = new StringWriter();

        var exit = CliApp.Run(["test", "--shadow", "--repo", _repo.Root, "--base", "HEAD", "--summary-file", summary], stdout, new StringWriter());

        Assert.True(exit == 3, stdout.ToString());
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        Assert.True(report.GetProperty("compared").GetBoolean());
        var classes = report.GetProperty("projects").EnumerateArray().Single().GetProperty("classes").EnumerateArray()
            .ToDictionary(static c => c.GetProperty("name").GetString()!, static c => c);
        Assert.Equal("caught", classes["Shop.Tests.TaxTests"].GetProperty("verdict").GetString());
        Assert.Equal("miss", classes["Shop.Tests.DiscountTests"].GetProperty("verdict").GetString());
        Assert.Equal("flaky", classes["Shop.Tests.FlakyTests"].GetProperty("verdict").GetString());
        Assert.Equal("pre-existing", classes["Shop.Tests.BrokenTests"].GetProperty("verdict").GetString());
        Assert.Contains("passes alone on the base commit", classes["Shop.Tests.DiscountTests"].GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, report.GetProperty("totals").GetProperty("misses").GetInt32());
        Assert.StartsWith("Would have run 1 of 4 test classes", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Contains("1 MISS(ES)", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);

        var reproduction = report.GetProperty("reproduction");
        Assert.Equal(64, reproduction.GetProperty("testDettaSha256").GetString()!.Length);
        Assert.Equal("tests/Shop.Tests/Shop.Tests.csproj", reproduction.GetProperty("maps").EnumerateArray().Single().GetProperty("testProject").GetString());
        Assert.StartsWith("td affected --base ", reproduction.GetProperty("command").GetString(), StringComparison.Ordinal);

        var markdown = File.ReadAllText(summary);
        Assert.Contains("### TestDetta shadow run", markdown, StringComparison.Ordinal);
        Assert.Contains("**Misses** (1)", markdown, StringComparison.Ordinal);
        Assert.Contains("`Shop.Tests.DiscountTests`", markdown, StringComparison.Ordinal);

        // The base commit's worktree is gone again.
        Assert.DoesNotContain("testdetta-shadow", Worktrees(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_analysis_still_runs_everything_with_the_users_filter()
    {
        var stdout = new StringWriter();
        _repo.Write(".testdetta/flaky-marker", "ran");

        // A base ref that does not exist makes the analysis fail; BrokenTests is outside the user's own filter.
        var exit = CliApp.Run(["test", "--shadow", "--repo", _repo.Root, "--base", "no-such-ref", "--", "--filter", "Category!=Broken"], stdout, new StringWriter());

        Assert.True(exit == 0, stdout.ToString());
        Assert.Contains("every test still runs", stdout.ToString(), StringComparison.Ordinal);
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        Assert.False(report.GetProperty("compared").GetBoolean());
        Assert.StartsWith("No shadow result: the analysis failed", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        var classes = report.GetProperty("projects").EnumerateArray().Single().GetProperty("classes").EnumerateArray().Select(static c => c.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("Shop.Tests.BrokenTests", classes);
        Assert.Contains("Shop.Tests.TaxTests", classes);
    }

    [Fact]
    public void Without_classification_a_left_out_failure_counts_as_a_miss_until_checked()
    {
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.FlakyTests"] = [],
            ["Shop.Tests.BrokenTests"] = [],
        });
        _repo.Write(".testdetta/flaky-marker", "ran");
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        var results = Path.Combine(_repo.Root, ".testdetta", "my-results");
        var stdout = new StringWriter();

        // The user's own TRX logger and results directory stay as given; the report is found there.
        var exit = CliApp.Run(
            ["test", "--shadow", "--shadow-no-classify", "--repo", _repo.Root, "--base", "HEAD", "--", "--logger", "trx", "--results-directory", results],
            stdout,
            new StringWriter());

        Assert.True(exit == 3, stdout.ToString());
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        Assert.False(report.GetProperty("classified").GetBoolean());
        var classes = report.GetProperty("projects").EnumerateArray().Single().GetProperty("classes").EnumerateArray()
            .ToDictionary(static c => c.GetProperty("name").GetString()!, static c => c);
        Assert.Equal("unclassified", classes["Shop.Tests.DiscountTests"].GetProperty("verdict").GetString());
        Assert.Equal("unclassified", classes["Shop.Tests.BrokenTests"].GetProperty("verdict").GetString());
        Assert.NotEmpty(Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories));
    }

    private string Worktrees()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _repo.Root, RedirectStandardOutput = true };
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("list");
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private void WriteMap(Dictionary<string, IReadOnlyList<int>> classes)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Shop.Tests/Shop.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Shop.Calc", "Tax"), new CoveredMethod("Shop.Discount", "Apply")],
            new CoverageHits(classes, new Dictionary<string, IReadOnlyList<int>>(), []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            FileReads = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
            OutputUses = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Shop.Tests/Shop.Tests.csproj")), map);
    }
}

/// <summary>The parts of shadow mode that need no test run: options, the verdict line, times and the Markdown's escaping.</summary>
public sealed class ShadowReportTests
{
    [Fact]
    public void Shadow_options_go_together_only_where_they_make_sense()
    {
        Assert.True(CliOptions.TryParse(["test", "--shadow", "--shadow-no-classify", "--shadow-report", "out.json"], "/repo", out var options, out _));
        Assert.True(options.Test.Shadow);
        Assert.False(options.Test.ShadowClassify);
        Assert.Equal("out.json", options.Test.ShadowReport);

        Assert.False(CliOptions.TryParse(["test", "--shadow", "--dry-run"], "/repo", out _, out var error));
        Assert.Contains("--dry-run", error, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["test", "--shadow-no-classify"], "/repo", out _, out error));
        Assert.Contains("need --shadow", error, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["affected", "--shadow"], "/repo", out _, out _));
    }

    [Fact]
    public void The_selections_part_of_a_filtered_project_is_its_overhead_and_its_selected_classes()
    {
        var project = Project("filtered", wall: 10, ("A", true, 2, false), ("B", false, 5, false));

        // 10 s in all, 7 s in classes: 3 s of build and start-up, plus A's 2 s.
        Assert.Equal(5, project.SelectedSeconds, 3);
        Assert.Equal(10, Project("all", wall: 10).SelectedSeconds, 3);
        Assert.Equal(0, Project("not-affected", wall: 10).SelectedSeconds, 3);
    }

    [Fact]
    public void The_verdict_says_plainly_what_the_run_showed()
    {
        var clean = Result(Project("filtered", 10, ("A", true, 2, true) with { }, ("B", false, 5, false)));
        Assert.Equal("Would have run 1 of 2 test classes (50.0%), 5.0 s of 10.0 s of test time; every failing test class was selected or failed for a reason the change did not cause.", Verdicts(clean, ShadowVerdict.Caught).Verdict());

        var missed = Verdicts(Result(Project("filtered", 10, ("A", true, 2, false), ("B", false, 5, true))), ShadowVerdict.Miss);
        Assert.Contains("1 MISS(ES)", missed.Verdict(), StringComparison.Ordinal);

        var quiet = Result(Project("filtered", 10, ("A", true, 2, false)));
        Assert.EndsWith("no test failed, so this run shows no miss and no catch.", quiet.Verdict(), StringComparison.Ordinal);

        var none = new ShadowResult(false, "the analysis failed (boom)", [], Reproduction(), 0);
        Assert.Equal("No shadow result: the analysis failed (boom). The full suite ran as usual.", none.Verdict());
    }

    [Fact]
    public void Repository_values_cannot_inject_markdown()
    {
        var result = Verdicts(Result(Project("filtered", 1, ("Evil`[x](http://e)@team", false, 0, true))), ShadowVerdict.Miss);
        var writer = new StringWriter();

        ShadowReport.WriteMarkdown(result, writer);

        Assert.Contains("`` Evil`[x](http://e)@team ``", writer.ToString(), StringComparison.Ordinal);
    }

    private static ShadowResult Verdicts(ShadowResult result, ShadowVerdict verdict) => result with
    {
        Projects = [.. result.Projects.Select(p => p with { Classes = [.. p.Classes.Select(c => c.Failed ? c with { Verdict = verdict } : c)] })],
    };

    private static ShadowResult Result(params ShadowProject[] projects) => new(true, null, projects, Reproduction(), 1);

    private static ShadowReproduction Reproduction() => new("1.0", null, "origin/main", "abc", "def", [], [], "td affected --base abc");

    private static ShadowProject Project(string mode, double wall, params (string Name, bool Selected, double Seconds, bool Failed)[] classes) =>
        new("tests/A.Tests/A.Tests.csproj", mode, "reason", classes.Any(static c => c.Failed) ? 1 : 0, wall, true,
            [.. classes.Select(static c => new ShadowClass(c.Name, c.Selected, "Coverage", "evidence", c.Seconds, c.Failed))]);
}
