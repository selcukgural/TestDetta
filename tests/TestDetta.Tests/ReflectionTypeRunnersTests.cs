using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A change in a type a framework may find through reflection is more than its map can answer, but the classes the map
/// says ran the changed code are affected all the same: they are selected, and the change is traced by name as well. The
/// type's whole-project fallback needs its name spread, which a member named elsewhere does not do (an extension method,
/// an F# AutoOpen module's function), so without them a class that ran the code through a path no name shows was left
/// out (Argu's CliParser, docs/design/reflection-types-with-coverage.md).
/// </summary>
public sealed class ReflectionTypeRunnersTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public ReflectionTypeRunnersTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Steps.cs", """namespace Lib; [System.ComponentModel.Description("steps")] public static class StepsExtensions { public static int Triple(this int x) => x * 3; }""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Nine() => 3.Triple(); public int Other() => 1; }");
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
            public class CalcTests { [Fact] public void Nines() => Assert.Equal(9, new Lib.Calc().Nine()); }
            public class HiddenTests { [Fact] public void Triples() => Assert.Equal(6, typeof(Lib.Calc).Assembly.GetType("Lib.StepsExtensions")!.GetMethod("Triple")!.Invoke(null, [2])); }
            public class OtherTests { [Fact] public void Ones() => Assert.Equal(1, new Lib.Calc().Other()); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void The_classes_that_ran_a_changed_member_of_a_reflection_found_type_are_selected()
    {
        WriteMap();
        _repo.Replace("src/Lib/Steps.cs", "x * 3", "x + x + x");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Contains("Lib.Tests.HiddenTests", selection.TestClasses);
        Assert.Contains("Lib.Tests.CalcTests", selection.TestClasses);
        Assert.DoesNotContain("Lib.Tests.OtherTests", selection.TestClasses);
    }

    /// <summary>A map in which CalcTests ran Calc.Nine and the extension, HiddenTests the extension alone, OtherTests Calc.Other.</summary>
    private void WriteMap()
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Nine"), new CoveredMethod("Lib.StepsExtensions", "Triple"), new CoveredMethod("Lib.Calc", "Other")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0, 1], ["Lib.Tests.HiddenTests"] = [1], ["Lib.Tests.OtherTests"] = [2] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
