using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A route mapping runs whenever a host starts; its handler lambdas run only for the requests that
/// reach them. A name the mapping method mentions only inside its handlers' bodies is answered by the
/// coverage of its lambdas, not of the whole method, which every class booting the app ran (AfterApply's
/// MapSiteTrafficEndpoints named SiteTrafficNormalizer in its handler: 97 of 100 classes instead of 4).
/// A name in the method's own body, in what a handler returns (the framework reads the handler's
/// signature at start-up), or of a declaration whose shape changed keeps the whole method's coverage.
/// The mapping here is the repository's own MapPost; the rule goes by the call's name. Slow (it runs
/// dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class EndpointHandlerTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("body")]
    [InlineData("own")]
    [InlineData("return")]
    [InlineData("shape")]
    public void A_name_only_in_an_endpoint_handler_is_answered_by_the_handlers_coverage(string variant)
    {
        var scale = variant == "return"
            ? "router.MapPost(\"/scale\", x => { return Normalizer.Scale(x); });"
            : "router.MapPost(\"/scale\", x => { var y = Normalizer.Scale(x); return y; });";
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Normalizer.cs", """
            namespace App;
            public static class Normalizer
            {
                private static readonly int Factor = 2;
                public static int Scale(int x) => x * Factor;
            }
            """);
        _repo.Write("src/App/Endpoints.cs", $$"""
            namespace App;
            public sealed class Router { public readonly List<Func<int, int>> Handlers = []; }
            public static class RouterExtensions
            {
                public static Router MapPost(this Router router, string path, Func<int, int> handler)
                {
                    router.Handlers.Add(handler);
                    return router;
                }
            }
            public static class Endpoints
            {
                public static Router Register()
                {
                    var router = new Router();
                    {{scale}}
                    router.MapPost("/next", x => { var y = x + 1; return y; });
                    {{(variant == "own" ? "Normalizer.Scale(0);" : "")}}
                    return router;
                }
            }
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
            public class ScaleTests { [Fact] public void Scales() => Assert.Equal(6, App.Endpoints.Register().Handlers[0](3)); }
            public class NextTests { [Fact] public void Next() => Assert.Equal(4, App.Endpoints.Register().Handlers[1](3)); }
            public class StartupTests { [Fact] public void Registers() => Assert.Equal(2, App.Endpoints.Register().Handlers.Count); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        Assert.NotNull(map.NestedMethods);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        if (variant == "shape")
        {
            // A new member: the type's shape changed, which a framework may read where the mapping runs.
            _repo.Replace("src/App/Normalizer.cs", "public static int Scale", "public static int Twice(int x) => x * 2;\n    public static int Scale");
        }
        else
        {
            // A static field's value: what the handler computes changes, nothing the mapping registers does.
            _repo.Replace("src/App/Normalizer.cs", "Factor = 2", "Factor = 3");
        }

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        // Only the classes that ran a handler of Register (both handlers are its lambdas); StartupTests
        // only mapped the routes.
        Assert.Equal(
            variant == "body" ? ["App.Tests.NextTests", "App.Tests.ScaleTests"] : ["App.Tests.NextTests", "App.Tests.ScaleTests", "App.Tests.StartupTests"],
            selection.Mode == TestRunMode.All ? ["App.Tests.NextTests", "App.Tests.ScaleTests", "App.Tests.StartupTests"] : selection.TestClasses);
    }
}
