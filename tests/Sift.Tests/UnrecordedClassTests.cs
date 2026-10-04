using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// Test classes a coverage map never saw running: skipped where the map was recorded, or named
/// differently by the build than by the recording (docs/design/unrecorded-classes.md).
/// </summary>
public sealed class UnrecordedClassTests : IDisposable
{
    private const string Specs = "tests/Shop.Tests/Specs.generated.cs";

    /// <summary>Markdig's spec class as a Turkish-locale build names it.</summary>
    private const string TurkishInlines = "Shop.Tests.TestİnlinesSpec";

    private readonly GitRepoFixture _repo = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sift-unrecorded-" + Guid.NewGuid().ToString("N"));

    public UnrecordedClassTests()
    {
        _repo.Write("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Shop/PriceCalculator.cs", """
            namespace Shop;
            public class PriceCalculator
            {
                public decimal Tax(decimal net) => net * 0.2m;
            }
            """);
        _repo.Write("src/Shop/Slug.cs", """
            namespace Shop;
            public static class Slug
            {
                public static string Make(string title) => title.ToLowerInvariant();
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
        _repo.Write("tests/Shop.Tests/TaxTests.cs", """
            namespace Shop.Tests;
            public class TaxTests
            {
                [Fact] public void Is_twenty_percent() => Assert.Equal(2m, new PriceCalculator().Tax(10m));
            }
            """);

        // Lines 3-6 and 8-11; the generator spells the first class differently from one build to the next.
        _repo.Write(Specs, """
            namespace Shop.Tests;

            public class TestInlinesSpec
            {
                [Fact] public void Example1() => Assert.Equal("a", Slug.Make("A"));
            }

            public class TestBlocksSpec
            {
                [Fact] public void Example2() => Assert.Equal("b", Slug.Make("B"));
            }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Class_the_build_renamed_is_decided_by_the_record_at_its_place()
    {
        WriteMap(new MapSpec
        {
            Classes = { ["Shop.Tests.TaxTests"] = ["Tax"], [TurkishInlines] = ["Tax"], ["Shop.Tests.TestBlocksSpec"] = ["Make"] },
            Locations = { [TurkishInlines] = (Specs, 5), ["Shop.Tests.TestBlocksSpec"] = (Specs, 10) },
            Timings = new CoverageTimings(
                new Dictionary<string, double> { ["Shop.Tests.TaxTests"] = 1, [TurkishInlines] = 2, ["Shop.Tests.TestBlocksSpec"] = 4 },
                new Dictionary<string, double>()),
        });
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        // The record's name is not in the build, so a filter on it would run nothing.
        Assert.Equal(["Shop.Tests.TaxTests", "Shop.Tests.TestInlinesSpec"], selection.TestClasses);
        var inlines = selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestInlinesSpec");
        Assert.Equal(ClassDecisionKind.Coverage, inlines.Kind);
        Assert.EndsWith($"; recorded as {TurkishInlines}", inlines.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(selection.Decisions, static d => d.TestClass == TurkishInlines);
        Assert.False(selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestBlocksSpec").Selected);

        // Its recorded time counts for the selection too.
        Assert.Equal(3.0, selection.Estimate!.SelectedSeconds);
        Assert.Equal(7.0, selection.Estimate.FullSeconds);
    }

    [Fact]
    public void Class_the_build_renamed_keeps_its_record_when_that_ran_nothing_changed()
    {
        WriteMap(new MapSpec
        {
            Classes = { ["Shop.Tests.TaxTests"] = ["Tax"], [TurkishInlines] = ["Make"], ["Shop.Tests.TestBlocksSpec"] = ["Make"] },
            Locations = { [TurkishInlines] = (Specs, 5), ["Shop.Tests.TestBlocksSpec"] = (Specs, 10) },
        });
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        Assert.Equal(["Shop.Tests.TaxTests"], selection.TestClasses);
        var inlines = selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestInlinesSpec");
        Assert.Equal(ClassDecisionKind.Skipped, inlines.Kind);
        Assert.Contains($"recorded as {TurkishInlines}", inlines.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_records_at_one_place_select_the_class_and_say_to_record_again()
    {
        WriteMap(new MapSpec
        {
            Classes = { ["Shop.Tests.TaxTests"] = ["Tax"], [TurkishInlines] = ["Make"], [TurkishInlines + "2"] = ["Make"], ["Shop.Tests.TestBlocksSpec"] = ["Make"] },
            Locations = { [TurkishInlines] = (Specs, 5), [TurkishInlines + "2"] = (Specs, 4), ["Shop.Tests.TestBlocksSpec"] = (Specs, 10) },
        });
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        var inlines = selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestInlinesSpec");
        Assert.True(inlines.Selected);
        Assert.Equal(ClassDecisionKind.MapMismatch, inlines.Kind);
        Assert.Contains("record the map again", inlines.Evidence, StringComparison.Ordinal);
        Assert.False(selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestBlocksSpec").Selected);
    }

    [Fact]
    public void Record_at_no_class_of_the_file_makes_the_unknown_classes_of_that_file_disagree()
    {
        WriteMap(new MapSpec
        {
            Classes = { ["Shop.Tests.TaxTests"] = ["Tax"], [TurkishInlines] = ["Make"], ["Shop.Tests.TestBlocksSpec"] = ["Make"] },
            Locations = { [TurkishInlines] = (Specs, 1), ["Shop.Tests.TestBlocksSpec"] = (Specs, 10) },
        });
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var decisions = Select().Decisions.ToDictionary(static d => d.TestClass);

        Assert.Equal(ClassDecisionKind.MapMismatch, decisions["Shop.Tests.TestInlinesSpec"].Kind);
        Assert.Equal(ClassDecisionKind.Skipped, decisions["Shop.Tests.TestBlocksSpec"].Kind);
    }

    [Fact]
    public void Class_the_map_never_saw_stays_skipped_without_a_record_missing_from_its_file()
    {
        // HiddenTests is an orphan too (the index never parses it), but in another file.
        _repo.Write("tests/Shop.Tests/HiddenTests.cs", """
            #if NET6_0_OR_GREATER && !NETSTANDARD2_0
            namespace Shop.Tests;
            public class HiddenTests { [Fact] public void Taxes() => Assert.Equal(2m, new PriceCalculator().Tax(10m)); }
            #endif
            """);
        _repo.Commit("hidden tests");
        WriteMap(new MapSpec
        {
            Classes = { ["Shop.Tests.TaxTests"] = ["Tax"], ["Shop.Tests.HiddenTests"] = ["Tax"], ["Shop.Tests.TestBlocksSpec"] = ["Make"] },
            Locations = { ["Shop.Tests.HiddenTests"] = ("tests/Shop.Tests/HiddenTests.cs", 3), ["Shop.Tests.TestBlocksSpec"] = (Specs, 10) },
        });
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        Assert.Equal(["Shop.Tests.HiddenTests", "Shop.Tests.TaxTests"], selection.TestClasses);
        Assert.Equal(ClassDecisionKind.Skipped, selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestInlinesSpec").Kind);
    }

    [Fact]
    public void Map_that_does_not_know_its_classes_matches_nothing()
    {
        WriteMap(new MapSpec
        {
            Classes = { ["Shop.Tests.TaxTests"] = ["Tax"], [TurkishInlines] = ["Tax"], ["Shop.Tests.TestBlocksSpec"] = ["Make"] },
            Locations = { [TurkishInlines] = (Specs, 5), ["Shop.Tests.TestBlocksSpec"] = (Specs, 10) },
            Skipped = null,
        });
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        Assert.Equal(ClassDecisionKind.Skipped, selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestInlinesSpec").Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Skipped_class_runs_only_when_the_map_comes_from_another_platform(bool samePlatform)
    {
        WriteMap(new MapSpec
        {
            Classes = { ["Shop.Tests.TaxTests"] = ["Tax"], ["Shop.Tests.TestInlinesSpec"] = ["Make"] },
            Skipped = ["Shop.Tests.TestBlocksSpec"],
            Platform = samePlatform ? RuntimeInformation.RuntimeIdentifier : "other-x64",
        });
        _repo.Replace("src/Shop/PriceCalculator.cs", "net * 0.2m", "net * 0.25m");

        var selection = Select();

        var blocks = selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestBlocksSpec");
        Assert.Equal(!samePlatform, blocks.Selected);
        Assert.Equal(samePlatform ? ClassDecisionKind.Skipped : ClassDecisionKind.SkippedWhileRecording, blocks.Kind);
        Assert.Contains(samePlatform ? "this platform" : "other-x64", blocks.Evidence, StringComparison.Ordinal);
        Assert.False(selection.Decisions.Single(static d => d.TestClass == "Shop.Tests.TestInlinesSpec").Selected);
    }

    [Fact]
    public void Skipped_classes_platform_and_locations_round_trip_through_the_store()
    {
        var map = new CoverageMap(
            new CoverageMapHeader("tests/A.Tests/A.Tests.csproj", "abc", DateTimeOffset.UtcNow),
            [new CoveredMethod("A.B", "Run")],
            new CoverageHits(new Dictionary<string, IReadOnlyList<int>> { ["A.Tests.BTests"] = [0] }, new Dictionary<string, IReadOnlyList<int>>(), []))
        {
            SkippedClasses = new HashSet<string> { "A.Tests.LaterTests" },
            Platform = "osx-arm64",
            ClassLocations = new Dictionary<string, IReadOnlyList<ClassLocation>>
            {
                ["A.Tests.BTests"] = [new ClassLocation("tests/A.Tests/BTests.cs", 4), new ClassLocation("tests/A.Tests/BTests.More.cs", 2)],
            },
        };
        var path = Path.Combine(_repo.Root, "map.json");

        CoverageMapStore.Write(path, map);
        var read = CoverageMapStore.Read(path)!;

        Assert.Equal(["A.Tests.LaterTests"], read.SkippedClasses!);
        Assert.Equal("osx-arm64", read.Platform);
        Assert.Equal(map.ClassLocations["A.Tests.BTests"], read.ClassLocations!["A.Tests.BTests"]);
        Assert.True(read.KnowsClasses);

        CoverageMapStore.Write(path, map with { SkippedClasses = null, Platform = null, ClassLocations = null });
        var old = CoverageMapStore.Read(path)!;
        Assert.Null(old.SkippedClasses);
        Assert.Null(old.Platform);
        Assert.Null(old.ClassLocations);
        Assert.False(old.KnowsClasses);
    }

    [Fact]
    public void Trx_report_lists_classes_whose_tests_were_all_skipped()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "run.trx"), """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions>
                <UnitTest id="1"><TestMethod className="App.OrderTests, App.Tests" name="Places" /></UnitTest>
                <UnitTest id="2"><TestMethod className="App.LaterTests" name="Later" /></UnitTest>
                <UnitTest id="3"><TestMethod className="App.MixedTests" name="Runs" /></UnitTest>
                <UnitTest id="4"><TestMethod className="App.MixedTests" name="Skips" /></UnitTest>
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="1" outcome="Passed" />
                <UnitTestResult testId="2" outcome="NotExecuted" />
                <UnitTestResult testId="3" outcome="Passed" />
                <UnitTestResult testId="4" outcome="NotExecuted" />
              </Results>
            </TestRun>
            """);

        var outcome = TrxReport.Read(_directory)!;

        Assert.Equal(["App.LaterTests", "App.MixedTests", "App.OrderTests"], outcome.ReportedClasses.Order(StringComparer.Ordinal));
        Assert.Equal(["App.MixedTests", "App.OrderTests"], outcome.ExecutedClasses.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Class_locations_come_from_the_pdb_with_generated_code_counted_for_its_class()
    {
        Directory.CreateDirectory(_directory);
        var root = Path.Combine(_directory, "repo");
        var trees = new[]
        {
            Parse(Path.Combine(root, "tests/A/Specs.cs"), """
                namespace A;
                public class Outer
                {
                    public int Run() => Helper(() => 1);
                    private static int Helper(System.Func<int> f) => f();
                    public class Inner
                    {
                        public async System.Threading.Tasks.Task<int> WorkAsync() { await System.Threading.Tasks.Task.Yield(); return 2; }
                    }
                }
                """),
            Parse("/_/tests/A/Plain.cs", "namespace B; public class Plain { public int X() => 3; }"),
            Parse(Path.Combine(_directory, "elsewhere/Far.cs"), "namespace C; public class Far { public int Y() => 4; }"),
        };
        var core = typeof(object).Assembly.Location;
        var compilation = CSharpCompilation.Create(
            "A",
            trees,
            [MetadataReference.CreateFromFile(core), MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(core)!, "System.Runtime.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));
        var assembly = Path.Combine(_directory, "A.dll");
        using (var pe = File.Create(assembly))
        using (var pdb = File.Create(Path.Combine(_directory, "A.pdb")))
        {
            var emitted = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }

        var locations = ClassLocationReader.Read(assembly, root);

        Assert.Equal([new ClassLocation("tests/A/Specs.cs", 4)], locations["A.Outer"]);
        Assert.Equal([new ClassLocation("tests/A/Specs.cs", 8)], locations["A.Outer+Inner"]);
        Assert.Equal([new ClassLocation("tests/A/Plain.cs", 1)], locations["B.Plain"]);
        Assert.DoesNotContain("C.Far", locations.Keys);
    }

    /// <summary>Parses <paramref name="text"/> as the file at <paramref name="path"/>, with the encoding a PDB needs.</summary>
    private static SyntaxTree Parse(string path, string text) =>
        CSharpSyntaxTree.ParseText(text, path: path, encoding: Encoding.UTF8);

    /// <summary>What the next <see cref="WriteMap"/> records.</summary>
    private sealed class MapSpec
    {
        /// <summary>Test class to the members (of PriceCalculator or Slug) it ran.</summary>
        public Dictionary<string, string[]> Classes { get; } = [];

        /// <summary>Classes whose tests were all skipped; null for a map that does not know its classes.</summary>
        public HashSet<string>? Skipped { get; init; } = [];

        /// <summary>Platform the map was recorded on.</summary>
        public string Platform { get; init; } = RuntimeInformation.RuntimeIdentifier;

        /// <summary>Recorded times, or null for none.</summary>
        public CoverageTimings? Timings { get; init; }

        /// <summary>Test class to its file and first line.</summary>
        public Dictionary<string, (string File, int Line)> Locations { get; } = [];
    }

    /// <summary>Writes a map for Shop.Tests recorded at the current HEAD, the merge base of the change.</summary>
    private void WriteMap(MapSpec spec)
    {
        List<CoveredMethod> methods = [new("Shop.PriceCalculator", "Tax"), new("Shop.Slug", "Make")];
        var byClass = spec.Classes.ToDictionary(
            static c => c.Key,
            c => (IReadOnlyList<int>)[.. c.Value.Select(m => methods.FindIndex(x => x.MemberName == m))]);

        CoverageMap Map(string commit) =>
            new(new CoverageMapHeader("tests/Shop.Tests/Shop.Tests.csproj", commit, DateTimeOffset.UtcNow), methods,
                new CoverageHits(byClass, new Dictionary<string, IReadOnlyList<int>>(), []), spec.Timings)
            {
                SkippedClasses = spec.Skipped,
                Platform = spec.Platform,
                ClassLocations = spec.Locations.ToDictionary(
                    static l => l.Key, static l => (IReadOnlyList<ClassLocation>)[new ClassLocation(l.Value.File, l.Value.Line)]),
            };

        var path = CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Shop.Tests/Shop.Tests.csproj"));
        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"), ".sift/\n");
        _repo.Commit("ignore maps");
        CoverageMapStore.Write(path, Map(new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root)));
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
