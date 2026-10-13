using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// The remaining risk of docs/design/loaded-outputs.md: an assembly loads once per process, so a class that uses an output
/// an earlier class loaded, without a call the recording sees, is not recorded as its user. Here <c>ALoaderTests</c> loads a
/// plugin referenced only for build order, and <c>BShapeTests</c>, which runs after it, finds the loaded plugin through
/// <c>AssemblyLoadContext.Assemblies</c> and checks the shape of its types without running any of their code. A new type
/// that breaks the check fails <c>BShapeTests</c> in a full run; the selection ran only <c>ALoaderTests</c> until scans
/// of loaded assemblies and of an assembly's types were recorded (<c>TestDettaTracker.OutputScanned</c>), in the test's
/// own code and in a third-party plugin host alike, and until the outputs used while a value was built went to the readers
/// of the field it was stored into (types kept in a static cache, <c>TestDettaTracker.GiveRecentUses</c>). Slow: builds
/// and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class OrderDependentLoadTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_class_that_inspects_a_plugin_an_earlier_class_loaded_is_selected_when_the_plugin_changes() =>
        AssertInspectorSelected("""
            var plugin = AssemblyLoadContext.Default.Assemblies.Single(static a => a.GetName().Name == "Plugin");
            Assert.All(plugin.GetExportedTypes(), static t => Assert.True(t.IsSealed, t.FullName));
            """, vendor: false);

    [Fact]
    public void A_class_that_inspects_it_through_a_third_party_host_is_selected_too() =>
        AssertInspectorSelected("""
            Assert.All(Vendor.PluginHost.TypesOf("Plugin"), static t => Assert.True(t.IsSealed, t.FullName));
            """, vendor: true);

    [Fact]
    public void A_class_that_inspects_types_an_earlier_class_kept_in_a_static_cache_is_selected_too() =>
        AssertInspectorSelected("""
            Assert.All(PluginCache.Types!, static t => Assert.True(t.IsSealed, t.FullName));
            """, vendor: false, cache: true);

    private void AssertInspectorSelected(string inspection, bool vendor, bool cache = false)
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("tools/Plugin/Plugin.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("tools/Plugin/Plugins.cs", """namespace Plugin; public sealed class Greeter { public string Name() => "greeter"; }""");
        if (vendor)
        {
            WriteVendorHost(Path.Combine(_repo.Root, "lib", "Vendor.dll"));
        }

        _repo.Write("tests/Host.Tests/Host.Tests.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../tools/Plugin/Plugin.csproj" ReferenceOutputAssembly="false" />
                {{(vendor ? """<Reference Include="Vendor" HintPath="../../lib/Vendor.dll" />""" : "")}}
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Host.Tests/Tests.cs", $$"""
            using System.Reflection;
            using System.Runtime.Loader;
            using Xunit;
            using Xunit.Abstractions;
            [assembly: CollectionBehavior(DisableTestParallelization = true)]
            [assembly: TestCollectionOrderer("Host.Tests.ByCollectionName", "Host.Tests")]
            namespace Host.Tests;

            // Collections run in name order: a fixed order, which the recording and a full run share.
            public sealed class ByCollectionName : ITestCollectionOrderer
            {
                public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
                    testCollections.OrderBy(static c => c.DisplayName, StringComparer.Ordinal);
            }

            // A cache the first class fills and later classes read, as a plugin host keeps the types it found.
            public static class PluginCache { public static Type[]? Types; }

            [Collection("1 loader")]
            public class ALoaderTests
            {
                [Fact]
                public void Loads()
                {
                    var plugin = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Plugin/bin/Debug/net10.0/Plugin.dll")));
                    {{(cache ? "PluginCache.Types = plugin.GetExportedTypes();" : "")}}
                    Assert.NotNull(plugin.GetType("Plugin.Greeter"));
                }
            }

            [Collection("2 shape")]
            public class BShapeTests
            {
                [Fact]
                public void Every_plugin_type_is_sealed()
                {
                    {{inspection}}
                }
            }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        Assert.Contains("Plugin", map.OutputUses!.Classes["Host.Tests.ALoaderTests"]);
        Assert.Contains(CoverageMap.OutputScanPrefix + "Plugin", map.OutputUses.Classes["Host.Tests.BShapeTests"]);

        Assert.True(map.KnowsLoadsOf(["Plugin"]));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        _repo.Write("tools/Plugin/Plugins.cs", """
            namespace Plugin;
            public sealed class Greeter { public string Name() => "greeter"; }
            public class Farewell { public string Name() => "bye"; }
            """);
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Host.Tests.ALoaderTests", "Host.Tests.BShapeTests"], selection.TestClasses.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Compiles a plugin host standing in for a package: it finds a loaded assembly among all loaded ones and lists its
    /// exported types, the calls a third-party scanner makes.
    /// </summary>
    /// <param name="path">Where the library goes.</param>
    private static void WriteVendorHost(string path)
    {
        // Against the reference pack, as a package is built: the test project compiles against it in turn.
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        var referencePack = Directory.GetDirectories(Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref"))
            .Select(static d => Path.Combine(d, "ref", "net10.0", "System.Runtime.dll"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .Last();
        var compilation = CSharpCompilation.Create(
            "Vendor",
            [CSharpSyntaxTree.ParseText("""
                namespace Vendor;
                public static class PluginHost
                {
                    public static System.Type[] TypesOf(string name)
                    {
                        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (assembly.GetName().Name == name) { return assembly.GetExportedTypes(); }
                        }

                        return [];
                    }
                }
                """)],
            [MetadataReference.CreateFromFile(referencePack)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var pe = File.Create(path);
        var emitted = compilation.Emit(pe);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
    }
}
