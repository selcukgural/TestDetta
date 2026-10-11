using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A test data member several classes use runs while the framework discovers tests; its rows serve those classes and no
/// other. Recorded as code outside every test, coverage could not answer for what it ran (GraphQL's
/// <c>GraphQLSerializersTestData</c>, the <c>[ClassData]</c> of 13 classes). The recorder now names its users: the classes
/// naming it in a data attribute and every concrete class deriving from one that does. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class SharedDataSourceTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_shared_data_source_counts_for_its_users_and_selects_them_alone()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Rows.cs", """
            namespace App;
            public static class Rows
            {
                public static int Make() => 1;
                public static int Base() => 2;
                public static int Two() => 2;
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
            using System.Collections;
            using Xunit;
            namespace App.Tests;
            public sealed class SharedData : IEnumerable<object[]>
            {
                public IEnumerator<object[]> GetEnumerator() { yield return [Rows.Make()]; }
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public class FirstTests { [Theory, ClassData(typeof(SharedData))] public void Row(int x) => Assert.Equal(1, x); }
            public class SecondTests { [Theory, ClassData(typeof(SharedData))] public void Row(int x) => Assert.Equal(1, x); }
            public abstract class BaseTests
            {
                public static IEnumerable<object[]> Data => [[Rows.Base()]];
                [Theory, MemberData(nameof(Data))] public void Row(int x) => Assert.Equal(2, x);
            }
            public class LeftTests : BaseTests;
            public abstract class MiddleTests : BaseTests;
            // Two levels down: it names MiddleTests, not BaseTests, so only the recorder can tell it uses the data.
            public class DeepTests : MiddleTests;
            public class PlainTests { [Fact] public void Two() => Assert.Equal(2, Rows.Two()); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        var make = new CoveredMethod("App.Rows", "Make");
        Assert.DoesNotContain(make, map.Ambient.Select(id => map.Methods[id]));
        var shared = Assert.Single(map.DataSourceUsers!, static d => d.Value.Contains("App.Tests.FirstTests"));
        Assert.Equal(["App.Tests.FirstTests", "App.Tests.SecondTests"], shared.Value.Order(StringComparer.Ordinal));
        Assert.Contains(make, map.Fixtures[shared.Key].Select(id => map.Methods[id]));
        var inherited = Assert.Single(map.DataSourceUsers!, static d => d.Value.Contains("App.Tests.LeftTests"));
        Assert.Equal(["App.Tests.DeepTests", "App.Tests.LeftTests"], inherited.Value.Order(StringComparer.Ordinal));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        Assert.Equal(map.DataSourceUsers!.Count, CoverageMapStore.Read(CoverageMapStore.PathFor(_repo.Root, testProject.Path))!.DataSourceUsers!.Count);

        _repo.Replace("src/App/Rows.cs", "Make() => 1;", "Make() => 1 + 0;");
        var made = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();
        Assert.True(made.Mode == TestRunMode.Filtered, $"{made.Mode}: {made.Reason}");
        Assert.Equal(["App.Tests.FirstTests", "App.Tests.SecondTests"], made.TestClasses.Order(StringComparer.Ordinal));

        _repo.Replace("src/App/Rows.cs", "Make() => 1 + 0;", "Make() => 1;");
        _repo.Replace("src/App/Rows.cs", "Base() => 2;", "Base() => 2 + 0;");
        var based = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();
        Assert.Contains("App.Tests.DeepTests", based.TestClasses);
        Assert.Contains("App.Tests.LeftTests", based.TestClasses);
        Assert.DoesNotContain("App.Tests.FirstTests", based.TestClasses);
    }
}
