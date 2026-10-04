using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Theory data the framework enumerates during discovery runs before any test, and every run pays
/// for it whatever it selects: its time is start-up, not the time of the class that uses the data.
/// Its code still counts for that class. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class StartupTimeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Time_spent_in_discovery_data_counts_as_startup()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Rows.cs", "namespace Calc; public static class Rows { public static int Slow() { System.Threading.Thread.Sleep(1500); return 1; } }");
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using System.Collections.Generic;
            using Xunit;
            namespace Calc.Tests;
            public class SlowDataTests
            {
                // Serializable rows: xUnit enumerates them while discovering.
                public static IEnumerable<object[]> Data() { yield return new object[] { Rows.Slow() }; }
                [Theory, MemberData(nameof(Data))] public void Uses(int row) => Assert.Equal(1, row);
            }
            public class OtherTests { [Fact] public void Runs() => Assert.True(true); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);

        Assert.True(passed);
        Assert.Contains(map.Methods.ToList().IndexOf(new CoveredMethod("Calc.Rows", "Slow")), map.Classes["Calc.Tests.SlowDataTests"]);
        Assert.True(map.Timings!.StartupSeconds >= 1.4, $"start-up {map.Timings.StartupSeconds:0.00} s");
        Assert.True(map.Timings.ClassSeconds["Calc.Tests.SlowDataTests"] < 1.0, $"class {map.Timings.ClassSeconds["Calc.Tests.SlowDataTests"]:0.00} s");
    }

    public void Dispose() => _repo.Dispose();
}
