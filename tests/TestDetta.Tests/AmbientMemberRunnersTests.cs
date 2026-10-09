using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A member that also ran outside any test cannot be answered by coverage alone, so it is traced by name; the classes
/// the map saw running it stay selected all the same. FsCheck (app tier, 2026-10-10) finds its default arbitraries by
/// reflection: <c>Default.DoNotShrink</c> ran in a cache fill and in <c>Arbitrary</c>'s tests, nothing names it, and the
/// name trace alone selected nothing.
/// </summary>
public sealed class AmbientMemberRunnersTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public AmbientMemberRunnersTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Default.cs", "namespace Lib; public static class Default { public static int DoNotShrink() => 1; }");
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
            public class ArbitraryTests { [Fact] public void Finds() => Assert.NotNull(System.Type.GetType("Lib.De" + "fault, Lib")!.GetMethod("Do" + "NotShrink")!.Invoke(null, null)); }
            public class PlainTests { [Fact] public void Holds() => Assert.True(true); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_member_that_also_ran_outside_any_test_keeps_the_classes_that_ran_it()
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/T/T.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Default", "DoNotShrink")],
            // ArbitraryTests ran it, and it also ran before the first test (a registry filled by reflection).
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["T.ArbitraryTests"] = [0], ["T.PlainTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                [0]),
            null)
        {
            FailedClasses = new HashSet<string>(),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/T/T.csproj")), map);
        _repo.Replace("src/Lib/Default.cs", "=> 1", "=> 2");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single(static t => t.Project.IsTestProject);

        Assert.Equal(["T.ArbitraryTests"], selection.TestClasses);
    }
}
