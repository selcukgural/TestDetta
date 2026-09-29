using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

public sealed class TestingPlatformTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sift-mtp-" + Guid.NewGuid().ToString("N"));

    public TestingPlatformTests() => Directory.CreateDirectory(Path.Combine(_directory, "src", "App"));

    [Fact]
    public void Mode_comes_from_the_nearest_global_json()
    {
        Assert.Equal(DotnetTestMode.VSTest, DotnetTestModeDetector.Detect(Path.Combine(_directory, "src", "App")));

        File.WriteAllText(Path.Combine(_directory, "global.json"), """
            {
              // comments are allowed in global.json
              "sdk": { "version": "10.0.100" },
              "test": { "runner": "Microsoft.Testing.Platform" },
            }
            """);
        Assert.Equal(DotnetTestMode.TestingPlatform, DotnetTestModeDetector.Detect(Path.Combine(_directory, "src", "App")));

        // A nearer global.json without the opt-in wins.
        File.WriteAllText(Path.Combine(_directory, "src", "global.json"), """{ "sdk": { "version": "10.0.100" } }""");
        Assert.Equal(DotnetTestMode.VSTest, DotnetTestModeDetector.Detect(Path.Combine(_directory, "src", "App")));
    }

    [Fact]
    public void Testing_platform_invocations_pass_the_project_with_an_option()
    {
        var project = new ProjectNode("/repo/tests/A.Tests/A.Tests.csproj", "/repo/tests/A.Tests", true, []);
        TestProjectSelection[] selections = [new(project, TestRunMode.Filtered, ["A.Tests.OrderTests"], "")];

        var invocation = TestInvocationBuilder.Build(selections, "/repo", ["--no-build"], DotnetTestMode.TestingPlatform).Single();

        Assert.Equal(["test", "--project", "tests/A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.Tests.OrderTests.", "--no-build"], invocation.Arguments);
    }

    [Fact]
    public void Runtime_is_added_to_deps_json_as_a_library_of_the_runtime_target()
    {
        const string deps = """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": { ".NETCoreApp,Version=v10.0": { "App.Tests/1.0.0": { "runtime": { "App.Tests.dll": {} } } } },
              "libraries": { "App.Tests/1.0.0": { "type": "project" } }
            }
            """;

        var patched = JsonNode.Parse(RuntimeDeployment.AddRuntime(deps))!;
        var target = patched["targets"]![".NETCoreApp,Version=v10.0"]!.AsObject();

        var entry = Assert.Single(target, e => e.Key.StartsWith("Sift.Coverage.Runtime/", StringComparison.Ordinal));
        Assert.NotNull(entry.Value!["runtime"]!["Sift.Coverage.Runtime.dll"]);
        Assert.Equal("project", patched["libraries"]![entry.Key]!["type"]!.GetValue<string>());
        Assert.NotNull(target["App.Tests/1.0.0"]);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

/// <summary>Records coverage for a real xUnit v3 project under Microsoft.Testing.Platform. Slow.</summary>
[Trait("Category", "Slow")]
public sealed class TestingPlatformRecorderTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public TestingPlatformRecorderTests()
    {
        _repo.Write("global.json", """
            { "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }
            """);
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public class Calculator { public int Add(int a, int b) => a + b; public int Mul(int a, int b) => a * b; public int Warm() => 0; }
            """);
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="4.0.1" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using Xunit;
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, new Calculator().Add(1, 2)); }
            public class MulTests { [Fact] public void Muls() => Assert.Equal(6, new Calculator().Mul(2, 3)); }
            public class Warmup { public Warmup() => new Calculator().Warm(); }
            public class WarmTests(Warmup warmup) : IClassFixture<Warmup> { [Fact] public void Warm() => Assert.NotNull(warmup); }
            """);
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Commit("base");
    }

    [Fact]
    public void Records_an_xunit_v3_project_in_one_and_in_several_processes()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var recorder = new CoverageRecorder(NullLoggerFactory.Instance);
        var output = new StringWriter();

        foreach (var parallelism in (int[])[1, 2])
        {
            CoverageMap map;
            bool passed;
            try
            {
                (map, passed) = recorder.Record(new RecordRequest(_repo.Root, graph, testProject, "Debug", parallelism), output);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
            }

            Assert.True(passed, output.ToString());
            Assert.Contains(new CoveredMethod("Calc.Calculator", "Add"), Ran(map, "Calc.Tests.AddTests"));
            Assert.DoesNotContain(new CoveredMethod("Calc.Calculator", "Mul"), Ran(map, "Calc.Tests.AddTests"));
            Assert.Contains(new CoveredMethod("Calc.Calculator", "Mul"), Ran(map, "Calc.Tests.MulTests"));
            Assert.Contains(
                new CoveredMethod("Calc.Calculator", "Warm"),
                map.Fixtures["Calc.Tests.Warmup"].Select(id => map.Methods[id]));
        }

        // The output is back to what the build produced.
        var outputDirectory = Path.Combine(_repo.Root, "tests/Calc.Tests/bin/Debug/net10.0");
        Assert.False(File.Exists(Path.Combine(outputDirectory, "Sift.Coverage.Runtime.dll")));
        Assert.DoesNotContain("Sift.Coverage.Runtime", File.ReadAllText(Path.Combine(outputDirectory, "Calc.Tests.deps.json")));
    }

    private static HashSet<CoveredMethod> Ran(CoverageMap map, string testClass) =>
        [.. map.Classes[testClass].Select(id => map.Methods[id])];

    public void Dispose() => _repo.Dispose();
}
