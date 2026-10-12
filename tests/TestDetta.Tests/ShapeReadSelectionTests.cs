using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A change to the metadata of a type that only carries data selects the classes that ran its constructor or accessors
/// and the ones that read its shape, instead of tracing its name through the endpoint registrations every host runs
/// (docs/design/shape-reads.md). A map that did not track shape reads, or saw the shape read outside the tests, keeps
/// the name trace.
/// </summary>
public sealed class ShapeReadSelectionTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public ShapeReadSelectionTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Detail.cs", "namespace Lib; public sealed record Detail(int Id, string Name);");
        _repo.Write("src/Lib/Routes.cs", "namespace Lib; public static class Routes { public static System.Type Response() => typeof(Detail); }");
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
            public class SerializeTests { [Fact] public void Reads() => Assert.Equal(1, new Lib.Detail(1, "a").Id); }
            public class SchemaTests { [Fact] public void Lists() => Assert.NotNull(Lib.Routes.Response()); }
            public class PingTests { [Fact] public void Pings() => Assert.NotNull(Lib.Routes.Response()); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Only_the_classes_that_ran_or_read_the_record_are_selected()
    {
        WriteMap(tracksShapes: true, schemaRead: [CoverageMap.OutputShapePrefix + "Lib.Detail"], ambient: []);
        ChangeRecord();

        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Lib.Tests.SchemaTests", "Lib.Tests.SerializeTests"], selection.TestClasses.Order(StringComparer.Ordinal));
        Assert.Contains("read the shape of Lib.Detail", selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.SchemaTests").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_that_did_not_track_shape_reads_keeps_the_name_trace()
    {
        WriteMap(tracksShapes: false, schemaRead: [], ambient: []);
        ChangeRecord();

        Assert.True(Runs(Select(), "Lib.Tests.PingTests"));
    }

    [Fact]
    public void A_shape_read_outside_every_test_keeps_the_name_trace()
    {
        WriteMap(tracksShapes: true, schemaRead: [], ambient: [CoverageMap.OutputShapePrefix + "Lib.Detail"]);
        ChangeRecord();

        Assert.True(Runs(Select(), "Lib.Tests.PingTests"));
    }

    [Fact]
    public void A_type_with_code_is_no_data_carrier()
    {
        _repo.Write("src/Lib/Detail.cs", "namespace Lib; public sealed record Detail(int Id, string Name) { public string Label() => Name; }");
        _repo.Commit("code");
        WriteMap(tracksShapes: true, schemaRead: [CoverageMap.OutputShapePrefix + "Lib.Detail"], ambient: []);
        _repo.Write("src/Lib/Detail.cs", "namespace Lib; public sealed record Detail(int Id, string Name, string? Note = null) { public string Label() => Name; }");

        Assert.True(Runs(Select(), "Lib.Tests.PingTests"));
    }

    [Fact]
    public void An_attribute_class_is_no_data_carrier()
    {
        // Its metadata acts where other types are read, which no shape read of it names.
        _repo.Write("src/Lib/Detail.cs", "namespace Lib; public sealed record Detail(int Id, string Name) : System.Attribute;");
        _repo.Commit("attribute");
        WriteMap(tracksShapes: true, schemaRead: [CoverageMap.OutputShapePrefix + "Lib.Detail"], ambient: []);
        _repo.Write("src/Lib/Detail.cs", "namespace Lib; [System.AttributeUsage(System.AttributeTargets.Class)] public sealed record Detail(int Id, string Name) : System.Attribute;");

        Assert.True(Runs(Select(), "Lib.Tests.PingTests"));
    }

    private static bool Runs(TestProjectSelection selection, string testClass) =>
        selection.Mode == TestRunMode.All || selection.TestClasses.Contains(testClass);

    private void ChangeRecord() => _repo.Write("src/Lib/Detail.cs", "namespace Lib; public sealed record Detail(int Id, string Name, string? Note = null);");

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single();

    private void WriteMap(bool tracksShapes, IReadOnlyList<string> schemaRead, IReadOnlyList<string> ambient)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        // Every class ran Routes.Response (the code that names the record, as endpoint registrations do); only
        // SerializeTests ran the record's constructor and accessors.
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Detail", ".ctor"), new CoveredMethod("Lib.Detail", "Id"), new CoveredMethod("Lib.Routes", "Response")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>>
                {
                    ["Lib.Tests.SerializeTests"] = [0, 1, 2],
                    ["Lib.Tests.SchemaTests"] = [2],
                    ["Lib.Tests.PingTests"] = [2],
                },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            TracksShapeReads = tracksShapes,
            OutputLoadsUnseen = [],
            OutputUses = new FileReads(
                new Dictionary<string, IReadOnlyList<string>> { ["Lib.Tests.SchemaTests"] = schemaRead },
                new Dictionary<string, IReadOnlyList<string>>(),
                ambient,
                []),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
