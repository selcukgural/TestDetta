using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// xUnit disposes a class fixture when its class finishes, with no test code running. The teardown serves the fixture's
/// users as its setup does; recorded outside every test, it and every value it read could not be answered by coverage
/// (GraphQL's <c>ComplexityMetaDataFixture</c> disposing a shared schema). A test that disposes its fixture itself keeps
/// the code in its own class. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class FixtureTeardownTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_fixture_teardown_belongs_to_the_fixture_and_a_test_s_own_dispose_to_the_test()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Cleanup.cs", """
            namespace App;
            public static class Cleanup { public static int Count; public static void Run() => Count++; }
            public static class Math2 { public static int Two() => 2; }
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
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
            public sealed class StoreFixture : IDisposable { public int Two { get; } = Math2.Two(); public void Dispose() => Cleanup.Run(); }
            public class UserTests(StoreFixture store) : IClassFixture<StoreFixture> { [Fact] public void Uses() => Assert.Equal(2, store.Two); }
            // Disposes its fixture inside the test, where the code is the test's own.
            public class SelfTests(StoreFixture store) : IClassFixture<StoreFixture> { [Fact] public void Disposes() => store.Dispose(); }
            public class PlainTests { [Fact] public void Two() => Assert.Equal(2, Math2.Two()); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        var run = new CoveredMethod("App.Cleanup", "Run");
        Assert.DoesNotContain(run, map.Ambient.Select(id => map.Methods[id]));
        Assert.Contains(run, map.Fixtures["App.Tests.StoreFixture"].Select(id => map.Methods[id]));
        Assert.Contains(run, map.Classes["App.Tests.SelfTests"].Select(id => map.Methods[id]));
        Assert.DoesNotContain(run, map.Classes.GetValueOrDefault("App.Tests.PlainTests", []).Select(id => map.Methods[id]));
    }
}
