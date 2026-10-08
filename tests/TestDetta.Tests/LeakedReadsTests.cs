using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A class that reads a value another test class's code left in a field is named in the map
/// (<see cref="CoverageMap.LeakedReads"/>, docs/design/order-dependent-state.md): NLog's global configuration, set by
/// one test and read by the next. A value a static constructor built is no leftover: whoever needs it first builds it.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LeakedReadsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_class_that_reads_what_another_class_set_is_named_and_one_that_reads_a_static_constructors_value_is_not()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Settings.cs", """
            namespace App;
            public sealed class Settings(string name)
            {
                public string Name { get; } = name;

                // Set by whoever configures; otherwise loaded on first use (NLog's LogFactory.Configuration).
                public static Settings? Current { get; set; }

                public static string NameNow() => (Current ??= Load()).Name;

                public static Settings Load() => new("loaded");
            }
            public static class Defaults
            {
                public static readonly Settings Value = new("default");
            }
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            // Runs first and leaves its settings behind.
            public class AaaConfiguresTests { [Fact] public void Configures() => App.Settings.Current = new App.Settings("configured"); }
            // Runs next and gets Aaa's settings; alone it would load its own.
            public class BbbReadsTests { [Fact] public void Reads() => Assert.Equal("configured", App.Settings.NameNow()); }
            // Reads a value built by a static constructor.
            public class CccDefaultsTests { [Fact] public void Reads() => Assert.Equal("default", App.Defaults.Value.Name); }
            public class DddDefaultsTests { [Fact] public void Reads() => Assert.Equal("default", App.Defaults.Value.Name); }
            """);
        _repo.Write("tests/App.Tests/Order.cs", """
            [assembly: Xunit.TestCollectionOrderer("App.Tests.ByName", "App.Tests")]
            [assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
            namespace App.Tests;
            public sealed class ByName : Xunit.ITestCollectionOrderer
            {
                public System.Collections.Generic.IEnumerable<Xunit.Abstractions.ITestCollection> OrderTestCollections(System.Collections.Generic.IEnumerable<Xunit.Abstractions.ITestCollection> collections) =>
                    System.Linq.Enumerable.OrderBy(collections, static c => c.DisplayName, System.StringComparer.Ordinal);
            }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);

        var leaked = map.LeakedReads!;
        Assert.Equal(["App.Tests.BbbReadsTests"], leaked.Keys);
        Assert.Contains(leaked["App.Tests.BbbReadsTests"], static f => f.Contains("Settings", StringComparison.Ordinal));

        var path = CoverageMapStore.PathFor(_repo.Root, testProject.Path);
        CoverageMapStore.Write(path, map);
        Assert.Equal(leaked["App.Tests.BbbReadsTests"], CoverageMapStore.Read(path)!.LeakedReads!["App.Tests.BbbReadsTests"]);
    }
}
