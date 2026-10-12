using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A fixture type that caches what its first instance builds (FastEndpoints keeps one server per fixture type) and a test
/// that builds that fixture type itself: when the test runs first, the host's start-up runs in its flow and the fixture's
/// own setup later takes the cached server. Both the test's class and the fixture's users must be selected for a change to
/// the start-up (2026-10-12: FixtureATests was missed when TestServerTests ran first). Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class TestBuiltFixtureTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_fixture_built_by_a_test_first_still_serves_its_users()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("src/Lib/Host.cs", "namespace Lib; public static class Host { public static int Boot() => 42; }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", """
            using System.Collections.Concurrent;
            using Xunit;
            using Xunit.Abstractions;
            [assembly: CollectionBehavior(DisableTestParallelization = true)]
            [assembly: TestCollectionOrderer("Lib.Tests.ByCollectionName", "Lib.Tests")]
            namespace Lib.Tests;

            public sealed class ByCollectionName : ITestCollectionOrderer
            {
                public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
                    testCollections.OrderBy(static c => c.DisplayName, StringComparer.Ordinal);
            }

            // One host per fixture type, booted by whichever instance awaits it first, on a task (as FastEndpoints' AppFixture).
            public class CachedHost : IAsyncLifetime
            {
                private static readonly ConcurrentDictionary<Type, Lazy<Task<int>>> Hosts = new();
                public int Value;
                public async Task InitializeAsync() =>
                    Value = await Hosts.GetOrAdd(GetType(), static _ => new Lazy<Task<int>>(static () => Task.Run(Lib.Host.Boot))).Value;
                public Task DisposeAsync() => Task.CompletedTask;
            }

            // Builds the fixture type itself, and runs first.
            [Collection("1 builder")]
            public class BuilderTests
            {
                [Fact]
                public async Task Builds()
                {
                    var host = new CachedHost();
                    await host.InitializeAsync();
                    Assert.Equal(42, host.Value);
                }
            }

            [Collection("2 user")]
            public class UserTests(CachedHost host) : IClassFixture<CachedHost>
            {
                [Fact] public void Uses() => Assert.Equal(42, host.Value);
            }

            [Collection("3 plain")]
            public class PlainTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        Assert.Equal(["Lib.Tests.BuilderTests"], map.FixtureBuilders!["Lib.Tests.CachedHost"]);
        var boot = new CoveredMethod("Lib.Host", "Boot");
        Assert.Contains(boot, map.Fixtures["Lib.Tests.CachedHost"].Select(id => map.Methods[id]));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        _repo.Write("src/Lib/Host.cs", "namespace Lib; public static class Host { public static int Boot() => 41 + 1; }");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.BuilderTests", "Lib.Tests.UserTests"], selection.TestClasses.Order(StringComparer.Ordinal));
    }
}
