using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// An incremental map holds members its carried-over entries could not have seen as unobserved
/// (docs/design/incremental-recording.md): a change to one is traced by name, never answered by entries recorded before
/// the member looked as it does now.
/// </summary>
public sealed class UnobservedMemberTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public UnobservedMemberTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Helper.cs", "namespace Lib; public static class Helper { public static int Run() => 1; }");
        _repo.Write("src/Lib/Wrapper.cs", "namespace Lib; public static class Wrapper { public static int Go() => Helper.Run(); }");
        _repo.Write("tests/T/T.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/T/Tests.cs", """
            namespace T;
            public class GoTests { [Fact] public void Goes() => Assert.Equal(1, Lib.Wrapper.Go()); }
            public class PlainTests { [Fact] public void Holds() => Assert.True(true); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    // GoTests' entry was carried over from before Helper.Run existed, so it does not name it.
    private void WriteMap(IReadOnlyList<int>? unobserved)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/T/T.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Helper", "Run"), new CoveredMethod("Lib.Wrapper", "Go")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["T.GoTests"] = [1], ["T.PlainTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            Unobserved = unobserved,
            Lineage = unobserved is null ? null : new MapLineage("0123456789abcdef", DateTimeOffset.UtcNow, 2),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/T/T.csproj")), map);
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

    [Fact]
    public void A_change_to_an_unobserved_member_is_traced_by_name()
    {
        WriteMap(unobserved: [0]);
        _repo.Replace("src/Lib/Helper.cs", "=> 1", "=> 2");

        var selection = Select();

        Assert.Equal(["T.GoTests"], selection.TestClasses);
        Assert.Contains(selection.Decisions, static d => d.TestClass == "T.GoTests" && d.Selected);
    }

    [Fact]
    public void Without_the_unobserved_mark_the_carried_entries_would_answer_that_no_class_runs_it()
    {
        WriteMap(unobserved: null);
        _repo.Replace("src/Lib/Helper.cs", "=> 1", "=> 2");

        Assert.Empty(Select().TestClasses);
    }

    [Fact]
    public void Unobserved_members_and_the_lineage_survive_the_map_file()
    {
        WriteMap(unobserved: [0]);

        var map = CoverageMapStore.Read(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/T/T.csproj")))!;

        Assert.Equal([0], map.Unobserved);
        Assert.Equal(2, map.Lineage!.Steps);
        Assert.Equal("0123456789abcdef", map.Lineage.FullCommit);
    }
}
