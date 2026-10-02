using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// Some suites take their test host down when their tests run one at a time (NLog runs out of
/// memory). Such a suite is recorded with its own parallelism: code inside a test still counts for
/// its class, code outside every test flow counts as ambient, and the map has no class times.
/// Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ParallelOnlySuiteTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Suite_that_crashes_when_sequential_is_recorded_with_its_own_parallelism()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc; public static class Calculator { public static int Add(int a, int b) => a + b; public static int Mul(int a, int b) => a * b; }");
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
            using System;
            using System.Threading;
            using Xunit;
            namespace Calc.Tests;
            // Each class needs the other running at the same time, or the host goes down.
            public static class Together
            {
                private static readonly Barrier Meeting = new(2);
                public static void Meet() { if (!Meeting.SignalAndWait(TimeSpan.FromSeconds(10))) { Environment.FailFast("ran alone"); } }
            }
            public class AddTests { [Fact] public void Adds() { Together.Meet(); Assert.Equal(3, Calculator.Add(1, 2)); } }
            public class MulTests { [Fact] public void Muls() { Together.Meet(); Assert.Equal(6, Calculator.Mul(2, 3)); } }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        using var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        var methods = map.Methods.ToList();
        Assert.Contains(methods.IndexOf(new CoveredMethod("Calc.Calculator", "Add")), map.Classes["Calc.Tests.AddTests"]);
        Assert.DoesNotContain(methods.IndexOf(new CoveredMethod("Calc.Calculator", "Add")), map.Classes["Calc.Tests.MulTests"]);
        Assert.Contains(methods.IndexOf(new CoveredMethod("Calc.Calculator", "Mul")), map.Classes["Calc.Tests.MulTests"]);
        Assert.Null(map.Timings);
    }

    public void Dispose() => _repo.Dispose();
}
