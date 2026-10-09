using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// An output loaded or a file read while initializing a value stored into a tracked field belongs to
/// the readers of that field, as the code run there does (docs/design/runtime-file-reads.md,
/// initialization): AfterApply's architecture tests load the layers' assemblies in their own static
/// fields, which ran the whole unit test project for every change to a layer. A use whose value
/// leaves through no tracked field stays initialization, which runs the whole project. Slow (it runs
/// dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class InitializationUseTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public InitializationUseTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        foreach (var tool in (ReadOnlySpan<string>)["Helper", "Loose"])
        {
            _repo.Write($"tools/{tool}/{tool}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            _repo.Write($"tools/{tool}/Marker.cs", $"namespace {tool}; public static class Marker {{ }}");
        }

        _repo.Write("testdata/own.txt", "own\n");
        _repo.Write("testdata/shared.txt", "shared\n");
        _repo.Write("testdata/loose.txt", "loose\n");
        _repo.Write("tests/Init.Tests/Init.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../tools/Helper/Helper.csproj" ReferenceOutputAssembly="false" />
                <ProjectReference Include="../../tools/Loose/Loose.csproj" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Init.Tests/Tests.cs", """
            using System.Reflection;
            using Xunit;
            namespace Init.Tests;

            static class Repo
            {
                public static string Path(params string[] parts)
                {
                    var root = AppContext.BaseDirectory;
                    while (!File.Exists(System.IO.Path.Combine(root, "global.json"))) root = System.IO.Path.GetDirectoryName(root)!;
                    return System.IO.Path.Combine([root, .. parts]);
                }

                public static string Tool(string name) => Path("tools", name, "bin", "Debug", "net10.0", name + ".dll");
            }

            // Loads and reads in its own static fields, as AfterApply's LayerDependencyTests does.
            public class OwnFieldTests
            {
                private static readonly Assembly Helper = Assembly.LoadFrom(Repo.Tool("Helper"));
                private static readonly string Own = File.ReadAllText(Repo.Path("testdata", "own.txt"));

                [Fact] public void Loads() => Assert.Equal("Helper", Helper.GetName().Name);
                [Fact] public void Reads() => Assert.Equal("own", Own.Trim());
            }

            // A shared holder outside the tests: whoever reads its fields uses what built them.
            public static class Holder
            {
                public static readonly Assembly Helper = Assembly.LoadFrom(Repo.Tool("Helper"));
                public static readonly string Shared = File.ReadAllText(Repo.Path("testdata", "shared.txt"));
            }

            public class ReaderTests
            {
                [Fact] public void Loads() => Assert.Equal("Helper", Holder.Helper.GetName().Name);
                [Fact] public void Reads() => Assert.Equal("shared", Holder.Shared.Trim());
            }

            // Its static constructor keeps only mutable numbers, which no field probe sees (a counter
            // would store on every call): what it used may serve anyone, so it stays initialization.
            public static class Loose
            {
                public static int NameLength;
                public static int TextLength;

                static Loose()
                {
                    NameLength = Assembly.LoadFrom(Repo.Tool("Loose")).GetName().Name!.Length;
                    TextLength = File.ReadAllText(Repo.Path("testdata", "loose.txt")).Length;
                }
            }

            public class LooseTests
            {
                [Fact] public void Counts() => Assert.Equal(5, Loose.NameLength + Loose.TextLength - 6);
            }

            public class PlainTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_use_while_initializing_a_field_belongs_to_the_fields_readers()
    {
        var map = Record();

        var uses = Assert.IsType<FileReads>(map.OutputUses);
        Assert.Contains("Helper", uses.Classes["Init.Tests.OwnFieldTests"]);
        Assert.Contains("Helper", uses.Classes["Init.Tests.ReaderTests"]);
        Assert.DoesNotContain("Helper", uses.Initialization);
        Assert.False(uses.Classes.ContainsKey("Init.Tests.PlainTests"));

        var reads = Assert.IsType<FileReads>(map.FileReads);
        Assert.Contains("testdata/own.txt", reads.Classes["Init.Tests.OwnFieldTests"]);
        Assert.Contains("testdata/shared.txt", reads.Classes["Init.Tests.ReaderTests"]);
        Assert.DoesNotContain("testdata/own.txt", reads.Initialization);
        Assert.DoesNotContain("testdata/shared.txt", reads.Initialization);
        Assert.False(reads.Classes.ContainsKey("Init.Tests.PlainTests"));
    }

    [Fact]
    public void A_use_whose_value_leaves_through_no_tracked_field_stays_initialization()
    {
        var map = Record();

        Assert.Contains("Loose", map.OutputUses!.Initialization);
        Assert.Contains("testdata/loose.txt", map.FileReads!.Initialization);
    }

    [Fact]
    public void A_change_to_what_initialized_a_field_selects_only_the_fields_readers()
    {
        Record();

        _repo.Write("tools/Helper/Marker.cs", "namespace Helper; public static class Marker { public const int Version = 2; }");
        Assert.Equal(["Init.Tests.OwnFieldTests", "Init.Tests.ReaderTests"], Select());

        _repo.Write("tools/Helper/Marker.cs", "namespace Helper; public static class Marker { }");
        _repo.Write("testdata/shared.txt", "shared, changed\n");
        Assert.Equal(["Init.Tests.ReaderTests"], Select());

        IReadOnlyList<string> Select()
        {
            var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();
            Assert.Equal(TestRunMode.Filtered, selection.Mode);
            return selection.TestClasses;
        }
    }

    /// <summary>Records the test project, checks that its tests passed and stores the map where the selection finds it.</summary>
    private CoverageMap Record()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();
        CoverageMap map;
        bool passed;
        try
        {
            (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
                .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
        }

        Assert.True(passed, output.ToString());
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        return map;
    }
}
