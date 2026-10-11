using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Recording tracks every assembly load (docs/design/loaded-outputs.md): a load through a call the recording wraps names
/// the class that made it; a load it does not see (<c>Assembly.Load(bytes)</c>, a third-party host, the runtime) puts the
/// assembly in <see cref="CoverageMap.OutputLoadsUnseen"/>, so the selection does not narrow to the classes that used it;
/// a class that enumerates the loaded assemblies is a user of each of them. Slow (it builds and records).
/// </summary>
[Trait("Category", "Slow")]
public sealed class AssemblyLoadTrackingTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Recording_names_who_loaded_an_output_and_lists_the_loads_it_could_not_see()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        foreach (var tool in (string[])["Tool", "Hidden"])
        {
            _repo.Write($"tools/{tool}/{tool}.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            _repo.Write($"tools/{tool}/Plugin.cs", $"namespace {tool}; public class Plugin {{ public string Name() => \"{tool}\"; }}");
        }

        // A module catalog in code an application also uses, as Carter's.
        _repo.Write("src/Catalog/Catalog.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("src/Catalog/Modules.cs", "namespace Catalog; public static class Modules { public static System.Reflection.Assembly Find(string name) => System.Reflection.Assembly.Load(name); }");
        _repo.Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Catalog/Catalog.csproj" /></ItemGroup></Project>""");
        _repo.Write("tests/Tools.Tests/Tools.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Catalog/Catalog.csproj" />
                <ProjectReference Include="../../tools/Tool/Tool.csproj" ReferenceOutputAssembly="false" OutputItemType="PluginDll" />
                <ProjectReference Include="../../tools/Hidden/Hidden.csproj" ReferenceOutputAssembly="false" OutputItemType="PluginDll" />
              </ItemGroup>
              <Target Name="CopyPlugins" AfterTargets="ResolveProjectReferences">
                <ItemGroup><Content Include="@(PluginDll)" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
              </Target>
            </Project>
            """);
        _repo.Write("tests/Tools.Tests/Tests.cs", """
            using System.Reflection;
            using Xunit;
            namespace Tools.Tests;
            public class LoadFromTests { [Fact] public void Loads() => Assert.NotNull(Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Tool.dll")).GetType("Tool.Plugin")); }
            public class BytesTests { [Fact] public void Loads() => Assert.NotNull(Assembly.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Hidden.dll"))).GetType("Hidden.Plugin")); }
            public class ScanTests { [Fact] public void Scans() => Assert.NotEmpty(AppDomain.CurrentDomain.GetAssemblies()); }
            public class NameTests { [Fact] public void Loads() => Assert.NotNull(Assembly.Load(new AssemblyName("Tools.Tests"))); }
            public class CatalogTests { [Fact] public void Finds() => Assert.NotNull(Catalog.Modules.Find("Tools.Tests")); }
            """);
        _repo.Commit("base");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject && p.Path.EndsWith("Tools.Tests.csproj", StringComparison.Ordinal));
        var output = new StringWriter();

        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance).Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);

        Assert.True(passed, output.ToString());
        var uses = map.OutputUses!.Classes;
        Assert.Contains("Tool", uses["Tools.Tests.LoadFromTests"]);
        Assert.Contains(CoverageMap.OutputScanPrefix + "Tools.Tests", uses["Tools.Tests.ScanTests"]);
        Assert.DoesNotContain("Tools.Tests", uses["Tools.Tests.ScanTests"]);
        // The test chose what it loads and may inspect it (an architecture test); the catalog finds modules.
        Assert.Contains("Tools.Tests", uses["Tools.Tests.NameTests"]);
        Assert.Contains(CoverageMap.OutputNameLoadPrefix + "Tools.Tests", uses["Tools.Tests.CatalogTests"]);
        Assert.DoesNotContain("Tools.Tests", uses["Tools.Tests.CatalogTests"]);
        Assert.NotNull(map.OutputLoadsUnseen);
        Assert.Contains("Hidden", map.OutputLoadsUnseen!);
        Assert.DoesNotContain("Tool", map.OutputLoadsUnseen!);
        Assert.True(map.KnowsLoadsOf(["Tool"]));
        Assert.False(map.KnowsLoadsOf(["Hidden"]));
    }
}
