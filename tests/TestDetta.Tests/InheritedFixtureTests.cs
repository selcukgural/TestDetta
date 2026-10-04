using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A test base class from a library can declare <c>IClassFixture&lt;TAppFixture&gt;</c> for its
/// derived test classes (FastEndpoints.Testing's <c>TestBase&lt;TAppFixture&gt;</c>), and xUnit v3
/// can share a fixture across the assembly with <c>[assembly: AssemblyFixture]</c>. The code the
/// fixture runs (starting the app) must select every class that uses it. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class InheritedFixtureTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Fixture_declared_by_a_base_class_in_another_library_selects_its_users()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Host.cs", """
            namespace App;
            public sealed class Handler { public Handler(string name) => Name = name; public string Name { get; } }
            public static class Host { public static List<Handler> Start() => [new Handler("orders")]; }
            public static class Math2 { public static int Two() => 2; }
            """);
        _repo.Write("src/Testing/Testing.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit.extensibility.core" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Testing/TestBase.cs", """
            namespace Testing;
            public abstract class TestBase<TAppFixture> : Xunit.IClassFixture<TAppFixture> where TAppFixture : class;
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/App/App.csproj" />
                <ProjectReference Include="../../src/Testing/Testing.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            public sealed class Fixture { public Fixture() => Handlers = Host.Start(); public List<App.Handler> Handlers { get; } }
            public class OrdersTests(Fixture app) : Testing.TestBase<Fixture> { [Fact] public void Has_orders() => Assert.Equal("orders", app.Handlers[0].Name); }
            public class CountTests(Fixture app) : Testing.TestBase<Fixture> { [Fact] public void Has_one() => Assert.Single(app.Handlers); }
            public class PlainTests { [Fact] public void Two() => Assert.Equal(2, Math2.Two()); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        Assert.Contains(new CoveredMethod("App.Handler", ".ctor"), map.Fixtures.SelectMany(f => f.Value).Select(id => map.Methods[id]));
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        _repo.Replace("src/App/Host.cs", "=> Name = name;", "=> Name = name.Trim();");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.True(selection.Mode == TestRunMode.All
                    || selection.TestClasses.Contains("App.Tests.OrdersTests") && selection.TestClasses.Contains("App.Tests.CountTests"),
            string.Join(", ", selection.TestClasses));
    }

    public void Dispose() => _repo.Dispose();
}
