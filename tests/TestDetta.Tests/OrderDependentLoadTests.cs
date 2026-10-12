using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// The remaining risk of docs/design/loaded-outputs.md: an assembly loads once per process, so a class that uses an output
/// an earlier class loaded, without a call the recording sees, is not recorded as its user. Here <c>ALoaderTests</c> loads a
/// plugin referenced only for build order, and <c>BShapeTests</c>, which runs after it, finds the loaded plugin through
/// <c>AssemblyLoadContext.Assemblies</c> and checks the shape of its types without running any of their code. A new type
/// that breaks the check fails <c>BShapeTests</c> in a full run, while the selection runs only <c>ALoaderTests</c>.
/// Slow: builds, records and runs the tests.
/// </summary>
[Trait("Category", "Slow")]
public sealed class OrderDependentLoadTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_class_that_inspects_a_plugin_an_earlier_class_loaded_is_selected_when_the_plugin_changes()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("tools/Plugin/Plugin.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("tools/Plugin/Plugins.cs", """namespace Plugin; public sealed class Greeter { public string Name() => "greeter"; }""");
        _repo.Write("tests/Host.Tests/Host.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../tools/Plugin/Plugin.csproj" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Host.Tests/Tests.cs", """
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

            [Collection("1 loader")]
            public class ALoaderTests
            {
                [Fact]
                public void Loads() => Assert.NotNull(Assembly.LoadFrom(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Plugin/bin/Debug/net10.0/Plugin.dll"))).GetType("Plugin.Greeter"));
            }

            [Collection("2 shape")]
            public class BShapeTests
            {
                [Fact]
                public void Every_plugin_type_is_sealed()
                {
                    var plugin = AssemblyLoadContext.Default.Assemblies.Single(static a => a.GetName().Name == "Plugin");
                    Assert.All(plugin.GetExportedTypes(), static t => Assert.True(t.IsSealed, t.FullName));
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
        Assert.True(map.KnowsLoadsOf(["Plugin"]));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        _repo.Write("tools/Plugin/Plugins.cs", """
            namespace Plugin;
            public sealed class Greeter { public string Name() => "greeter"; }
            public class Farewell { public string Name() => "bye"; }
            """);
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.Contains("Host.Tests.ALoaderTests", selection.TestClasses);
        Assert.True(selection.Mode == TestRunMode.All || selection.TestClasses.Contains("Host.Tests.BShapeTests"), $"{selection.Mode}: {selection.Reason}");
    }
}
