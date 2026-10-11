using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A test framework extension the repository ships and uses on its own tests (AutoFixture's <c>[AutoData]</c>) runs
/// while the framework discovers and builds the tests: its code serves the classes using the attribute, not every class.
/// Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ExtensionAttributeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_repository_data_attribute_counts_for_the_classes_using_it()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit.extensibility.core" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Lib/Rows.cs", """
            using System.Reflection;
            namespace Lib;
            public static class Rows { public static int Make() => 1; public static int Two() => 2; }
            public sealed class RowsAttribute : Xunit.Sdk.DataAttribute
            {
                public override IEnumerable<object[]> GetData(MethodInfo testMethod) { yield return [Rows.Make()]; }
            }
            """);
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
            using Xunit;
            namespace Lib.Tests;
            public class FirstTests { [Theory, Rows] public void Row(int x) => Assert.Equal(1, x); }
            public class SecondTests { [Theory, Rows] public void Row(int x) => Assert.Equal(1, x); }
            public class PlainTests { [Fact] public void Two() => Assert.Equal(2, Rows.Two()); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        var make = new CoveredMethod("Lib.Rows", "Make");
        Assert.DoesNotContain(make, map.Ambient.Select(id => map.Methods[id]));
        Assert.Equal(["Lib.Tests.FirstTests", "Lib.Tests.SecondTests"], map.DataSourceUsers!["[data-source]Lib.RowsAttribute"].Order(StringComparer.Ordinal));
        Assert.Contains(make, map.Fixtures["[data-source]Lib.RowsAttribute"].Select(id => map.Methods[id]));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        _repo.Replace("src/Lib/Rows.cs", "Make() => 1;", "Make() => 1 + 0;");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();
        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.FirstTests", "Lib.Tests.SecondTests"], selection.TestClasses.Order(StringComparer.Ordinal));
    }
}
