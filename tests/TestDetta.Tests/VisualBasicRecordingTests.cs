using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A Visual Basic library under Visual Basic xUnit tests, recorded for real: the names the analysis
/// gives Visual Basic members (root namespace included) are the names the coverage map records from IL,
/// so a changed member is answered by coverage (docs/design/vb-fsharp-support.md). Slow (records).
/// </summary>
[Trait("Category", "Slow")]
public sealed class VisualBasicRecordingTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_changed_member_is_answered_by_the_coverage_of_visual_basic_tests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Calc/Calc.vbproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>Shop</RootNamespace></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.vb", """
            Namespace Calc
                Public Class Calculator
                    Public Function Add(a As Integer, b As Integer) As Integer
                        Return a + b
                    End Function

                    Public Function Sub2(a As Integer, b As Integer) As Integer
                        Return a - b
                    End Function
                End Class

                Public Module Helpers
                    Public Function Twice(x As Integer) As Integer
                        Return Add2(x, x)
                    End Function

                    Private Function Add2(x As Integer, y As Integer) As Integer
                        Return New Calculator().Add(x, y)
                    End Function
                End Module
            End Namespace
            """);
        _repo.Write("tests/Calc.Tests/Calc.Tests.vbproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><RootNamespace>Shop.Tests</RootNamespace></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Calc/Calc.vbproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.vb", """
            Imports Xunit
            Imports Shop.Calc

            Public Class AddTests
                <Fact>
                Public Sub Adds()
                    Assert.Equal(3, New Calculator().Add(1, 2))
                End Sub
            End Class

            Public Class SubTests
                <Fact>
                Public Sub Subtracts()
                    Assert.Equal(1, New Calculator().Sub2(3, 2))
                End Sub
            End Class

            Public Class TwiceTests
                <Fact>
                Public Sub Doubles()
                    Assert.Equal(4, Twice(2))
                End Sub
            End Class
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

        // Add is run by AddTests directly and by TwiceTests through the module: both, and not SubTests.
        _repo.Replace("src/Calc/Calculator.vb", "Return a + b", "Return b + a");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Shop.Tests.AddTests", "Shop.Tests.TwiceTests"], selection.TestClasses);
        Assert.All(selection.Decisions.Where(static d => d.Selected), static d => Assert.Equal(ClassDecisionKind.Coverage, d.Kind));
    }
}
