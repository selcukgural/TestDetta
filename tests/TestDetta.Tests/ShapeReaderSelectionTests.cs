using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A class the map saw read a type's shape is selected for a change to that type's metadata, also when it names neither
/// the type nor anything that runs it: OData's routing reads [ODataAttributeRouting] of ODataController through the
/// controllers derived from it, and two classes doing so were missed by a shape mutant (2026-10-12).
/// </summary>
public sealed class ShapeReaderSelectionTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public ShapeReaderSelectionTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Controllers.cs", """
            namespace Lib;
            [System.Obsolete("routing")] public abstract class RoutedBase { public int Id() => 1; }
            public static class Conventions { public static int Count(System.Type type) => type.GetCustomAttributes(true).Length; }
            """);
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
            public class ConventionTests { [Fact] public void Counts() => Assert.Equal(1, Lib.Conventions.Count(typeof(Probe))); }
            public class PlainTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_class_that_read_the_shape_is_selected_for_a_metadata_change()
    {
        WriteMap([CoverageMap.OutputShapePrefix + "Lib.RoutedBase"]);
        _repo.Write("src/Lib/Controllers.cs", """
            namespace Lib;
            public abstract class RoutedBase { public int Id() => 1; }
            public static class Conventions { public static int Count(System.Type type) => type.GetCustomAttributes(true).Length; }
            """);

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single();

        Assert.True(selection.Mode == TestRunMode.All || selection.TestClasses.Contains("Lib.Tests.ConventionTests"), $"{selection.Mode}: {selection.Reason}");
        if (selection.Mode == TestRunMode.Filtered)
        {
            Assert.Contains("read the shape of Lib.RoutedBase", selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.ConventionTests").Evidence, StringComparison.Ordinal);
        }
    }

    private void WriteMap(IReadOnlyList<string> conventionUses)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        // ConventionTests ran Conventions.Count on a type derived from RoutedBase; nothing names RoutedBase in the tests.
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Conventions", "Count"), new CoveredMethod("Lib.RoutedBase", "Id")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.ConventionTests"] = [0], ["Lib.Tests.PlainTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            OutputLoadsUnseen = [],
            OutputUses = new FileReads(
                new Dictionary<string, IReadOnlyList<string>> { ["Lib.Tests.ConventionTests"] = conventionUses },
                new Dictionary<string, IReadOnlyList<string>>(),
                [],
                []),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
