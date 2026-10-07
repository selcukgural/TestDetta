using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Projects that share an assembly name: variants of one library built against different dependencies,
/// each with its own test project of one shared name too (Meziantou.Analyzer's Roslyn variants). Each
/// test project's output holds its own variant, so recording instruments that one and reads its build
/// inputs under it, whichever project the graph lists first. An executable only copied into the output
/// is still not instrumented (<see cref="GeneratedSourceReader.CompiledAgainst"/>).
/// </summary>
public sealed class SharedAssemblyNameTests : IDisposable
{
    // Graph order depends on the file system (macOS lists the second variant first, Linux the first): a
    // first-listed owner would leave one variant's test project without its assemblies, and the theory
    // below checks both, so either order catches it.
    private const string First = "src/Lib.V1/Lib.V1.csproj";
    private const string Second = "src/Lib.V2.Roslyn/Lib.V2.Roslyn.csproj";
    private const string FirstTests = "tests/Lib.V1.Tests/Lib.V1.Tests.csproj";
    private const string SecondTests = "tests/Lib.V2.Roslyn.Tests/Lib.V2.Roslyn.Tests.csproj";
    private const string Tool = "tools/Tool/Tool.csproj";

    private readonly GitRepoFixture _repo = new();

    public SharedAssemblyNameTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(Tool, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("tools/Tool/Program.cs", """
            namespace Tool;
            public static class Program { public static int Main() => 0; }
            """);
        foreach (var (library, tests, variant) in new[] { (First, FirstTests, "v1"), (Second, SecondTests, "v2") })
        {
            _repo.Write(library, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Lib</AssemblyName><RootNamespace>Lib</RootNamespace></PropertyGroup>
                </Project>
                """);
            _repo.Write(Path.Combine(Path.GetDirectoryName(library)!, "Calc.cs"), $$"""
                namespace Lib;
                public static class Calc
                {
                    public static int Add(int a, int b) => a + b;
                    public static string Variant() => "{{variant}}";
                }
                """);

            // The executable is referenced without compiling against it and its output copied next to the tests.
            _repo.Write(tests, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>Lib.Tests</AssemblyName><RootNamespace>Lib.Tests</RootNamespace></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                    <ProjectReference Include="../../{library}" />
                    <ProjectReference Include="../../{Tool}" ReferenceOutputAssembly="false" />
                  </ItemGroup>
                  <Target Name="CopyTool" AfterTargets="Build">
                    <Copy SourceFiles="$(MSBuildThisFileDirectory)../../tools/Tool/bin/$(Configuration)/net10.0/Tool.dll" DestinationFolder="$(OutDir)" />
                  </Target>
                </Project>
                """);
            _repo.Write(Path.Combine(Path.GetDirectoryName(tests)!, "Tests.cs"), $$"""
                using Xunit;
                namespace Lib.Tests;
                public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calc.Add(1, 2)); }
                public class VariantTests { [Fact] public void Is() => Assert.Equal("{{variant}}", Calc.Variant()); }
                """);
        }

        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Every_project_of_a_shared_name_is_an_owner()
    {
        var graph = LoadGraph();
        var owners = new ProjectAssemblies(NullLogger.Instance).Owners(graph, _repo.Root);

        // Graph order differs between file systems (macOS, Linux); selection never relies on it.
        Assert.Equal([First, Second], owners["Lib.dll"].Select(Relative).Order(StringComparer.Ordinal));
        Assert.Equal([FirstTests, SecondTests], owners["Lib.Tests.dll"].Select(Relative).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(FirstTests, First)]
    [InlineData(SecondTests, Second)]
    public void Each_test_project_owns_its_own_variant_and_not_the_copied_executable(string tests, string library)
    {
        var graph = LoadGraph();
        var testProject = Project(graph, tests);

        var compiled = GeneratedSourceReader.CompiledAgainst(graph, testProject, new ProjectAssemblies(NullLogger.Instance).Owners(graph, _repo.Root));

        Assert.Equal(["Lib.Tests.dll", "Lib.dll"], compiled.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(library, Relative(compiled["Lib.dll"]));
        Assert.Equal(tests, Relative(compiled["Lib.Tests.dll"]));
    }

    [Fact]
    public void Several_reached_owners_resolve_to_the_first_by_path_whatever_the_graph_order()
    {
        var graph = LoadGraph();
        var owners = new ProjectAssemblies(NullLogger.Instance).Owners(graph, _repo.Root);
        var both = Project(graph, FirstTests) with { References = [Project(graph, First).Path, Project(graph, Second).Path] };

        var forward = GeneratedSourceReader.CompiledAgainst(graph, both, owners);
        var reversed = GeneratedSourceReader.CompiledAgainst(graph, both, owners.ToDictionary(o => o.Key, o => Enumerable.Reverse(o.Value).ToList()));

        Assert.Equal(First, Relative(forward["Lib.dll"]));
        Assert.Equal(First, Relative(reversed["Lib.dll"]));
    }

    [Fact]
    public void Build_inputs_are_read_under_the_variant_the_test_project_compiles_against()
    {
        var graph = LoadGraph();
        var testProject = Project(graph, FirstTests);
        var output = Path.Combine(_repo.Root, "tests", "Lib.V1.Tests", "bin", "Debug", "net10.0");
        var owners = GeneratedSourceReader.CompiledAgainst(graph, testProject, new ProjectAssemblies(NullLogger.Instance).Owners(graph, _repo.Root));

        // No files are built: an assembly that cannot be read has no inputs, but is still listed under its owner.
        var inputs = GeneratedSourceReader.ReadBuildInputs(
            _repo.Root, testProject, Path.Combine(output, "Lib.Tests.dll"), owners, [Path.Combine(output, "Lib.dll")], new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal([First, FirstTests], inputs.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Each_test_project_records_its_own_variant()
    {
        var graph = LoadGraph();
        foreach (var (tests, library) in new[] { (FirstTests, First), (SecondTests, Second) })
        {
            var testProject = Project(graph, tests);
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
                throw new InvalidOperationException($"{tests}: {ex.Message}\n{output}", ex);
            }

            Assert.True(passed, output.ToString());
            Assert.Empty(map.FailedClasses!);
            Assert.Equal(["Lib.Tests.AddTests", "Lib.Tests.VariantTests"], map.Classes.Keys.Order(StringComparer.Ordinal));
            Assert.Contains(new CoveredMethod("Lib.Calc", "Add"), map.Classes["Lib.Tests.AddTests"].Select(id => map.Methods[id]));
            Assert.Contains(new CoveredMethod("Lib.Calc", "Variant"), map.Classes["Lib.Tests.VariantTests"].Select(id => map.Methods[id]));

            // The copied executable is in the output but not instrumented.
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.Combine(_repo.Root, tests))!, "bin", "Debug", "net10.0", "Tool.dll")));
            Assert.DoesNotContain(map.Methods, m => m.TypeName.StartsWith("Tool.", StringComparison.Ordinal));

            Assert.Equal([library, tests], map.BuildInputs!.Keys.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>Loads the projects written in the constructor.</summary>
    private ProjectGraph LoadGraph() => new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

    /// <summary>The project at a path relative to the repository.</summary>
    private ProjectNode Project(ProjectGraph graph, string relative) => graph.Projects.Single(p => Relative(p) == relative);

    /// <summary>A project's path relative to the repository, with '/' separators.</summary>
    private string Relative(ProjectNode project) => Path.GetRelativePath(_repo.Root, project.Path).Replace('\\', '/');
}
