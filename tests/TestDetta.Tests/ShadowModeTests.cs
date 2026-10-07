using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;
using TestDetta.Coverage;

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
        Assert.StartsWith("td test --shadow --base ", reproduction.GetProperty("command").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("td affected --base ", reproduction.GetProperty("explainCommand").GetString(), StringComparison.Ordinal);

        // No solution or project in the root, so dotnet test without a project argument would find nothing to run.
        var run = report.GetProperty("run");
        Assert.Equal("per-project", run.GetProperty("kind").GetString());
        Assert.Contains("no single solution or project", run.GetProperty("fallbackReason").GetString(), StringComparison.Ordinal);
        Assert.Contains("Ran each test project your command runs on its own, one after another, because", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("--shadow-classify-budget 45m", reproduction.GetProperty("options").EnumerateArray().Select(static o => o.GetString()));
        var classification = report.GetProperty("classification");
        Assert.Equal(900, classification.GetProperty("rerunTimeoutSeconds").GetDouble());
        Assert.Equal(2700, classification.GetProperty("budgetSeconds").GetDouble());
        Assert.False(classification.GetProperty("budgetRanOut").GetBoolean());
        Assert.InRange(classification.GetProperty("spentSeconds").GetDouble(), 0.001, 2700);

        var markdown = File.ReadAllText(summary);
        Assert.Contains("### TestDetta shadow run", markdown, StringComparison.Ordinal);
        Assert.Contains("**Misses** (1)", markdown, StringComparison.Ordinal);
        Assert.Contains("`Shop.Tests.DiscountTests`", markdown, StringComparison.Ordinal);

        // The base commit's worktree is gone again.
        Assert.DoesNotContain("testdetta-shadow", Worktrees(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_spent_budget_leaves_the_remaining_failures_unclassified_and_the_full_run_as_it_was()
    {
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.FlakyTests"] = [],
            ["Shop.Tests.BrokenTests"] = [],
        });
        _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.3m");
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        var stdout = new StringWriter();

        // One second is gone before the first rerun could start: nothing is rerun.
        var exit = CliApp.Run(["test", "--shadow", "--shadow-classify-budget", "1s", "--repo", _repo.Root, "--base", "HEAD"], stdout, new StringWriter());

        Assert.True(exit == 3, stdout.ToString());
        Assert.DoesNotContain("td shadow: rerunning", stdout.ToString(), StringComparison.Ordinal);
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        var project = report.GetProperty("projects").EnumerateArray().Single();
        Assert.Equal(1, project.GetProperty("exitCode").GetInt32());
        var classes = project.GetProperty("classes").EnumerateArray().ToDictionary(static c => c.GetProperty("name").GetString()!, static c => c);
        Assert.Equal("caught", classes["Shop.Tests.TaxTests"].GetProperty("verdict").GetString());
        foreach (var left in (string[])["Shop.Tests.DiscountTests", "Shop.Tests.FlakyTests", "Shop.Tests.BrokenTests"])
        {
            Assert.Equal("unclassified", classes[left].GetProperty("verdict").GetString());
            Assert.Equal("the classification time budget (1 s) ran out", classes[left].GetProperty("note").GetString());
        }

        var classification = report.GetProperty("classification");
        Assert.Equal(1, classification.GetProperty("budgetSeconds").GetDouble());
        Assert.True(classification.GetProperty("budgetRanOut").GetBoolean());
        Assert.Equal(3, report.GetProperty("totals").GetProperty("unclassified").GetInt32());
        Assert.Contains("the budget ran out", stdout.ToString(), StringComparison.Ordinal);
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

    [Fact]
    public void A_solution_runs_as_one_command_and_is_still_compared_and_classified_per_project()
    {
        WriteSolutionWithSecondProject();
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.FlakyTests"] = [],
            ["Shop.Tests.BrokenTests"] = [],
        });
        _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.3m");
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        var log = new OrderedLog();

        // No project argument: dotnet test runs the one solution in the root, as the team's own job would.
        var exit = CliApp.Run(["test", "--shadow", "--log-level", "debug", "--repo", _repo.Root, "--base", "HEAD"], log.Stdout, log.Stderr);

        Assert.True(exit == 3, log.Text);
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        Assert.Equal(4, report.GetProperty("schema").GetInt32());
        var run = report.GetProperty("run");
        Assert.Equal("verbatim", run.GetProperty("kind").GetString());
        Assert.Equal("dotnet test --logger trx", run.GetProperty("command").GetString());
        Assert.Equal(1, run.GetProperty("exitCode").GetInt32());
        Assert.InRange(run.GetProperty("wallSeconds").GetDouble(), 0.1, 3600);
        Assert.Empty(run.GetProperty("unmappedReports").EnumerateArray());

        // One dotnet test for the full run; the reruns that follow are TestDetta's own, one project at a time.
        Assert.Single(log.Lines, static l => l.Contains("TestRunner: Running dotnet test", StringComparison.Ordinal));

        // Nothing was redirected or moved; the reports only TestDetta's logger asked for were read, then deleted.
        Assert.True(Directory.Exists(Path.Combine(_repo.Root, "tests", "Shop.Tests", "TestResults")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_repo.Root, "tests", "Shop.Tests", "TestResults"), "*.trx"));

        var projects = report.GetProperty("projects").EnumerateArray().ToDictionary(static p => p.GetProperty("path").GetString()!);
        var shop = projects["tests/Shop.Tests/Shop.Tests.csproj"];
        Assert.Equal("filtered", shop.GetProperty("mode").GetString());
        Assert.Equal(1, shop.GetProperty("exitCode").GetInt32());
        Assert.InRange(shop.GetProperty("wallSeconds").GetDouble(), 0.001, 3600);
        var classes = shop.GetProperty("classes").EnumerateArray().ToDictionary(static c => c.GetProperty("name").GetString()!, static c => c);
        Assert.Equal("caught", classes["Shop.Tests.TaxTests"].GetProperty("verdict").GetString());
        Assert.Equal("miss", classes["Shop.Tests.DiscountTests"].GetProperty("verdict").GetString());
        Assert.Equal("flaky", classes["Shop.Tests.FlakyTests"].GetProperty("verdict").GetString());
        Assert.Equal("pre-existing", classes["Shop.Tests.BrokenTests"].GetProperty("verdict").GetString());
        var other = projects["tests/Other.Tests/Other.Tests.csproj"];
        Assert.Equal("not-affected", other.GetProperty("mode").GetString());
        Assert.Equal(0, other.GetProperty("exitCode").GetInt32());
        Assert.False(other.GetProperty("classes").EnumerateArray().Single().GetProperty("selected").GetBoolean());
        Assert.StartsWith("Would have run 1 of 5 test classes", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Contains("the whole test run took", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);

        // Nothing of the selection before the full run has ended; the analysis's log comes after it, since everything ran.
        var ended = log.Lines.FindIndex(static l => l.Contains("TestRunner: dotnet test", StringComparison.Ordinal) && l.Contains(" exited with ", StringComparison.Ordinal));
        Assert.True(ended > 0, log.Text);
        Assert.DoesNotContain(log.Lines.Take(ended), static l => RevealsSelection(l));
        Assert.Contains(log.Lines.Skip(ended), static l => l.Contains("] TestSelector:", StringComparison.Ordinal));
        Assert.True(log.Lines.FindIndex(static l => l.StartsWith("  tests/Shop.Tests/Shop.Tests.csproj: filtered, would have run", StringComparison.Ordinal)) > ended, log.Text);
        Assert.DoesNotContain("testdetta-shadow", Worktrees(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_project_runs_as_given_and_a_partial_filter_shows_decisions_only_for_the_classes_that_ran()
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
        var stdout = new StringWriter();

        var exit = CliApp.Run(
            ["test", "--shadow", "--shadow-no-classify", "--repo", _repo.Root, "--base", "HEAD", "--", "tests/Shop.Tests/Shop.Tests.csproj", "--filter", "Category!=Broken"],
            stdout,
            new StringWriter());

        Assert.True(exit == 3, stdout.ToString());
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        var run = report.GetProperty("run");
        Assert.Equal("verbatim", run.GetProperty("kind").GetString());
        Assert.StartsWith("dotnet test tests/Shop.Tests/Shop.Tests.csproj --filter Category!=Broken --logger trx", run.GetProperty("command").GetString(), StringComparison.Ordinal);
        var project = report.GetProperty("projects").EnumerateArray().Single();
        var classes = project.GetProperty("classes").EnumerateArray().Select(static c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Shop.Tests.DiscountTests", "Shop.Tests.FlakyTests", "Shop.Tests.TaxTests"], classes);
        Assert.Equal(["Shop.Tests.BrokenTests"], project.GetProperty("notRun").EnumerateArray().Select(static c => c.GetString()));
        Assert.False(project.TryGetProperty("estimatedFullSeconds", out _));
        var totals = report.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("classes").GetInt32());
        Assert.Equal(1, totals.GetProperty("notRunClasses").GetInt32());
        Assert.Contains("1 class(es) the selection knows did not run in this job", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);

        // The reproduction needs no license: it is td test --shadow again, with the same arguments.
        var command = report.GetProperty("reproduction").GetProperty("command").GetString()!;
        Assert.DoesNotContain("affected", command, StringComparison.Ordinal);
        Assert.True(CliOptions.TryParse([.. command.Split(' ').Skip(1)], _repo.Root, out var again, out var error), error);
        Assert.True(again.Test.Shadow);
        Assert.Equal(["tests/Shop.Tests/Shop.Tests.csproj", "--filter", "Category!=Broken"], again.Test.DotnetTestArguments);
    }

    [Fact]
    public void A_filter_that_matches_nothing_reveals_no_decision_and_shadow_mode_reads_no_license()
    {
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.FlakyTests"] = [],
            ["Shop.Tests.BrokenTests"] = [],
        });
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        var read = new List<string>();
        var home = Path.Combine(_repo.Root, ".testdetta", "home");
        var host = new CliHost(name =>
        {
            lock (read)
            {
                read.Add(name);
            }

            // A license and a refresh setting are there to be read; shadow mode must not look.
            return name.StartsWith("TESTDETTA_LICENSE", StringComparison.Ordinal) ? "testdetta1.not-a-real-license" : null;
        }, home);
        var log = new OrderedLog();

        var exit = CliApp.Run(
            ["test", "--shadow", "--shadow-no-classify", "--log-level", "debug", "--repo", _repo.Root, "--base", "HEAD", "--", "tests/Shop.Tests/Shop.Tests.csproj", "--filter", "FullyQualifiedName~Nothing"],
            log.Stdout,
            log.Stderr,
            host);

        Assert.True(exit is 0 or 3, log.Text);
        Assert.Empty(read);
        Assert.False(Directory.Exists(home));
        var json = File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"));
        var report = JsonDocument.Parse(json).RootElement;
        var project = report.GetProperty("projects").EnumerateArray().Single();
        Assert.Equal("not-shown", project.GetProperty("mode").GetString());
        Assert.Empty(project.GetProperty("classes").EnumerateArray());
        Assert.Equal(4, project.GetProperty("notRun").GetArrayLength());
        Assert.Equal(0, report.GetProperty("totals").GetProperty("classes").GetInt32());
        Assert.Equal(0, report.GetProperty("totals").GetProperty("selectedClasses").GetInt32());
        Assert.DoesNotContain("\"decision\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"evidence\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("filtered", json, StringComparison.Ordinal);

        // Neither the console nor the logs say anything about the selection, before or after the run.
        Assert.DoesNotContain(log.Lines, static l => RevealsSelection(l));
        Assert.Contains("the analysis's log is not shown", log.Text, StringComparison.Ordinal);

        // td test without --shadow still checks the license.
        Assert.Equal(0, CliApp.Run(["test", "--dry-run", "--repo", _repo.Root, "--base", "HEAD"], new StringWriter(), new StringWriter(), host));
        Assert.Contains("TESTDETTA_LICENSE", read);
        Assert.Contains("TESTDETTA_LICENSE_REFRESH", read);
    }

    [Fact]
    public void Under_the_testing_platform_a_built_solution_runs_as_one_command_with_one_report_option()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="4.0.1" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("Shop.slnx", """<Solution><Project Path="src/Shop/Shop.csproj" /><Project Path="tests/Shop.Tests/Shop.Tests.csproj" /></Solution>""");
        _repo.Write(".testdetta/flaky-marker", "ran");
        _repo.Commit("testing platform");
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.FlakyTests"] = [],
            ["Shop.Tests.BrokenTests"] = [],
        });
        _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.3m");
        var build = new StringWriter();

        // Built first, as a job that runs dotnet test --no-build does: the report option is read from the build output.
        Assert.True(new DotnetProcess(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).Run(["build", "Shop.slnx"], _repo.Root, new Dictionary<string, string>(), build) == 0, build.ToString());
        var stdout = new StringWriter();
        var exit = CliApp.Run(["test", "--shadow", "--shadow-no-classify", "--repo", _repo.Root, "--base", "HEAD", "--", "--solution", "Shop.slnx", "--no-build"], stdout, new StringWriter());

        Assert.True(exit == 3, stdout.ToString());
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        var run = report.GetProperty("run");
        Assert.Equal("verbatim", run.GetProperty("kind").GetString());
        Assert.Equal("dotnet test --solution Shop.slnx --no-build --report-xunit-trx", run.GetProperty("command").GetString());
        var classes = report.GetProperty("projects").EnumerateArray().Single().GetProperty("classes").EnumerateArray()
            .ToDictionary(static c => c.GetProperty("name").GetString()!, static c => c);
        Assert.Equal("caught", classes["Shop.Tests.TaxTests"].GetProperty("verdict").GetString());
        Assert.Equal("unclassified", classes["Shop.Tests.BrokenTests"].GetProperty("verdict").GetString());

        // The team asked for no report: the ones TestDetta's report option wrote beside the build output are read, then deleted.
        Assert.Empty(Directory.GetFiles(_repo.Root, "*.trx", SearchOption.AllDirectories));
    }

    [Fact]
    public void Under_the_testing_platform_a_project_not_built_before_the_run_is_named_with_why_it_has_no_report()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="4.0.1" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("Shop.slnx", """<Solution><Project Path="src/Shop/Shop.csproj" /><Project Path="tests/Shop.Tests/Shop.Tests.csproj" /></Solution>""");
        _repo.Write(".testdetta/flaky-marker", "ran");
        _repo.Commit("testing platform");
        _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.3m");
        var summary = Path.Combine(_repo.Root, ".testdetta", "summary.md");
        var stdout = new StringWriter();

        // A fresh checkout: nothing is built, so the report option cannot be read from the build output and is not guessed.
        var exit = CliApp.Run(["test", "--shadow", "--shadow-no-classify", "--repo", _repo.Root, "--base", "HEAD", "--summary-file", summary, "--", "--solution", "Shop.slnx"], stdout, new StringWriter());

        Assert.True(exit != 0, stdout.ToString());
        var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"))).RootElement;
        Assert.Equal("per-project", report.GetProperty("run").GetProperty("kind").GetString());
        Assert.Contains("has no build output yet", report.GetProperty("run").GetProperty("fallbackReason").GetString(), StringComparison.Ordinal);
        var project = report.GetProperty("projects").EnumerateArray().Single();
        Assert.True(project.GetProperty("notBuilt").GetBoolean());
        Assert.False(project.GetProperty("reported").GetBoolean());
        Assert.StartsWith("no test report: the project was not built before the run (tests/Shop.Tests/bin/Debug/net10.0 did not exist)", project.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("build first and run dotnet test --no-build", project.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, report.GetProperty("totals").GetProperty("notBuiltProjects").GetInt32());
        Assert.Contains("1 testing-platform project(s) were not built before the run", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Contains("tests/Shop.Tests/Shop.Tests.csproj: not shown, no test report: the project was not built before the run", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("- `tests/Shop.Tests/Shop.Tests.csproj`: no test report: the project was not built before the run", File.ReadAllText(summary), StringComparison.Ordinal);
    }

    /// <summary>A line from the analysis's log or the plan: anything that tells what the selection decided.</summary>
    private static bool RevealsSelection(string line) =>
        line.Contains("] TestSelector:", StringComparison.Ordinal)
        || line.Contains("] CoverageSelection:", StringComparison.Ordinal)
        || line.Contains("] AnalysisPipeline:", StringComparison.Ordinal)
        || line.Contains("] ImpactAnalyzer:", StringComparison.Ordinal)
        || line.Contains("would have run", StringComparison.Ordinal)
        || line.Contains("affected test project", StringComparison.Ordinal)
        || line.Contains("Tests to run", StringComparison.Ordinal);

    /// <summary>Adds a solution in the root and a second test project the change does not reach.</summary>
    private void WriteSolutionWithSecondProject()
    {
        _repo.Write("tests/Other.Tests/Other.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Other.Tests/Tests.cs", "using Xunit; namespace Other.Tests; public class OtherTests { [Fact] public void Holds() => Assert.True(true); }");
        _repo.Write("Shop.slnx", """
            <Solution>
              <Project Path="src/Shop/Shop.csproj" />
              <Project Path="tests/Shop.Tests/Shop.Tests.csproj" />
              <Project Path="tests/Other.Tests/Other.Tests.csproj" />
            </Solution>
            """);
        _repo.Commit("solution");
    }

    /// <summary>stdout and stderr as one list of lines in the order they were written.</summary>
    private sealed class OrderedLog
    {
        private readonly List<string> _lines = [];

        public OrderedLog()
        {
            Stdout = new LineWriter(this, string.Empty);
            Stderr = new LineWriter(this, string.Empty);
        }

        public TextWriter Stdout { get; }

        public TextWriter Stderr { get; }

        public List<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public string Text => string.Join('\n', Lines);

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        private sealed class LineWriter(OrderedLog log, string prefix) : TextWriter
        {
            private readonly System.Text.StringBuilder _pending = new();

            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

            public override void Write(char value)
            {
                lock (_pending)
                {
                    if (value == '\n')
                    {
                        log.Add(prefix + _pending.ToString().TrimEnd('\r'));
                        _pending.Clear();
                        return;
                    }

                    _pending.Append(value);
                }
            }
        }
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
    public void Shadow_time_limits_default_and_can_each_be_set_or_switched_off()
    {
        Assert.True(CliOptions.TryParse(["test", "--shadow"], "/repo", out var options, out _));
        Assert.Null(options.Test.ShadowLimits);
        Assert.Equal(new ShadowLimits(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(45)), ShadowLimits.Default);

        Assert.True(CliOptions.TryParse(
            ["test", "--shadow", "--shadow-rerun-timeout", "90s", "--shadow-rerun-idle-timeout", "none", "--shadow-base-timeout", "1h30m", "--shadow-base-idle-timeout", "0", "--shadow-classify-budget", "20"],
            "/repo",
            out options,
            out var error),
            error);
        Assert.Equal(new ShadowLimits(TimeSpan.FromSeconds(90), null, TimeSpan.FromMinutes(90), null, TimeSpan.FromMinutes(20)), options.Test.ShadowLimits);

        // One option given keeps the others' defaults.
        Assert.True(CliOptions.TryParse(["test", "--shadow", "--shadow-classify-budget", "2h"], "/repo", out options, out _));
        Assert.Equal(ShadowLimits.Default with { Budget = TimeSpan.FromHours(2) }, options.Test.ShadowLimits);

        foreach (var invalid in (string[])["-5", "1.5h", "15min", "m", "", "30m1h", "1h1h", "169h", "9999999999s"])
        {
            Assert.False(CliOptions.TryParse(["test", "--shadow", "--shadow-base-timeout", invalid], "/repo", out _, out error), invalid);
            Assert.Contains("--shadow-base-timeout needs a time such as 90s, 15m, 1h or 1h30m", error, StringComparison.Ordinal);
        }

        Assert.False(CliOptions.TryParse(["test", "--shadow-classify-budget", "45m"], "/repo", out _, out error));
        Assert.Contains("need --shadow", error, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["test", "--shadow", "--shadow-rerun-timeout"], "/repo", out _, out error));
        Assert.Contains("needs a value", error, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["affected", "--shadow-rerun-timeout", "5m"], "/repo", out _, out _));
    }

    [Theory]
    [InlineData("90s", 90, "1m30s", "1 min 30 s")]
    [InlineData("15m", 900, "15m", "15 min")]
    [InlineData("15", 900, "15m", "15 min")]
    [InlineData("1h", 3600, "1h", "1 h")]
    [InlineData(" 1H30M ", 5400, "1h30m", "1 h 30 min")]
    [InlineData("1h0m5s", 3605, "1h5s", "1 h 5 s")]
    public void A_time_limit_reads_and_prints_in_the_command_lines_syntax(string value, int seconds, string option, string described)
    {
        Assert.True(Durations.TryParseLimit(value, out var limit));
        Assert.Equal(TimeSpan.FromSeconds(seconds), limit);
        Assert.Equal(option, Durations.Limit(limit));
        Assert.Equal(described, Durations.DescribeLimit(limit));
        Assert.True(Durations.TryParseLimit("none", out limit));
        Assert.Equal("none", Durations.Limit(limit));
        Assert.Equal("no limit", Durations.DescribeLimit(limit));
    }

    [Fact]
    public void The_budget_cuts_a_rerun_to_what_is_left_and_runs_out_before_nothing_is_left()
    {
        var now = TimeSpan.FromMinutes(8);
        var budget = new ShadowBudget(TimeSpan.FromMinutes(10), () => now);

        Assert.Equal((new RunLimits(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)), true), budget.For(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)));
        Assert.Equal((new RunLimits(TimeSpan.FromMinutes(1), null), false), budget.For(TimeSpan.FromMinutes(1), null));
        Assert.Equal((new RunLimits(TimeSpan.FromMinutes(2), null), true), budget.For(null, null));
        Assert.False(budget.RanOut);

        now = TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(500);
        Assert.True(budget.RanOut);
        Assert.False(budget.CutShort);
        Assert.Equal("the classification time budget (10 min) ran out", budget.Exhausted());
        Assert.True(budget.CutShort);

        now = TimeSpan.FromHours(5);
        var unlimited = new ShadowBudget(null, () => now);
        Assert.False(unlimited.RanOut);
        Assert.Equal((new RunLimits(TimeSpan.FromMinutes(15), null), false), unlimited.For(TimeSpan.FromMinutes(15), null));
    }

    [Fact]
    public void Every_report_shows_the_limits_and_the_budget_spent()
    {
        var result = Verdicts(Result(Project("filtered", 10, ("A", false, 2, true))), ShadowVerdict.Unclassified) with
        {
            Classification = new ShadowClassification(ShadowLimits.Default with { BaseIdle = null }, 12, BudgetRanOut: true),
        };
        var markdown = new StringWriter();
        var console = new StringWriter();
        var path = Path.Combine(Path.GetTempPath(), "testdetta-shadow-" + Guid.NewGuid().ToString("N") + ".json");

        ShadowReport.WriteMarkdown(result, markdown);
        ShadowReport.WriteConsole(result, console);
        try
        {
            ShadowReport.WriteJson(result, path);
            var classification = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("classification");
            Assert.Equal(900, classification.GetProperty("rerunTimeoutSeconds").GetDouble());
            Assert.Equal(300, classification.GetProperty("rerunIdleSeconds").GetDouble());
            Assert.Equal(1800, classification.GetProperty("baseTimeoutSeconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, classification.GetProperty("baseIdleSeconds").ValueKind);
            Assert.Equal(2700, classification.GetProperty("budgetSeconds").GetDouble());
            Assert.Equal(12, classification.GetProperty("spentSeconds").GetDouble());
            Assert.True(classification.GetProperty("budgetRanOut").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }

        const string Line = "Classification took 12.0 s of its 45 min budget; a rerun with the change at most 15 min (idle 5 min), on the base commit at most 30 min (idle no limit); the budget ran out, so the classes left then are not classified.";
        Assert.Contains(Line, markdown.ToString(), StringComparison.Ordinal);
        Assert.Contains(Line, console.ToString(), StringComparison.Ordinal);
        Assert.Contains("- options: `--base origin/main --shadow-classify-budget 45m`", markdown.ToString(), StringComparison.Ordinal);

        // Without a classification (switched off, or nothing compared) there is nothing to show.
        Assert.DoesNotContain("Classification took", WriteMarkdown(result with { Classification = null }), StringComparison.Ordinal);
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

    [Fact]
    public void The_target_of_the_teams_command_is_found_in_either_runners_spelling()
    {
        var directory = Directory.CreateTempSubdirectory("testdetta-target-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "tests"));
            var solution = ShadowCommand.Target(["-c", "Release", "Shop.sln", "--filter", "Category!=Slow"], directory);
            Assert.Equal((ShadowTargetKind.Solution, Path.Combine(directory, "Shop.sln"), 2), (solution.Kind, solution.Path, solution.Index));
            Assert.Equal(["-c", "Release", "--filter", "Category!=Slow"], solution.Without(["-c", "Release", "Shop.sln", "--filter", "Category!=Slow"]));

            var platform = ShadowCommand.Target(["--solution", "Shop.slnx", "--report-trx"], directory);
            Assert.Equal((ShadowTargetKind.Solution, 0, 2), (platform.Kind, platform.Index, platform.Length));
            Assert.Equal(["--report-trx"], platform.Without(["--solution", "Shop.slnx", "--report-trx"]));

            Assert.Equal(ShadowTargetKind.Project, ShadowCommand.Target(["--project", "tests/A.Tests/A.Tests.fsproj"], directory).Kind);
            Assert.Equal(ShadowTargetKind.Project, ShadowCommand.Target(["tests/A.Tests/A.Tests.csproj"], directory).Kind);
            Assert.Equal(ShadowTargetKind.Directory, ShadowCommand.Target(["tests"], directory).Kind);
            Assert.Equal(ShadowTargetKind.Other, ShadowCommand.Target(["bin/A.Tests.dll"], directory).Kind);

            // An option's value is not the target, nor is anything after "--" (run settings).
            Assert.Equal(ShadowTargetKind.None, ShadowCommand.Target(["--results-directory", "tests", "-c", "Release"], directory).Kind);
            Assert.Equal(ShadowTargetKind.None, ShadowCommand.Target(["--", "tests"], directory).Kind);
            Assert.Equal(-1, ShadowCommand.Target([], directory).Index);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_target_is_found_in_the_equals_and_colon_spellings_too()
    {
        var directory = Directory.CreateTempSubdirectory("testdetta-target-").FullName;
        try
        {
            var project = ShadowCommand.Target(["-c", "Release", "--project=tests/A.Tests/A.Tests.csproj", "--no-build"], directory);
            Assert.Equal((ShadowTargetKind.Project, Path.Combine(directory, "tests/A.Tests/A.Tests.csproj"), 2, 1), (project.Kind, project.Path, project.Index, project.Length));
            Assert.Equal(["-c", "Release", "--no-build"], project.Without(["-c", "Release", "--project=tests/A.Tests/A.Tests.csproj", "--no-build"]));
            Assert.Equal(ShadowTargetKind.Solution, ShadowCommand.Target(["--solution:Shop.slnx"], directory).Kind);
            Assert.Equal(ShadowTargetKind.Other, ShadowCommand.Target(["--test-modules=bin/**/*.Tests.dll"], directory).Kind);

            // An option's value in its = form is not mistaken for the target either.
            Assert.Equal(ShadowTargetKind.None, ShadowCommand.Target(["--results-directory=tests"], directory).Kind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("--logger", "trx")]
    [InlineData("-l", "trx;LogFileName=team.trx")]
    [InlineData("--logger", "TRX")]
    [InlineData("--logger:trx", null)]
    [InlineData("--logger=trx;LogFileName=x.trx", null)]
    [InlineData("-l:trx", null)]
    [InlineData("--logger", "logger://Microsoft/TestPlatform/TrxLogger/v1")]
    public void A_trx_logger_is_recognised_in_every_spelling(string option, string? value)
    {
        Assert.True(TestRunArguments.HasTrxLogger(value is null ? [option] : [option, value]));
        Assert.False(TestRunArguments.HasTrxLogger(["--logger", "console;verbosity=detailed", "-l:html"]));
    }

    [Fact]
    public void A_command_in_a_report_is_quoted_as_a_shell_needs_it()
    {
        Assert.Equal("dotnet test -l \"trx;LogFileName=a.trx\" --filter \"A|B\" Category!=Slow \"x\\$y\"", "dotnet test " + TestRunArguments.CommandLine(["-l", "trx;LogFileName=a.trx", "--filter", "A|B", "Category!=Slow", "x$y"]));
    }

    [Fact]
    public void The_analysis_time_limit_defaults_to_ten_minutes_and_needs_shadow()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ShadowLimits.Default.Analysis);
        Assert.True(CliOptions.TryParse(["test", "--shadow", "--shadow-analysis-timeout", "none"], "/repo", out var options, out var error), error);
        Assert.Null(options.Test.ShadowLimits!.Analysis);
        Assert.Contains("--shadow-analysis-timeout none", options.Test.ShadowLimits.Options);
        Assert.False(CliOptions.TryParse(["test", "--shadow-analysis-timeout", "5m"], "/repo", out _, out error));
        Assert.Contains("need --shadow", error, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["test", "--shadow", "--shadow-analysis-timeout", "soon"], "/repo", out _, out _));
    }

    [Fact]
    public void A_pipe_in_a_repository_value_cannot_split_a_table_row()
    {
        var project = new ShadowProject("tests/A|B.Tests/A.Tests.csproj", "filtered", "reason", 0, 1, true, [new ShadowClass("A", true, "Coverage", "evidence", 1, false)]);

        var markdown = WriteMarkdown(Result(project));

        var row = markdown.Split('\n').Single(static l => l.StartsWith("| `tests/A", StringComparison.Ordinal));
        Assert.StartsWith("| `tests/A\\|B.Tests/A.Tests.csproj` | filtered |", row, StringComparison.Ordinal);
        Assert.Equal(8, row.Replace("\\|", string.Empty, StringComparison.Ordinal).Count(static c => c == '|'));
    }

    [Fact]
    public void The_projects_of_a_solution_or_filter_are_read_in_every_format()
    {
        var directory = Directory.CreateTempSubdirectory("testdetta-solution-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Shop.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "tests", "tests", "{0AB3BF05-4346-4AA6-1389-037BE0695223}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A.Tests", "tests\A.Tests\A.Tests.csproj", "{BAB3E586-92B8-4144-A876-970840FC9DF4}"
                EndProject
                """);
            File.WriteAllText(Path.Combine(directory, "Shop.slnx"), """<Solution><Folder Name="/tests/"><Project Path="tests/B.Tests/B.Tests.csproj" /></Folder></Solution>""");
            File.WriteAllText(Path.Combine(directory, "Fast.slnf"), """{ "solution": { "path": "Shop.sln", "projects": [ "tests\\A.Tests\\A.Tests.csproj" ] } }""");
            string Full(string relative) => Path.GetFullPath(Path.Combine(directory, relative)).Replace('\\', '/');

            Assert.Equal([Full("tests/A.Tests/A.Tests.csproj")], ShadowCommand.Projects(ShadowCommand.Target(["Shop.sln"], directory)));
            Assert.Equal([Full("tests/B.Tests/B.Tests.csproj")], ShadowCommand.Projects(ShadowCommand.Target(["Shop.slnx"], directory)));
            Assert.Equal([Full("tests/A.Tests/A.Tests.csproj")], ShadowCommand.Projects(ShadowCommand.Target(["Fast.slnf"], directory)));

            // Two solutions in the directory: dotnet test without an argument stops, so there is nothing to tell.
            Assert.False(ShadowCommand.FindsOne(directory));
            Assert.Null(ShadowCommand.Projects(ShadowCommand.Target([], directory)));
            File.Delete(Path.Combine(directory, "Shop.sln"));
            Assert.True(ShadowCommand.FindsOne(directory));
            Assert.Equal([Full("tests/B.Tests/B.Tests.csproj")], ShadowCommand.Projects(ShadowCommand.Target([], directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_report_tells_its_test_assembly_its_times_and_its_outcome()
    {
        var file = Path.Combine(Path.GetTempPath(), "testdetta-facts-" + Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(file, """
            <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times creation="2026-10-07T01:31:49.29+03:00" start="2026-10-07T01:31:47.5+03:00" finish="2026-10-07T01:31:49.5+03:00" />
              <ResultSummary outcome="Failed" />
              <TestDefinitions>
                <UnitTest name="X" storage="/repo/tests/a.tests/bin/debug/net10.0/a.tests.dll" id="t1">
                  <TestMethod codeBase="/repo/tests/A.Tests/bin/Debug/net10.0/A.Tests.dll" className="A.Tests.A1Tests" name="X" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """);
        try
        {
            var facts = TrxReport.Facts(file)!;
            Assert.Equal(["/repo/tests/A.Tests/bin/Debug/net10.0/A.Tests.dll"], facts.Assemblies);
            Assert.Equal(2, (facts.Finish!.Value - facts.Start!.Value).TotalSeconds, 3);
            Assert.True(facts.Failed);
            Assert.False((facts with { Outcome = "Completed" }).Failed);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_project_none_of_whose_tests_ran_shows_no_decision_anywhere()
    {
        var ran = Project("filtered", 10, ("A", true, 2, false), ("B", false, 5, false)) with { NotRun = ["C"] };
        var hidden = new ShadowProject("tests/B.Tests/B.Tests.csproj", ShadowProject.NotShown, "no test of it ran in this job", 0, 1, true, []) { NotRun = ["Secret.Tests.Kept"] };
        var result = Result(ran, hidden) with { Run = new ShadowRunInfo(ShadowRunKind.Verbatim, "dotnet test Shop.slnx --logger trx", null, 12, 0) };
        var path = Path.Combine(Path.GetTempPath(), "testdetta-shadow-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ShadowReport.WriteJson(result, path);
            var report = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            var totals = report.GetProperty("totals");
            Assert.Equal(2, totals.GetProperty("classes").GetInt32());
            Assert.Equal(2, totals.GetProperty("notRunClasses").GetInt32());
            Assert.Equal(10, totals.GetProperty("fullSeconds").GetDouble());
            Assert.Equal(12, totals.GetProperty("wallSeconds").GetDouble());
            var second = report.GetProperty("projects")[1];
            Assert.Equal("not-shown", second.GetProperty("mode").GetString());
            Assert.Empty(second.GetProperty("classes").EnumerateArray());
            Assert.Equal(0, second.GetProperty("selectedSeconds").GetDouble());
            Assert.Equal("verbatim", report.GetProperty("run").GetProperty("kind").GetString());
        }
        finally
        {
            File.Delete(path);
        }

        var console = new StringWriter();
        ShadowReport.WriteConsole(result, console);
        Assert.Contains("  tests/B.Tests/B.Tests.csproj: not shown, no test of it ran in this job", console.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 class(es) did not run in this job", console.ToString(), StringComparison.Ordinal);
        Assert.Contains("the whole test run took 12.0 s", result.Verdict(), StringComparison.Ordinal);
        Assert.Contains("2 class(es) the selection knows did not run in this job", result.Verdict(), StringComparison.Ordinal);
        var markdown = WriteMarkdown(result);
        Assert.Contains("| `tests/B.Tests/B.Tests.csproj` | not shown: `no test of it ran in this job` |", markdown, StringComparison.Ordinal);
        Assert.Contains("Ran your own command as given, adding only a test report per project: `dotnet test Shop.slnx --logger trx`", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret.Tests.Kept", console + markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_failed_without_a_failed_test_in_any_report_cannot_be_checked()
    {
        var result = Result(Project("filtered", 10, ("A", true, 2, false))) with { Run = new ShadowRunInfo(ShadowRunKind.Verbatim, "dotnet test", null, 5, 1) };

        Assert.True(result.UnexplainedRun);
        Assert.EndsWith("the test run failed (exit code 1) without a report naming a failed test, so the selection cannot be checked.", result.Verdict(), StringComparison.Ordinal);
        Assert.False((result with { Run = result.Run! with { ExitCode = 0 } }).UnexplainedRun);
    }

    private static ShadowResult Verdicts(ShadowResult result, ShadowVerdict verdict) => result with
    {
        Projects = [.. result.Projects.Select(p => p with { Classes = [.. p.Classes.Select(c => c.Failed ? c with { Verdict = verdict } : c)] })],
    };

    private static ShadowResult Result(params ShadowProject[] projects) => new(true, null, projects, Reproduction(), 1);

    private static ShadowReproduction Reproduction() => new("1.0", null, "origin/main", "abc", "def", [], ["--base origin/main", "--shadow-classify-budget 45m"], "td test --shadow --base abc", "td affected --base abc --format json");

    private static string WriteMarkdown(ShadowResult result)
    {
        var writer = new StringWriter();
        ShadowReport.WriteMarkdown(result, writer);
        return writer.ToString();
    }

    private static ShadowProject Project(string mode, double wall, params (string Name, bool Selected, double Seconds, bool Failed)[] classes) =>
        new("tests/A.Tests/A.Tests.csproj", mode, "reason", classes.Any(static c => c.Failed) ? 1 : 0, wall, true,
            [.. classes.Select(static c => new ShadowClass(c.Name, c.Selected, "Coverage", "evidence", c.Seconds, c.Failed))]);
}
