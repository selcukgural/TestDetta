using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Test data members (<c>[MemberData]</c>, <c>[ClassData]</c>) run before their test class is
/// created: at discovery, or when a theory starts. Their code must count for the class that uses
/// them, not for the class entered before (Newtonsoft.Json's <c>JsonSchemaSpecTests</c> missed a
/// change to <c>JArray.Parse</c> that only its data called). Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class DataSourceTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public DataSourceTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Lib/Lib.cs", """
            namespace Lib;
            public static class Parser { public static int Parse(string text) => text.Length; public static int Other() => 1; }
            public static class Iter { public static int Make() => 2; }
            public static class Rows { public static int Make() => 3; }
            public static class Shared { public static int Build() => 4; }
            public sealed class Thing { public int Value { get; } = Iter.Make(); }
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
            public class AlphaTests { [Fact] public void Others() => Assert.Equal(1, Parser.Other()); }

            // Serializable rows: xUnit enumerates them while discovering.
            public class SpecTests
            {
                public static IList<object[]> Cases() => [[Parser.Parse("ab")]];
                [Theory, MemberData(nameof(Cases))] public void Runs(int n) => Assert.True(n > 0);
            }

            // Rows xUnit cannot serialize, from an iterator: enumerated when the theory starts.
            public class IteratorTests
            {
                public static IEnumerable<object[]> Things() { yield return [new Thing()]; }
                [Theory, MemberData(nameof(Things))] public void Runs(Thing thing) => Assert.Equal(2, thing.Value);
            }

            public sealed class RowData : TheoryData<int> { public RowData() => Add(Rows.Make()); }
            public class ClassTests { [Theory, ClassData(typeof(RowData))] public void Runs(int n) => Assert.Equal(3, n); }

            // One data member serves two classes.
            public static class SharedData { public static IEnumerable<object[]> Build() => [[Shared.Build()]]; }
            public class SharedATests { [Theory, MemberData(nameof(SharedData.Build), MemberType = typeof(SharedData))] public void Runs(int n) => Assert.Equal(4, n); }
            public class SharedBTests { [Theory, MemberData(nameof(SharedData.Build), MemberType = typeof(SharedData))] public void Runs(int n) => Assert.Equal(4, n); }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Code_run_for_test_data_counts_for_the_class_using_the_data()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();

        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);

        Assert.True(passed, output.ToString());
        Assert.Contains(new CoveredMethod("Lib.Parser", "Parse"), Ran(map, "Lib.Tests.SpecTests"));
        Assert.Contains(new CoveredMethod("Lib.Iter", "Make"), Ran(map, "Lib.Tests.IteratorTests"));
        Assert.Contains(new CoveredMethod("Lib.Rows", "Make"), Ran(map, "Lib.Tests.ClassTests"));
        foreach (var (testClass, _) in map.Classes)
        {
            if (testClass != "Lib.Tests.SpecTests")
            {
                Assert.DoesNotContain(new CoveredMethod("Lib.Parser", "Parse"), Ran(map, testClass));
            }
        }

        // Shared data is not credited to one of its classes; the map leaves it to the fallback rules.
        Assert.Contains(new CoveredMethod("Lib.Shared", "Build"), map.Ambient.Select(id => map.Methods[id]));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        _repo.Replace("src/Lib/Lib.cs", "=> text.Length;", "=> text.Length + 0;");
        AssertSelects("Lib.Tests.SpecTests");

        _repo.Replace("src/Lib/Lib.cs", "=> text.Length + 0;", "=> text.Length;");
        _repo.Replace("src/Lib/Lib.cs", "Make() => 3;", "Make() => 1 + 2;");
        AssertSelects("Lib.Tests.ClassTests");

        _repo.Replace("src/Lib/Lib.cs", "Make() => 1 + 2;", "Make() => 3;");
        _repo.Replace("src/Lib/Lib.cs", "Build() => 4;", "Build() => 2 + 2;");
        AssertSelects("Lib.Tests.SharedATests", "Lib.Tests.SharedBTests");
    }

    /// <summary>Methods the map credits to <paramref name="testClass"/>.</summary>
    private static IEnumerable<CoveredMethod> Ran(CoverageMap map, string testClass) =>
        map.Classes.TryGetValue(testClass, out var ids) ? ids.Select(id => map.Methods[id]) : [];

    /// <summary>Asserts that the current change runs every one of <paramref name="classes"/>.</summary>
    private void AssertSelects(params string[] classes)
    {
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();
        Assert.True(selection.Mode == TestRunMode.All || classes.All(selection.TestClasses.Contains), string.Join(", ", selection.TestClasses));
    }

    public void Dispose() => _repo.Dispose();
}
