using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// Code that runs once per process and caches its result is credited only to the first test class
/// that triggers it. Recording must say so, and a change to it must select every class that uses the
/// cache, however the classes were spread over recording processes. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class RunOnceCodeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public RunOnceCodeTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Cache/Cache.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Cache/Catalog.cs", """
            namespace Cache;
            // Filled by the static constructor, once per process.
            public static class Catalog
            {
                private static readonly int[] Items = Build();
                public static int Count => Items.Length;
                private static int[] Build() => [1, 2];
            }

            // Filled on first use, once per process.
            public static class LazyCatalog
            {
                private static int[]? _cache;
                public static int Count() => (_cache ??= Load()).Length;
                public static int[] Load() => [1];
            }
            """);
        _repo.Write("tests/Cache.Tests/Cache.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Cache/Cache.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Cache.Tests/Tests.cs", """
            using Xunit;
            namespace Cache.Tests;
            public class AlphaTests { [Fact] public void Counts() { Assert.Equal(2, Catalog.Count); Assert.Equal(1, LazyCatalog.Count()); } }
            public class BetaTests { [Fact] public void Counts() { Assert.Equal(2, Catalog.Count); Assert.Equal(1, LazyCatalog.Count()); } }
            public class GammaTests { [Fact] public void Counts() { Assert.Equal(2, Catalog.Count); Assert.Equal(1, LazyCatalog.Count()); } }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Changes_to_cached_code_select_every_class_that_uses_the_cache()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);

        // Two processes for three classes: one process runs two classes, and the second finds the caches full.
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 2)), TextWriter.Null);

        Assert.True(passed);
        Assert.Contains(new CoveredMethod("Cache.Catalog", "Build"), map.TypeInitializers.Select(id => map.Methods[id]));
        Assert.DoesNotContain(new CoveredMethod("Cache.LazyCatalog", "Load"), map.SharedInProcess!.Select(id => map.Methods[id]));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        string[] all = ["Cache.Tests.AlphaTests", "Cache.Tests.BetaTests", "Cache.Tests.GammaTests"];

        _repo.Replace("src/Cache/Catalog.cs", "=> [1, 2];", "=> [2, 1];");
        Assert.Equal(TestRunMode.All, Select().Mode);

        _repo.Replace("src/Cache/Catalog.cs", "=> [2, 1];", "=> [1, 2];");
        _repo.Replace("src/Cache/Catalog.cs", "Load() => [1];", "Load() => [1 + 0];");
        var lazy = Select();
        Assert.True(lazy.Mode == TestRunMode.All || all.All(lazy.TestClasses.Contains), string.Join(", ", lazy.TestClasses));
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

    public void Dispose() => _repo.Dispose();
}
