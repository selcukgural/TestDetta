using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// A test may call a static helper declared on another test class (OpenTelemetry's
/// SpanBuilderShimTests calls SpanContextShimTests.GetSpanContextShim). The compiler gives such
/// a helper attributes of its own (nullable metadata), which must not make it an entry point of
/// its class: the code the test runs afterwards stays the calling class's. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class HelperCallTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Static_helper_of_another_test_class_does_not_take_over_the_bucket()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Spans/Spans.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Spans/Span.cs", """
            namespace Spans;
            public sealed class Context { public Context(string id) => Id = id; public string Id { get; } }
            public static class Builder { public static string Start(Context parent) => "child of " + parent.Id; }
            """);
        _repo.Write("tests/Spans.Tests/Spans.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Spans/Spans.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Spans.Tests/Tests.cs", """
            using Xunit;
            namespace Spans.Tests;
            public class ContextTests
            {
                [Fact] public void Keeps_id() => Assert.Equal("a", Make().Id);
                internal static Context Make(string? id = null) => new(id ?? "a");
            }
            public class BuilderTests
            {
                [Fact] public void Starts_child() => Assert.Equal("child of a", Builder.Start(ContextTests.Make()));
            }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);

        Assert.True(passed);
        var start = map.Methods.ToList().IndexOf(new CoveredMethod("Spans.Builder", "Start"));
        Assert.Contains(start, map.Classes["Spans.Tests.BuilderTests"]);
        Assert.DoesNotContain(start, map.Classes["Spans.Tests.ContextTests"]);
    }

    public void Dispose() => _repo.Dispose();
}
