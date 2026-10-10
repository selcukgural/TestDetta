using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A change that reaches a source generator in the repository is decided by what the generator writes
/// now, compared with what the test project's map recorded (docs/design/generator-output-changes.md),
/// instead of running every project that uses the generator.
/// </summary>
public sealed class GeneratorOutputTests : IDisposable
{
    private const string Mapper = "namespace Lib.Tests; public partial class Mapper { public int Map() => 1; }";

    private readonly GitRepoFixture _repo = new();

    public GeneratorOutputTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
        // The generator compiles the library's sources in, as protobuf-net's BuildTools does.
        _repo.Write("src/Gen/Gen.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Lib/**/*.cs" Link="Lib" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Gen/Generator.cs", "namespace Gen; public class Generator { public string Emit() => \"partial class Mapper { }\"; }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                <ProjectReference Include="../../src/Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", """
            namespace Lib.Tests;
            public class CalcTests { [Fact] public void Taxes() => Assert.Equal(2, new Lib.Calc().Tax()); }
            public class MapperTests { [Fact] public void Maps() => Assert.Equal(1, new Mapper().Map()); }
            public class PlainTests { [Fact] public void Holds() => Assert.True(true); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");

        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax"), new CoveredMethod("Lib.Tests.Mapper", "Map")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0], ["Lib.Tests.MapperTests"] = [1], ["Lib.Tests.PlainTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            Generated = [Generated("obj/Debug/net10.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper)],
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }

    [Fact]
    public void Generator_whose_output_is_the_same_changes_nothing_through_it()
    {
        // Another configuration puts the file under another directory; the generator and hint name match.
        var reader = new FakeReader(GeneratedCodeRead.Read([Generated("obj/Release/net10.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper)]));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select(reader);

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode} {selection.Reason}");
        Assert.Equal(["Lib.Tests.CalcTests"], selection.TestClasses);
        Assert.Equal([("tests/Lib.Tests/Lib.Tests.csproj", (string?)null)], reader.Calls);
    }

    [Fact]
    public void Generated_member_that_changed_selects_the_classes_that_ran_it()
    {
        var reader = new FakeReader(GeneratedCodeRead.Read([Generated("obj/Debug/net10.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper.Replace("=> 1", "=> 2", StringComparison.Ordinal))]));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select(reader);

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode} {selection.Reason}");
        Assert.Equal(["Lib.Tests.CalcTests", "Lib.Tests.MapperTests"], selection.TestClasses);
    }

    [Fact]
    public void Build_that_fails_ends_the_analysis_with_the_compiler_errors()
    {
        var reader = new FakeReader(GeneratedCodeRead.Failed("Calc.cs(1,40): error CS1002: ; expected"));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var error = Assert.Throws<GeneratedCodeBuildException>(() => Select(reader));

        Assert.Contains("error CS1002", error.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet test would fail the same way", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_code_that_cannot_be_read_runs_the_project_completely_and_says_why()
    {
        var reader = new FakeReader(GeneratedCodeRead.NotAvailable("its assembly has no portable PDB (DebugType none)"));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select(reader);

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("as a source generator, and its assembly has no portable PDB", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_project_with_several_frameworks_runs_completely_without_a_build()
    {
        // Its map holds net8.0's generated code; the generator may write other code for net10.0.
        var path = CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj"));
        CoverageMapStore.Write(path, CoverageMapStore.Read(path)! with { Framework = "net8.0" });
        var reader = new FakeReader(GeneratedCodeRead.Read([Generated("obj/Debug/net8.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper)]));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select(reader);

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("targets several frameworks while its map holds the generated code of net8.0 only", selection.Reason, StringComparison.Ordinal);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public void Test_project_with_several_frameworks_is_compared_framework_by_framework()
    {
        // Only net10.0's generated code changes; dotnet test runs that framework too.
        var path = CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj"));
        CoverageMapStore.Write(path, CoverageMapStore.Read(path)! with
        {
            Framework = "net8.0",
            GeneratedFrameworks = ["net8.0", "net10.0"],
            Generated =
            [
                Generated("obj/Debug/net8.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper) with { Framework = "net8.0" },
                Generated("obj/Debug/net10.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper) with { Framework = "net10.0" },
            ],
        });
        var reader = new FakeReader(framework => GeneratedCodeRead.Read(
            [Generated($"obj/Debug/{framework}/generated/Gen/Gen.Generator/Mapper.g.cs", framework == "net10.0" ? Mapper.Replace("=> 1", "=> 2", StringComparison.Ordinal) : Mapper)]));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select(reader);

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode} {selection.Reason}");
        Assert.Equal(["Lib.Tests.CalcTests", "Lib.Tests.MapperTests"], selection.TestClasses);
        Assert.Equal(["net10.0", "net8.0"], reader.Calls.Select(static c => c.Framework).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_run_of_one_framework_compares_only_that_frameworks_generated_code()
    {
        // Recorded with -f net8.0, run with -f net8.0: net10.0's tests do not run, so its code does not matter.
        var path = CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj"));
        CoverageMapStore.Write(path, CoverageMapStore.Read(path)! with
        {
            Framework = "net8.0",
            Generated = [Generated("obj/Debug/net8.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper) with { Framework = "net8.0" }],
        });
        var reader = new FakeReader(framework => GeneratedCodeRead.Read([Generated($"obj/Debug/{framework}/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper)]));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select(reader, framework: "net8.0");

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode} {selection.Reason}");
        Assert.DoesNotContain("Lib.Tests.MapperTests", selection.TestClasses);
        Assert.Equal(["net8.0"], reader.Calls.Select(static c => c.Framework));
    }

    [Fact]
    public void A_run_of_a_framework_the_map_holds_no_generated_code_of_runs_completely()
    {
        var path = CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj"));
        CoverageMapStore.Write(path, CoverageMapStore.Read(path)! with
        {
            Framework = "net8.0",
            Generated = [Generated("obj/Debug/net8.0/generated/Gen/Gen.Generator/Mapper.g.cs", Mapper) with { Framework = "net8.0" }],
        });
        var reader = new FakeReader(GeneratedCodeRead.Failed("must not be called"));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select(reader, framework: "net10.0");

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("its map holds no generated code of net10.0, the framework the tests run for", selection.Reason, StringComparison.Ordinal);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public void Change_that_does_not_reach_a_generator_builds_nothing()
    {
        var reader = new FakeReader(GeneratedCodeRead.Failed("must not be called"));
        _repo.Replace("tests/Lib.Tests/Tests.cs", "Assert.True(true)", "Assert.False(false)");

        var selection = Select(reader);

        Assert.Empty(reader.Calls);
        Assert.Equal(["Lib.Tests.PlainTests"], selection.TestClasses);
    }

    private TestProjectSelection Select(FakeReader reader, string? framework = null) =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions { GeneratedCode = reader, Framework = framework }).Tests.Single();

    private static GeneratedSource Generated(string name, string text) => new("tests/Lib.Tests/Lib.Tests.csproj", name, text);

    public void Dispose() => _repo.Dispose();

    /// <summary>Answers each read for its framework and remembers which project and framework were asked for.</summary>
    private sealed class FakeReader(Func<string?, GeneratedCodeRead> result) : IGeneratedCodeReader
    {
        public FakeReader(GeneratedCodeRead result)
            : this(_ => result)
        {
        }

        public List<(string Project, string? Framework)> Calls { get; } = [];

        public GeneratedCodeRead Read(ProjectGraph graph, string repoRoot, ProjectNode testProject, string? framework)
        {
            Calls.Add((Path.GetRelativePath(repoRoot, testProject.Path).Replace('\\', '/'), framework));
            return result(framework);
        }
    }
}
