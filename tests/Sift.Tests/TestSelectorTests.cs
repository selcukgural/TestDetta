using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

/// <summary>End-to-end selection on a small shop solution committed to a temporary git repository.</summary>
public sealed class TestSelectorTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public TestSelectorTests()
    {
        _repo.Write("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Shop/PriceCalculator.cs", """
            namespace Shop;
            public class PriceCalculator
            {
                public PriceCalculator() { }
                public decimal Total(decimal net) => net + Tax(net);
                public decimal Tax(decimal net) => net * 0.2m;
            }
            """);
        _repo.Write("src/Shop/OrderService.cs", """
            namespace Shop;
            public class OrderService(PriceCalculator calculator)
            {
                public decimal Checkout(decimal net) => calculator.Total(net);
            }
            """);
        _repo.Write("src/Shop/Slug.cs", """
            namespace Shop;
            public static class Slug
            {
                public static string Make(string title) => title.ToLowerInvariant();
            }
            """);
        _repo.Write("src/Shop/PingHandler.cs", """
            namespace Shop;
            public interface IHandler { string Handle(); }
            public class PingHandler : IHandler
            {
                public string Handle() => "pong";
            }
            """);

        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/OrderServiceTests.cs", """
            namespace Shop.Tests;
            public class OrderServiceTests
            {
                [Fact] public void Adds_tax() => Assert.Equal(12m, new OrderService(new PriceCalculator()).Checkout(10m));
            }
            """);
        _repo.Write("tests/Shop.Tests/TaxTests.cs", """
            namespace Shop.Tests;
            public class TaxTests
            {
                [Fact] public void Is_twenty_percent() => Assert.Equal(2m, new PriceCalculator().Tax(10m));
            }
            """);
        _repo.Write("tests/Shop.Tests/SlugTests.cs", """
            namespace Shop.Tests;
            public class SlugTests : TestBase
            {
                [Fact] public void Lowercases() => Assert.Equal(Expected(), Slug.Make("A"));
            }
            """);
        _repo.Write("tests/Shop.Tests/TestBase.cs", """
            namespace Shop.Tests;
            public abstract class TestBase
            {
                protected static string Expected() => "a";
            }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Member_change_selects_only_tests_that_reach_it_by_name()
    {
        _repo.Replace("src/Shop/PriceCalculator.cs", "net + Tax(net)", "net + Tax(net) + 0m");

        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Shop.Tests.OrderServiceTests"], selection.TestClasses);
    }

    [Fact]
    public void Static_test_class_is_selected()
    {
        // xUnit and NUnit run static test methods of static classes (Roslynator's tests do).
        _repo.Write("tests/Shop.Tests/StaticTaxTests.cs", """
            namespace Shop.Tests;
            public static class StaticTaxTests
            {
                [Fact] public static void Taxes() => Assert.Equal(2m, new PriceCalculator().Tax(10m));
            }
            """);
        _repo.Commit("static tests");
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Contains("Shop.Tests.StaticTaxTests", selection.TestClasses);
    }

    [Fact]
    public void Test_class_inside_a_conditional_is_found_by_name()
    {
        _repo.Write("tests/Shop.Tests/ModernTaxTests.cs", """
            #if NET8_0_OR_GREATER
            namespace Shop.Tests;
            public class ModernTaxTests
            {
                [Fact] public void Taxes() => Assert.Equal(2m, new PriceCalculator().Tax(10m));
            }
            #endif
            """);
        _repo.Commit("conditional tests");
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Contains("Shop.Tests.ModernTaxTests", selection.TestClasses);
    }

    [Fact]
    public void Change_used_by_several_paths_selects_each_test_class()
    {
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        // Tax is called by TaxTests directly and by OrderServiceTests through Total and Checkout.
        Assert.Equal(["Shop.Tests.OrderServiceTests", "Shop.Tests.TaxTests"], Select().TestClasses);
    }

    [Fact]
    public void Constructor_change_selects_everything_that_names_the_type()
    {
        _repo.Replace("src/Shop/PriceCalculator.cs", "public PriceCalculator() { }", "public PriceCalculator() { _ = 1; }");

        Assert.Equal(["Shop.Tests.OrderServiceTests", "Shop.Tests.TaxTests"], Select().TestClasses);
    }

    [Fact]
    public void Base_class_change_in_test_project_selects_derived_test_classes()
    {
        _repo.Replace("tests/Shop.Tests/TestBase.cs", "=> \"a\"", "=> \"a\" + \"\"");

        Assert.Equal(["Shop.Tests.SlugTests"], Select().TestClasses);
    }

    [Fact]
    public void Change_to_a_test_class_selects_that_class()
    {
        _repo.Replace("tests/Shop.Tests/TaxTests.cs", "Equal(2m", "Equal(2.0m");

        Assert.Equal(["Shop.Tests.TaxTests"], Select().TestClasses);
    }

    [Fact]
    public void Type_that_nothing_names_falls_back_to_the_whole_project()
    {
        _repo.Replace("src/Shop/PingHandler.cs", "\"pong\"", "\"PONG\"");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("Shop.PingHandler", selection.Reason);
    }

    [Fact]
    public void Non_csharp_file_falls_back_to_the_whole_project()
    {
        _repo.Write("src/Shop/appsettings.json", "{}");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("appsettings.json", selection.Reason);
    }

    [Fact]
    public void Added_test_class_is_selected_on_its_own()
    {
        _repo.Write("tests/Shop.Tests/NewTests.cs", """
            namespace Shop.Tests;
            public class NewTests { [Fact] public void Works() { } }
            """);

        Assert.Equal(["Shop.Tests.NewTests"], Select().TestClasses);
    }

    [Fact]
    public void Change_to_unused_code_skips_the_project()
    {
        _repo.Write("src/Shop/Report.cs", """
            namespace Shop;
            public class Report { public string Title() => Format.Heading("r"); }
            public static class Format { public static string Heading(string s) => s; }
            """);
        _repo.Commit("add report");
        _repo.Replace("src/Shop/Report.cs", "=> s;", "=> s.Trim();");

        // Heading reaches Report.Title, but nothing names Report and it has no base list or
        // attributes, so no framework can find it either: dead code.
        var selection = Select();

        Assert.Equal(TestRunMode.Skipped, selection.Mode);
    }

    [Fact]
    public void Removed_type_that_nothing_names_falls_back_to_the_whole_project()
    {
        File.Delete(Path.Combine(_repo.Root, "src/Shop/PingHandler.cs"));

        Assert.Equal(TestRunMode.All, Select().Mode);
    }

    [Fact]
    public void Private_member_names_do_not_spread_to_other_types()
    {
        // Both test classes have a private helper called Build; changing one must not select the other.
        _repo.Write("tests/Shop.Tests/ATests.cs", """
            namespace Shop.Tests;
            public class ATests { private static int Build() => 1; [Fact] public void A() => Assert.Equal(1, Build()); }
            """);
        _repo.Write("tests/Shop.Tests/BTests.cs", """
            namespace Shop.Tests;
            public class BTests { private static int Build() => 1; [Fact] public void B() => Assert.Equal(1, Build()); }
            """);
        _repo.Commit("add helpers");
        _repo.Replace("tests/Shop.Tests/ATests.cs", "=> 1;", "=> 2 - 1;");

        Assert.Equal(["Shop.Tests.ATests"], Select().TestClasses);
    }

    [Fact]
    public void Fakes_and_stubs_in_test_projects_are_not_test_classes()
    {
        _repo.Write("tests/Shop.Tests/FakeCalculator.cs", """
            namespace Shop.Tests;
            public class FakeCalculator { public decimal Tax(decimal net) => 0m; }
            """);
        _repo.Commit("add fake");
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        Assert.DoesNotContain("Shop.Tests.FakeCalculator", Select().TestClasses);
    }

    [Fact]
    public void Class_inheriting_tests_from_a_base_class_is_a_test_class()
    {
        _repo.Write("tests/Shop.Tests/Contracts.cs", """
            namespace Shop.Tests;
            public abstract class CalculatorContract { [Fact] public void Taxes() => Assert.True(new PriceCalculator().Tax(1m) > 0); }
            public class DefaultCalculatorTests : CalculatorContract { }
            """);
        _repo.Commit("add contract");
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        Assert.Contains("Shop.Tests.DefaultCalculatorTests", Select().TestClasses);
    }

    [Fact]
    public void Static_member_matches_only_code_that_names_its_type()
    {
        // Steps.All is a static field; LinqTests calls Enumerable's All and must not be selected.
        _repo.Write("src/Shop/Steps.cs", """
            namespace Shop;
            public static class Steps { public static readonly string[] All = ["a"]; }
            public static class StepCounter { public static int Count() => Steps.All.Length; }
            """);
        _repo.Write("tests/Shop.Tests/StepTests.cs", """
            namespace Shop.Tests;
            public class StepTests { [Fact] public void One() => Assert.Equal(1, StepCounter.Count()); }
            public class LinqTests { [Fact] public void AllPositive() => Assert.True(new[] { 1 }.All(x => x > 0)); }
            """);
        _repo.Commit("add steps");
        _repo.Replace("src/Shop/Steps.cs", "[\"a\"]", "[\"a\", \"b\"]");

        Assert.Equal(["Shop.Tests.StepTests"], Select().TestClasses);
    }

    [Fact]
    public void Extension_methods_match_without_their_type_name()
    {
        _repo.Write("src/Shop/TextExtensions.cs", """
            namespace Shop;
            public static class TextExtensions { public static string Shout(this string s) => s.ToUpperInvariant(); }
            """);
        _repo.Write("tests/Shop.Tests/ShoutTests.cs", """
            namespace Shop.Tests;
            public class ShoutTests { [Fact] public void Upper() => Assert.Equal("A", "a".Shout()); }
            """);
        _repo.Commit("add extension");
        _repo.Replace("src/Shop/TextExtensions.cs", "s.ToUpperInvariant()", "s.ToUpperInvariant() + \"\"");

        Assert.Equal(["Shop.Tests.ShoutTests"], Select().TestClasses);
    }

    [Fact]
    public void Unnamed_top_level_program_does_not_force_a_full_run()
    {
        // Tests reference the app project and exercise Slug directly; the entry point merely uses it.
        _repo.Write("src/Shop/Program.cs", """System.Console.WriteLine(Shop.Slug.Make("X"));""");
        _repo.Commit("add entry point");
        _repo.Replace("src/Shop/Slug.cs", "title.ToLowerInvariant()", "title.Trim().ToLowerInvariant()");

        Assert.Equal(["Shop.Tests.SlugTests"], Select().TestClasses);
    }

    [Fact]
    public void Coverage_map_narrows_a_member_change_to_the_classes_that_ran_it()
    {
        // By name, OrderServiceTests also reaches Tax (through Checkout and Total); the map says it never ran it.
        WriteMap(("Shop.Tests.TaxTests", [("Shop.PriceCalculator", "Tax")]),
                 ("Shop.Tests.OrderServiceTests", [("Shop.OrderService", "Checkout")]));
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        Assert.Equal(["Shop.Tests.TaxTests"], Select().TestClasses);
    }

    [Fact]
    public void Field_change_is_traced_to_its_readers_and_then_taken_from_coverage()
    {
        // Rates.Vat is a field, which coverage cannot see; its reader Tax is known to the map.
        _repo.Write("src/Shop/Rates.cs", """
            namespace Shop;
            public static class Rates { public static readonly decimal Vat = 0.2m; }
            """);
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * Rates.Vat");
        _repo.Commit("extract rate");
        WriteMap(("Shop.Tests.TaxTests", [("Shop.PriceCalculator", "Tax")]),
                 ("Shop.Tests.OrderServiceTests", [("Shop.OrderService", "Checkout")]));
        _repo.Replace("src/Shop/Rates.cs", "0.2m", "0.25m");

        Assert.Equal(["Shop.Tests.TaxTests"], Select().TestClasses);
    }

    [Fact]
    public void Code_run_by_fixture_setup_selects_the_classes_that_use_the_fixture()
    {
        _repo.Write("tests/Shop.Tests/HostTests.cs", """
            namespace Shop.Tests;
            public class ShopHost { public ShopHost() => new PriceCalculator().Tax(1m); }
            public class HostTests(ShopHost host) : IClassFixture<ShopHost> { [Fact] public void Starts() => Assert.NotNull(host); }
            """);
        _repo.Commit("add host");

        // Only the fixture ran Tax; no test class did.
        _fixtures["Shop.Tests.ShopHost"] = [2];
        WriteMap(("Shop.Tests.SlugTests", [("Shop.Slug", "Make")]));
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        Assert.Equal(["Shop.Tests.HostTests"], Select().TestClasses);
    }

    [Fact]
    public void Generic_fixture_is_matched_with_its_arguments()
    {
        // Host<CalcProfile> set up with the changed code; Host<SlugProfile> did not.
        _repo.Write("tests/Shop.Tests/Hosts.cs", """
            namespace Shop.Tests;
            public class Host<TProfile> { }
            public class CalcProfile { }
            public class SlugProfile { }
            public class CalcHostTests(Host<CalcProfile> host) : IClassFixture<Host<CalcProfile>> { [Fact] public void A() => Assert.NotNull(host); }
            public class SlugHostTests(Host<SlugProfile> host) : IClassFixture<Host<SlugProfile>> { [Fact] public void B() => Assert.NotNull(host); }
            """);
        _repo.Commit("add hosts");

        _fixtures["Shop.Tests.Host`1[Shop.Tests.CalcProfile]"] = [2];
        _fixtures["Shop.Tests.Host`1[Shop.Tests.SlugProfile]"] = [5];
        WriteMap(("Shop.Tests.SlugTests", [("Shop.Slug", "Make")]));
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        Assert.Equal(["Shop.Tests.CalcHostTests"], Select().TestClasses);
    }

    [Fact]
    public void Static_member_run_by_a_single_class_is_also_traced_through_its_callers()
    {
        // Make may cache its result per process: the map saw only SlugTests run it, although
        // ReportTests depends on it through Title.
        _repo.Write("src/Shop/Report.cs", """
            namespace Shop;
            public class Report { public string Title() => Slug.Make("Report"); }
            """);
        _repo.Write("tests/Shop.Tests/ReportTests.cs", """
            namespace Shop.Tests;
            public class ReportTests { [Fact] public void Titles() => Assert.Equal("report", new Report().Title()); }
            """);
        _repo.Commit("add report");

        WriteMap(("Shop.Tests.SlugTests", [("Shop.Slug", "Make")]),
                 ("Shop.Tests.ReportTests", [("Shop.Report", "Title")]));
        _repo.Replace("src/Shop/Slug.cs", "title.ToLowerInvariant()", "title.Trim().ToLowerInvariant()");

        Assert.Equal(["Shop.Tests.ReportTests", "Shop.Tests.SlugTests"], Select().TestClasses);
    }

    [Fact]
    public void Estimate_adds_up_the_selected_classes_and_their_fixtures()
    {
        _timings = new CoverageTimings(
            new Dictionary<string, double> { ["Shop.Tests.TaxTests"] = 2, ["Shop.Tests.OrderServiceTests"] = 3, ["Shop.Tests.SlugTests"] = 5 },
            new Dictionary<string, double>());
        WriteMap(("Shop.Tests.TaxTests", [("Shop.PriceCalculator", "Tax")]));
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var plan = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Plan;

        Assert.Equal(new TimeEstimate(10, 2), plan.Projects.Single().Estimate);
        Assert.Equal(new TimeEstimate(10, 2), plan.Estimate);
        Assert.Equal(0.8, plan.Estimate!.SavedFraction, 3);
    }

    [Fact]
    public void Member_no_test_ran_skips_the_project()
    {
        WriteMap(("Shop.Tests.TaxTests", [("Shop.PriceCalculator", "Tax")]));
        _repo.Replace("src/Shop/Slug.cs", "title.ToLowerInvariant()", "title.Trim().ToLowerInvariant()");

        Assert.Equal(TestRunMode.Skipped, Select().Mode);
    }

    [Fact]
    public void Additions_are_traced_by_name_even_with_a_map()
    {
        // The map knows nothing about Slug, and Twice is new: both are left to name tracing.
        WriteMap(("Shop.Tests.TaxTests", [("Shop.PriceCalculator", "Tax")]));
        _repo.Replace("src/Shop/Slug.cs", "public static string Make", "public static string Twice(string s) => s + s;\n    public static string Make");
        _repo.Replace("tests/Shop.Tests/SlugTests.cs", "Slug.Make(\"A\")", "Slug.Twice(Slug.Make(\"A\"))");

        Assert.Equal(["Shop.Tests.SlugTests"], Select().TestClasses);
    }

    [Fact]
    public void Map_recorded_at_another_commit_is_ignored()
    {
        WriteMap(("Shop.Tests.TaxTests", [("Shop.PriceCalculator", "Tax")]));
        _repo.Write("README.md", "moved on");
        _repo.Commit("later");
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        // The name-based answer includes OrderServiceTests, which the stale map would have left out.
        Assert.Contains("Shop.Tests.OrderServiceTests", Select().TestClasses);
    }

    [Fact]
    public void Reflection_discovered_type_stays_conservative_with_a_map()
    {
        WriteMap(("Shop.Tests.TaxTests", [("Shop.PingHandler", "Handle")]));
        _repo.Replace("src/Shop/PingHandler.cs", "\"pong\"", "\"PONG\"");

        Assert.Equal(TestRunMode.All, Select().Mode);
    }

    [Fact]
    public void Coverage_map_round_trips_through_the_store()
    {
        var map = new CoverageMap(
            new CoverageMapHeader("tests/A.Tests/A.Tests.csproj", "abc123", DateTimeOffset.Parse("2026-09-29T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            [new CoveredMethod("A.B", "Run"), new CoveredMethod("A.B`1", ".ctor")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["A.Tests.BTests"] = [0, 1] },
                new Dictionary<string, IReadOnlyList<int>> { ["A.Tests.Host`1"] = [1] },
                [1]),
            new CoverageTimings(
                new Dictionary<string, double> { ["A.Tests.BTests"] = 1.5 },
                new Dictionary<string, double> { ["A.Tests.Host`1"] = 0.25 }));
        var path = Path.Combine(_repo.Root, "map.json");

        CoverageMapStore.Write(path, map);
        var read = CoverageMapStore.Read(path)!;

        Assert.Equal(map.Methods, read.Methods);
        Assert.Equal(map.Classes["A.Tests.BTests"], read.Classes["A.Tests.BTests"]);
        Assert.Equal(map.Ambient, read.Ambient);
        Assert.Equal(map.Fixtures["A.Tests.Host`1"], read.Fixtures["A.Tests.Host`1"]);
        Assert.Equal(1.5, read.Timings!.ClassSeconds["A.Tests.BTests"]);
        Assert.Equal(0.25, read.Timings.FixtureSeconds["A.Tests.Host`1"]);
        Assert.Equal(map.Commit, read.Commit);
    }

    private CoverageMap Map(string commit, List<CoveredMethod> methods, Dictionary<string, IReadOnlyList<int>> byClass) =>
        new(new CoverageMapHeader("tests/Shop.Tests/Shop.Tests.csproj", commit, DateTimeOffset.UtcNow),
            methods,
            new CoverageHits(byClass, _fixtures, []),
            _timings);

    /// <summary>Timings the next <see cref="WriteMap"/> includes, or null for none.</summary>
    private CoverageTimings? _timings;

    /// <summary>Fixture hits the next <see cref="WriteMap"/> includes.</summary>
    private readonly Dictionary<string, IReadOnlyList<int>> _fixtures = [];

    /// <summary>Writes a coverage map for Shop.Tests recorded at the current HEAD (the merge base in these tests).</summary>
    private void WriteMap(params (string TestClass, (string Type, string Member)[] Methods)[] classes)
    {
        var methods = new List<CoveredMethod>
        {
            // Every production member is known to the map, run or not.
            new("Shop.PriceCalculator", ".ctor"), new("Shop.PriceCalculator", "Total"), new("Shop.PriceCalculator", "Tax"),
            new("Shop.OrderService", ".ctor"), new("Shop.OrderService", "Checkout"),
            new("Shop.Slug", "Make"), new("Shop.PingHandler", "Handle"), new("Shop.Report", "Title"),
        };

        var byClass = new Dictionary<string, IReadOnlyList<int>>();
        foreach (var (testClass, ran) in classes)
        {
            byClass[testClass] = [.. ran.Select(m => methods.IndexOf(new CoveredMethod(m.Type, m.Member)))];
        }

        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var path = CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Shop.Tests/Shop.Tests.csproj"));
        CoverageMapStore.Write(path, Map(head, methods, byClass));
        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"), ".sift/\n");
        _repo.Commit("ignore maps");

        // The map must describe the new HEAD, which is what 'main' points to.
        var newHead = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        CoverageMapStore.Write(path, Map(newHead, methods, byClass));
    }

    [Fact]
    public void Excluding_filter_matches_everything_outside_the_classes()
    {
        Assert.Equal("FullyQualifiedName!~A.B.&FullyQualifiedName!~A.C\\(1\\).", TestFilter.ExcludingClasses(["A.B", "A.C(1)"]));
    }

    [Fact]
    public void Filter_escapes_operators_and_ends_each_class_with_a_dot()
    {
        Assert.Equal(
            @"FullyQualifiedName~A.B.|FullyQualifiedName~A.Outer+Inner\(x\).",
            TestFilter.ForClasses(["A.B", "A.Outer+Inner(x)"]));
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

    public void Dispose() => _repo.Dispose();
}
