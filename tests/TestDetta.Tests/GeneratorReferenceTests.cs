using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Project references a project does not compile against: a source generator or analyzer
/// (<c>OutputItemType="Analyzer"</c>), or <c>ReferenceOutputAssembly="false"</c>. The referenced code
/// runs inside the compiler or in another process, where neither names nor coverage reach
/// (protobuf-net's BuildTools, which compiles in protobuf-net.Core's sources).
/// </summary>
public sealed class GeneratorReferenceTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public GeneratorReferenceTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public static class Calc { public static int Tax() => 2; }");
        // The generator compiles the library's sources in, as BuildTools does with protobuf-net.Core.
        _repo.Write("src/Gen/Gen.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Lib/**/*.cs" Link="Lib" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Gen/Generator.cs", "namespace Gen; public class Generator { public string Emit() => \"partial class Generated { }\"; }");
        _repo.Write("src/Tool/Tool.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        _repo.Write("src/Tool/Program.cs", "System.Console.WriteLine(\"tool\");");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                <ProjectReference Include="../../src/Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                <ProjectReference Include="../../src/Tool/Tool.csproj">
                  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/CalcTests.cs", "namespace Lib.Tests; public class CalcTests { [Fact] public void Taxes() => Assert.Equal(2, Lib.Calc.Tax()); }");
        _repo.Write("tests/Lib.Tests/GeneratedTests.cs", "namespace Lib.Tests; public class GeneratedTests { [Fact] public void Exists() => Assert.NotNull(typeof(Generated)); }");
        _repo.Write("tests/Lib.Tests/PlainTests.cs", "namespace Lib.Tests; public class PlainTests { [Fact] public void Holds() => Assert.True(true); }");
        _repo.Commit("base");
    }

    [Fact]
    public void Analyzer_and_unreferenced_assembly_references_are_opaque_and_plain_ones_are_not()
    {
        var graph = Load();
        var tests = graph[Full("tests/Lib.Tests/Lib.Tests.csproj")];

        Assert.Equal([Full("src/Gen/Gen.csproj"), Full("src/Tool/Tool.csproj")], tests.OpaqueReferences.Order(StringComparer.Ordinal));
        Assert.Equal([tests], graph.OpaqueConsumersOf(Full("src/Gen/Gen.csproj")));
        Assert.Empty(graph.OpaqueConsumersOf(Full("src/Lib/Lib.csproj")));
    }

    [Fact]
    public void Opaque_reference_declared_by_an_import_through_a_property_is_found()
    {
        _repo.Write("tests/Lib.Tests/Directory.Build.targets", """
            <Project>
              <PropertyGroup><GeneratorName>Gen</GeneratorName></PropertyGroup>
              <ItemGroup><ProjectReference Include="$(MSBuildThisFileDirectory)../../src/$(GeneratorName)/$(GeneratorName).csproj" OutputItemType="Analyzer" /></ItemGroup>
            </Project>
            """);
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", """<ProjectReference Include="../../src/Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />""", "");

        var tests = Load()[Full("tests/Lib.Tests/Lib.Tests.csproj")];

        Assert.Contains(Full("src/Gen/Gen.csproj"), tests.References);
        Assert.Contains(Full("src/Gen/Gen.csproj"), tests.OpaqueReferences);
    }

    [Fact]
    public void Opaque_reference_that_cannot_be_resolved_makes_every_project_it_may_name_opaque()
    {
        _repo.Replace(
            "tests/Lib.Tests/Lib.Tests.csproj",
            """<ProjectReference Include="../../src/Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />""",
            """<ProjectReference Include="../../src/$([System.String]::Concat('G', 'en'))/Gen.csproj" OutputItemType="Analyzer" />""");

        var graph = Load();

        Assert.Contains(graph[Full("tests/Lib.Tests/Lib.Tests.csproj")], graph.OpaqueConsumersOf(Full("src/Gen/Gen.csproj")));
    }

    [Fact]
    public void Generator_change_runs_the_whole_project_that_uses_it()
    {
        // No test names the generator; its change still rewrites Generated in the test assembly.
        _repo.Replace("src/Gen/Generator.cs", "partial class Generated { }", "partial class Generated { int X; }");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        // Without a build to compare what it generates, the project runs completely, as it always did.
        Assert.Contains("Gen.csproj, which is affected, as a source generator, and nothing builds it here", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Change_to_code_a_generator_compiles_in_runs_the_whole_project_that_uses_it()
    {
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        Assert.Equal(TestRunMode.All, Select().Mode);
    }

    [Fact]
    public void Change_to_a_project_used_without_its_assembly_runs_the_whole_consumer()
    {
        // The tests may run the tool as a process: coverage and names see nothing of it.
        _repo.Replace("src/Tool/Program.cs", "\"tool\"", "\"TOOL\"");

        Assert.Equal(TestRunMode.All, Select().Mode);
    }

    [Fact]
    public void Plain_reference_is_still_narrowed_when_no_opaque_reference_is_affected()
    {
        _repo.Write("src/Other/Other.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Other/Fee.cs", "namespace Other; public static class Fee { public static int Of() => 1; }");
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", """<ProjectReference Include="../../src/Lib/Lib.csproj" />""", """<ProjectReference Include="../../src/Lib/Lib.csproj" /><ProjectReference Include="../../src/Other/Other.csproj" />""");
        _repo.Write("tests/Lib.Tests/FeeTests.cs", "namespace Lib.Tests; public class FeeTests { [Fact] public void Charges() => Assert.Equal(1, Other.Fee.Of()); }");
        _repo.Commit("other");
        _repo.Replace("src/Other/Fee.cs", "=> 1", "=> 2");

        Assert.Equal(["Lib.Tests.FeeTests"], Select().TestClasses);
    }

    private ProjectGraph Load() => new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

    private string Full(string relative) => Paths.Normalize(Path.Combine(_repo.Root, relative));

    public void Dispose() => _repo.Dispose();
}
