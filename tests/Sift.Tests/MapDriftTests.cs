using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

/// <summary>
/// A coverage map recorded a few commits before the merge base is used through the drift bridge:
/// code changed since the map was recorded can create new paths from tests to the change, and the
/// classes behind those paths must be selected (docs/design/map-drift-bridge.md). Each scenario also
/// shows that using the old map as if it were current misses them, and that the bridge still uses
/// the map where nothing drifted.
/// </summary>
public sealed class MapDriftTests : IDisposable
{
    private const string TestProject = "tests/Shop.Tests/Shop.Tests.csproj";

    private readonly GitRepoFixture _repo = new();

    /// <summary>Commit the map is recorded at.</summary>
    private readonly string _mapCommit;

    public MapDriftTests()
    {
        _repo.Write(".gitignore", ".sift/\n");
        _repo.Write("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Shop/Calc.cs", """
            namespace Shop;
            public class Calc { public decimal Tax(decimal net) => net * 0.2m; }
            public class Discount { public decimal Apply(decimal price) => price - 1m; }
            public interface IRule { decimal Rule(decimal value); }
            public class HalfRule : IRule { public decimal Rule(decimal value) => value / 2; }
            public static class Rules { public static IRule Create() => new HalfRule(); }
            """);
        _repo.Write(TestProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/Tests.cs", """
            namespace Shop.Tests;
            public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2m, new Calc().Tax(10m)); }
            public class DiscountTests { [Fact] public void Discounts() => Assert.Equal(9m, new Discount().Apply(10m)); }
            public class RuleTests { [Fact] public void Halves() => Assert.Equal(2m, Rules.Create().Rule(4m)); }
            public class TaxLabelTests { [Fact] public void Names() => Assert.Equal("Tax", nameof(Calc.Tax)); }
            """);
        _repo.Commit("base");
        _mapCommit = Head();
        WriteMap(_mapCommit);
    }

    [Fact]
    public void A_call_added_since_the_map_selects_the_classes_that_ran_the_caller()
    {
        _repo.Replace("src/Shop/Calc.cs", "public decimal Apply(decimal price) => price - 1m;", "public decimal Apply(decimal price) => new Calc().Tax(price) - 1m;");
        _repo.Commit("discount after tax");
        ChangeTax();

        var bridged = Select();
        Assert.Equal(TestRunMode.Filtered, bridged.Mode);
        Assert.Equal(["Shop.Tests.DiscountTests", "Shop.Tests.TaxTests"], bridged.TestClasses);

        Assert.DoesNotContain("Shop.Tests.DiscountTests", Select(new AnalysisOptions { Maps = MapUse.AsIfCurrent }).TestClasses);
    }

    [Fact]
    public void A_test_class_added_since_the_map_is_selected_by_name()
    {
        _repo.Write("tests/Shop.Tests/MoreTaxTests.cs", """
            namespace Shop.Tests;
            public class MoreTaxTests { [Fact] public void Taxes_zero() => Assert.Equal(0m, new Calc().Tax(0m)); }
            """);
        _repo.Commit("more tax tests");
        ChangeTax();

        Assert.Equal(["Shop.Tests.MoreTaxTests", "Shop.Tests.TaxTests"], Select().TestClasses);
        Assert.DoesNotContain("Shop.Tests.MoreTaxTests", Select(new AnalysisOptions { Maps = MapUse.AsIfCurrent }).TestClasses);
    }

    [Fact]
    public void An_implementation_added_since_the_map_reaches_the_tests_that_dispatch_to_it()
    {
        _repo.Replace("src/Shop/Calc.cs", "public static IRule Create() => new HalfRule();", "public static IRule Create() => new TaxRule();");
        _repo.Write("src/Shop/TaxRule.cs", """
            namespace Shop;
            public class TaxRule : IRule { public decimal Rule(decimal value) => new Calc().Tax(value); }
            """);
        _repo.Commit("rules tax");
        ChangeTax();

        Assert.Contains("Shop.Tests.RuleTests", Select().TestClasses);
        Assert.DoesNotContain("Shop.Tests.RuleTests", Select(new AnalysisOptions { Maps = MapUse.AsIfCurrent }).TestClasses);
    }

    [Fact]
    public void Drift_that_no_member_describes_leaves_the_map_unused()
    {
        _repo.Write("src/Shop/rates.json", """{ "tax": 0.2 }""");
        _repo.Commit("rates");
        ChangeTax();

        // Decided by name, as without a map: the label test names Tax without running it.
        Assert.Contains("Shop.Tests.TaxLabelTests", Select().TestClasses);
    }

    [Fact]
    public void Unrelated_drift_keeps_the_map_answer()
    {
        _repo.Replace("src/Shop/Calc.cs", "public decimal Rule(decimal value) => value / 2;", "public decimal Rule(decimal value) => value / 2m;");
        _repo.Commit("rule literal");
        ChangeTax();

        Assert.Equal(["Shop.Tests.TaxTests"], Select().TestClasses);
    }

    [Fact]
    public void Map_beyond_the_age_limit_or_outside_the_history_is_not_used()
    {
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        _repo.Commit("one");
        _repo.Replace("src/Shop/Calc.cs", "price - 2m", "price - 3m");
        _repo.Commit("two");
        ChangeTax();

        Assert.Equal(["Shop.Tests.TaxTests"], Select().TestClasses);
        Assert.Contains("Shop.Tests.TaxLabelTests", Select(new AnalysisOptions { MaxMapAge = 1 }).TestClasses);
        Assert.Contains("Shop.Tests.TaxLabelTests", Select(new AnalysisOptions { MaxMapAge = 0 }).TestClasses);

        WriteMap("0123456789abcdef0123456789abcdef01234567");
        Assert.Contains("Shop.Tests.TaxLabelTests", Select().TestClasses);
    }

    [Fact]
    public void Output_names_the_older_map_and_the_drift()
    {
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        _repo.Commit("one");
        ChangeTax();
        var analysis = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main");

        var drift = Assert.Single(analysis.Drifts);
        Assert.Equal((_mapCommit, 1), (drift.FromCommit, drift.Commits));
        Assert.Equal(["src/Shop/Calc.cs"], drift.ChangedFiles);

        Assert.True(Sift.Cli.CliOptions.TryParse(["affected", "--format", "json"], _repo.Root, out var options, out _));
        using var output = new StringWriter();
        Sift.Cli.ResultWriter.Write(
            new Sift.Cli.ResultReport(options, analysis.RepoRoot, new Sift.Cli.RunStats(1, 2), analysis.Impact, analysis.Plan) { Drifts = analysis.Drifts },
            output);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        var older = Assert.Single(json.RootElement.GetProperty("olderCoverageMaps").EnumerateArray().ToList());
        Assert.Equal(_mapCommit, older.GetProperty("commit").GetString());
        Assert.Equal(1, older.GetProperty("commitsBehind").GetInt32());
    }

    [Fact]
    public void First_parent_distance_counts_commits_and_rejects_non_ancestors()
    {
        var git = new GitClient(NullLogger<GitClient>.Instance);
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price - 2m");
        _repo.Commit("one");
        var head = Head();

        Assert.Equal(1, git.FirstParentDistance(_repo.Root, _mapCommit, head));
        Assert.Equal(0, git.FirstParentDistance(_repo.Root, head, head));
        Assert.Null(git.FirstParentDistance(_repo.Root, head, _mapCommit));
        Assert.Null(git.FirstParentDistance(_repo.Root, "0123456789abcdef0123456789abcdef01234567", head));
    }

    /// <summary>The pull request's own change: Calc.Tax.</summary>
    private void ChangeTax() => _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.25m");

    private string Head() => new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);

    private TestProjectSelection Select(AnalysisOptions? options = null) =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", options ?? new AnalysisOptions()).Tests.Single();

    /// <summary>Writes the map as recorded at <paramref name="commit"/>: each class ran what it calls there.</summary>
    private void WriteMap(string commit)
    {
        List<CoveredMethod> methods =
        [
            new("Shop.Calc", ".ctor"), new("Shop.Calc", "Tax"), new("Shop.Discount", ".ctor"), new("Shop.Discount", "Apply"),
            new("Shop.HalfRule", ".ctor"), new("Shop.HalfRule", "Rule"), new("Shop.Rules", "Create"),
        ];
        var map = new CoverageMap(
            new CoverageMapHeader(TestProject, commit, DateTimeOffset.UtcNow),
            methods,
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>>
                {
                    ["Shop.Tests.TaxTests"] = [0, 1],
                    ["Shop.Tests.DiscountTests"] = [2, 3],
                    ["Shop.Tests.RuleTests"] = [4, 5, 6],
                    ["Shop.Tests.TaxLabelTests"] = [],
                },
                new Dictionary<string, IReadOnlyList<int>>(),
                []))
        {
            FailedClasses = new HashSet<string>(),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, TestProject)), map);
    }

    public void Dispose() => _repo.Dispose();
}
