using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Test classes whose source file several test projects compile (a shared .projitems or a linked
/// Compile item, as when one suite runs against several Roslyn versions) belong to every one of them.
/// </summary>
public sealed class SharedTestSourcesTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Shared_test_class_is_selected_in_every_project_that_compiles_it()
    {
        _repo.Write("src/Calc/Calc.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc; public class Calculator { public int Plus(int a, int b) => a + b; public int Mul(int a, int b) => a * b; }");
        _repo.Write("tests/Shared/PlusTests.cs", "namespace Calc.Tests; public class PlusTests { [Fact] public void Pluses() => Assert.Equal(3, new Calculator().Plus(1, 2)); }");
        foreach (var name in (string[])["V1", "V2"])
        {
            _repo.Write($"tests/Calc.{name}.Tests/Calc.{name}.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" />
                    <ProjectReference Include="../../src/Calc/Calc.csproj" />
                    <Compile Include="../Shared/*.cs" />
                  </ItemGroup>
                </Project>
                """);
            _repo.Write($"tests/Calc.{name}.Tests/MulTests.cs", "namespace Calc.Tests; public class MulTests { [Fact] public void Muls() => Assert.Equal(6, new Calculator().Mul(2, 3)); }");
        }

        _repo.Commit("base");
        _repo.Replace("src/Calc/Calculator.cs", "a + b", "b + a");

        var selections = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests;

        Assert.Equal(2, selections.Count);
        Assert.All(selections, s =>
        {
            Assert.True(s.Mode == TestRunMode.Filtered, s.Reason);
            Assert.Equal(["Calc.Tests.PlusTests"], s.TestClasses);
        });
    }

    public void Dispose() => _repo.Dispose();
}
