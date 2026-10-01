using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// A static constructor runs once per process, for whichever class comes first, and tests often
/// reach its type only through extension methods without naming it (MimeKit's
/// HtmlAttributeIdExtensions). A change to it must select every class that ran a member of the
/// type. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class StaticConstructorTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Change_to_a_static_constructor_selects_the_classes_that_ran_the_type()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Html/Html.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Html/Attributes.cs", """
            namespace Html;
            public enum AttributeId { Href, Src }
            public static class AttributeIdExtensions
            {
                static readonly Dictionary<AttributeId, string> Names;
                static AttributeIdExtensions()
                {
                    Names = new() { [AttributeId.Href] = "href", [AttributeId.Src] = "src" };
                }

                public static string ToName(this AttributeId id) => Names[id];
            }
            public static class Plain { public static int Two() => 2; }
            """);
        _repo.Write("tests/Html.Tests/Html.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Html/Html.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Html.Tests/Tests.cs", """
            using Xunit;
            namespace Html.Tests;
            public class HrefTests { [Fact] public void Names() => Assert.Equal("href", AttributeId.Href.ToName()); }
            public class SrcTests { [Fact] public void Names() => Assert.Equal("src", AttributeId.Src.ToName()); }
            public class PlainTests { [Fact] public void Two() => Assert.Equal(2, Plain.Two()); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        _repo.Replace("src/Html/Attributes.cs", "[AttributeId.Src] = \"src\" };", "[AttributeId.Src] = \"src\" };\n        _ = Names.Count;");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.True(selection.Mode == TestRunMode.All
                    || selection.TestClasses.Contains("Html.Tests.HrefTests") && selection.TestClasses.Contains("Html.Tests.SrcTests"),
            string.Join(", ", selection.TestClasses));
    }

    public void Dispose() => _repo.Dispose();
}
