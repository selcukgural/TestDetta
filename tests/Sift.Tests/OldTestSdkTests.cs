using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// Old test hosts (Microsoft.NET.Test.Sdk 16, as FluentValidation uses) do not load Sift's VSTest
/// collector; recording must still find the tracker. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class OldTestSdkTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Records_with_a_test_sdk_that_does_not_load_the_collector()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc; public class Calculator { public int Add(int a, int b) => a + b; }");
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="16.0.1" />
                <PackageReference Include="Microsoft.TestPlatform.TestHost" Version="16.0.1" />
                <PackageReference Include="xunit" Version="2.2.0" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.2.0" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using Xunit;
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, new Calculator().Add(1, 2)); }
            """);
        _repo.Commit("base");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var output = new StringWriter();

        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, graph.Projects.Single(p => p.IsTestProject), new RecordOptions()), output);

        Assert.True(passed, output.ToString());
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Add"), map.Classes["Calc.Tests.AddTests"].Select(id => map.Methods[id]));
    }

    public void Dispose() => _repo.Dispose();
}
