using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A map knows what its recording instrumented (<see cref="CoverageMap.InstrumentedProjects"/>). A file linked into two
/// assemblies compiles the same names into both; when the recording instrumented only one, the map knows the names
/// from that one, and must not answer for the other: a change there is traced by name (meziantou-analyzer, round
/// runs-full-1006, mutant 17: OverloadFinder linked into the analyzer and the code fixers, the map had only one).
/// </summary>
public sealed class InstrumentedProjectsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public InstrumentedProjectsTests()
    {
        _repo.Write("src/A/A.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/A/Shared/Helper.cs", "namespace Lib; internal static class Helper { public static int Run() => 1; }");
        _repo.Write("src/A/AWrapper.cs", "namespace Lib; public static class AWrapper { public static int Go() => Helper.Run(); }");
        _repo.Write("src/B/B.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../A/Shared/Helper.cs" LinkBase="Shared" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/B/BWrapper.cs", "namespace LibB; public static class BWrapper { public static int Go() => Lib.Helper.Run(); }");
        _repo.Write("tests/T/T.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/A/A.csproj" />
                <ProjectReference Include="../../src/B/B.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/T/Tests.cs", """
            namespace T;
            public class UsesATests { [Fact] public void Goes() => Assert.Equal(1, Lib.AWrapper.Go()); }
            public class UsesBTests { [Fact] public void Goes() => Assert.Equal(1, LibB.BWrapper.Go()); }
            public class PlainTests { [Fact] public void Holds() => Assert.True(true); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_linked_file_whose_second_assembly_the_map_did_not_instrument_is_traced_by_name_there()
    {
        WriteMap(["src/A/A.csproj", "tests/T/T.csproj"]);
        _repo.Replace("src/A/Shared/Helper.cs", "=> 1", "=> 2");

        var selection = Select();

        Assert.True(selection.Mode == TestRunMode.Filtered, selection.Reason);
        Assert.Equal(["T.UsesATests", "T.UsesBTests"], selection.TestClasses.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_map_that_instrumented_both_assemblies_answers_for_both()
    {
        WriteMap(["src/A/A.csproj", "src/B/B.csproj", "tests/T/T.csproj"]);
        _repo.Replace("src/A/Shared/Helper.cs", "=> 1", "=> 2");

        Assert.Equal(["T.UsesATests"], Select().TestClasses);
    }

    [Fact]
    public void A_map_recorded_before_it_listed_what_it_instrumented_answers_as_before()
    {
        WriteMap(instrumented: null);
        _repo.Replace("src/A/Shared/Helper.cs", "=> 1", "=> 2");

        Assert.Equal(["T.UsesATests"], Select().TestClasses);
    }

    [Fact]
    public void The_list_survives_the_map_file()
    {
        WriteMap(["src/A/A.csproj", "tests/T/T.csproj"]);

        var map = CoverageMapStore.Read(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/T/T.csproj")))!;

        Assert.Equal(["src/A/A.csproj", "tests/T/T.csproj"], map.InstrumentedProjects);
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

    private void WriteMap(string[]? instrumented)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/T/T.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Helper", "Run"), new CoveredMethod("Lib.AWrapper", "Go")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["T.UsesATests"] = [0, 1], ["T.UsesBTests"] = [], ["T.PlainTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            InstrumentedProjects = instrumented,
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/T/T.csproj")), map);
    }
}
