using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A map of an earlier format is not read: its project is selected without coverage, which selects more, never less,
/// until the map is recorded again (a format 2 map may miss a test that built a fixture type itself).
/// </summary>
public sealed class MapFormatTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public MapFormatTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", """
            namespace Lib.Tests;
            public class CalcTests { [Fact] public void Taxes() => Assert.Equal(2, new Lib.Calc().Tax()); }
            public class OtherTests { [Fact] public void Runs() => Assert.True(true); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_map_of_this_format_selects_by_coverage()
    {
        var path = WriteMap();
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 3; }");

        Assert.Equal(CoverageMapStore.Version, CoverageMapStore.FormatOf(path));
        var selection = Select();
        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Lib.Tests.CalcTests"], selection.TestClasses);
        Assert.Equal(ClassDecisionKind.Coverage, selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.CalcTests").Kind);
    }

    [Fact]
    public void A_map_of_an_earlier_format_is_ignored_and_the_project_selected_without_it()
    {
        var path = WriteMap();
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"version\":{CoverageMapStore.Version},", "\"version\":2,", StringComparison.Ordinal));
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 3; }");

        Assert.Equal(2, CoverageMapStore.FormatOf(path));
        var exception = Assert.Throws<InvalidDataException>(() => CoverageMapStore.Read(path));
        Assert.Contains("record the maps again", exception.Message, StringComparison.Ordinal);
        var selection = Select();
        // Without the map only the code decides: the class naming the changed type, by name.
        Assert.Equal(["Lib.Tests.CalcTests"], selection.TestClasses);
        Assert.Equal(ClassDecisionKind.Name, selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.CalcTests").Kind);
    }

    [Fact]
    public void A_file_that_does_not_start_with_its_format_has_none()
    {
        var path = Path.Combine(_repo.Root, "other.json");
        File.WriteAllText(path, """{"testProject":"T.csproj","version":3}""");

        Assert.Null(CoverageMapStore.FormatOf(path));
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single();

    private string WriteMap()
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0], ["Lib.Tests.OtherTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
        };
        var path = CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj"));
        CoverageMapStore.Write(path, map);
        return path;
    }
}
