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
