using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// An F# library under F# xUnit tests, recorded for real: the names the analysis gives F# declarations
/// (modules as classes, let functions as their methods, test modules as test classes) are the names the
/// coverage map records from IL, so a changed member is answered by coverage, and an inline function,
/// compiled into its callers, by name (docs/design/vb-fsharp-support.md). Slow (records).
/// </summary>
[Trait("Category", "Slow")]
public sealed class FSharpRecordingTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_changed_member_is_answered_by_the_coverage_of_fsharp_tests()
    {
        WriteAndRecord();

        // Add is run by AddTests directly and by TwiceTests through a closure in the module: both, not SubTests.
        _repo.Replace("src/Calc/Calculator.fs", "member _.Add(a: int, b: int) = a + b", "member _.Add(a: int, b: int) = b + a");
        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Shop.Tests.AddTests", "Shop.Tests.TwiceTests"], selection.TestClasses);
        Assert.All(selection.Decisions.Where(static d => d.Selected), static d => Assert.Equal(ClassDecisionKind.Coverage, d.Kind));
    }

    [Fact]
    public void A_changed_inline_function_selects_its_callers_by_name()
    {
        WriteAndRecord();

        _repo.Replace("src/Calc/Calculator.fs", "let inline half x = x / 2", "let inline half x = x / 2 + 0");
        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Shop.Tests.HalfTests"], selection.TestClasses);
    }

    private void WriteAndRecord()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Calc/Calc.fsproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="Calculator.fs" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.fs", """
            namespace Shop.Calc

            type Calculator() =
                member _.Add(a: int, b: int) = a + b

                member _.Sub2(a: int, b: int) = a - b

            module Helpers =
                let private add2 x y = Calculator().Add(x, y)

                let twice x = [ x ] |> List.map (fun v -> add2 v v) |> List.head

                let inline half x = x / 2
            """);
        _repo.Write("tests/Calc.Tests/Calc.Tests.fsproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Compile Include="Tests.fs" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Calc/Calc.fsproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.fs", """
            namespace Shop.Tests

            open Xunit
            open Shop.Calc

            module AddTests =
                [<Fact>]
                let ``adds`` () = Assert.Equal(3, Calculator().Add(1, 2))

            module SubTests =
                [<Fact>]
                let ``subtracts`` () = Assert.Equal(1, Calculator().Sub2(3, 2))

            module TwiceTests =
                [<Fact>]
                let ``doubles`` () = Assert.Equal(4, Helpers.twice 2)

            module HalfTests =
                [<Fact>]
                let ``halves`` () = Assert.Equal(2, Helpers.half 4)
            """);
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);
        Assert.True(passed, output.ToString());
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        Assert.Contains(new CoveredMethod("Shop.Calc.Calculator", "Add"), map.Classes["Shop.Tests.AddTests"].Select(id => map.Methods[id]));
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single();
}
