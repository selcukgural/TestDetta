using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A scan inside a cache fill (Autofac's reflection cache lists a plugin's types inside <c>GetOrAdd</c>) is the filling
/// class's, not code every class shares: as initialization it ran the whole project for any change to the plugin
/// (owner's option a, 2026-10-12). The fill here links to the class that reads its value, so it passes without the change
/// too; Autofac's own reflection caches, whose fills link to no reader, are what the measurement checks. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ScanInCacheFillTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_scan_inside_a_cache_fill_counts_for_the_class_that_filled_it()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("tools/Plugin/Plugin.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("tools/Plugin/Plugins.cs", "namespace Plugin; public sealed class Greeter { }");
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
            using System.Collections.Concurrent;
            using System.Reflection;
            using Xunit;
            namespace Host.Tests;

            public class CacheTests
            {
                // A count, which no field probe follows: the fill links to no reader, as Autofac's reflection cache.
                private static readonly ConcurrentDictionary<string, int> Counts = new();

                [Fact]
                public void Lists()
                {
                    var plugin = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Plugin/bin/Debug/net10.0/Plugin.dll")));
                    Assert.True(Counts.GetOrAdd("plugin", _ => plugin.GetExportedTypes().Length) > 0);
                }
            }

            public class PlainTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        Assert.Contains(CoverageMap.OutputScanPrefix + "Plugin", map.OutputUses!.Classes["Host.Tests.CacheTests"]);
        Assert.DoesNotContain(CoverageMap.OutputScanPrefix + "Plugin", map.OutputUses.Initialization);

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        _repo.Write("tools/Plugin/Plugins.cs", "namespace Plugin; public sealed class Greeter { } public sealed class Farewell { }");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Host.Tests.CacheTests"], selection.TestClasses);
    }
}
