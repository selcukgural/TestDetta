using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A test project that runs completely whatever coverage says (it uses a changed generator) has no say
/// in whether coverage can answer for a change. Its map cannot know code that ran in the compiler, and
/// that alone sent the change into the name trace for every other project (Mapperly: the integration
/// tests use the generator; a polyfill's unrelated <c>Version.Build</c> then ran the main tests completely).
/// </summary>
public sealed class FullRunCoverageTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public FullRunCoverageTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
        // Another Tax, which a name lookup cannot tell from Calc.Tax.
        _repo.Write("src/Lib/Invoice.cs", "namespace Lib; public class Invoice { public int Tax => 3; public string Print() => $\"{Tax}\"; }");
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
            public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2, new Lib.Calc().Tax()); }
            public class PrintTests { [Fact] public void Prints() => Assert.Equal("3", new Lib.Invoice().Print()); }
            """);
        _repo.Write("tests/Gen.Tests/Gen.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <!-- The library is itself the generator, as Riok.Mapperly is for its integration tests. -->
                <ProjectReference Include="../../src/Lib/Lib.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Gen.Tests/GeneratedTests.cs", "namespace Gen.Tests; public class GeneratedTests { [Fact] public void Runs() => Assert.True(true); }");
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    [Fact]
    public void Map_of_a_project_that_runs_completely_does_not_send_the_change_into_the_name_trace()
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        WriteMap("tests/Lib.Tests/Lib.Tests.csproj", head,
            [new("Lib.Calc", "Tax"), new("Lib.Invoice", "Tax"), new("Lib.Invoice", "Print")],
            new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.TaxTests"] = [0], ["Lib.Tests.PrintTests"] = [1, 2] });
        // Only generated code runs in Gen.Tests: its map knows nothing of the library.
        WriteMap("tests/Gen.Tests/Gen.Tests.csproj", head,
            [new("Gen.Tests.GeneratedTests", "Runs")],
            new Dictionary<string, IReadOnlyList<int>> { ["Gen.Tests.GeneratedTests"] = [0] });
        _repo.Replace("src/Lib/Calc.cs", "=> 2;", "=> 4;");

        var tests = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests;

        var generator = Assert.Single(tests, static t => t.Project.Path.EndsWith("Gen.Tests.csproj", StringComparison.Ordinal));
        Assert.Equal(TestRunMode.All, generator.Mode);
        var library = Assert.Single(tests, static t => t.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));
        Assert.True(library.Mode == TestRunMode.Filtered, $"{library.Mode} {library.Reason}");
        Assert.Equal(["Lib.Tests.TaxTests"], library.TestClasses);
    }

    /// <summary>Writes a usable map of <paramref name="project"/> recorded at <paramref name="commit"/>.</summary>
    private void WriteMap(string project, string commit, List<CoveredMethod> methods, Dictionary<string, IReadOnlyList<int>> classes)
    {
        var map = new CoverageMap(
            new CoverageMapHeader(project, commit, DateTimeOffset.UtcNow),
            methods,
            new CoverageHits(classes, new Dictionary<string, IReadOnlyList<int>>(), []),
            null)
        {
            FailedClasses = new HashSet<string>(),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, project)), map);
    }

    public void Dispose() => _repo.Dispose();
}
