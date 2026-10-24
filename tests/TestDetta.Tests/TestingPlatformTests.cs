using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

public sealed class TestingPlatformTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-mtp-" + Guid.NewGuid().ToString("N"));

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
        File.WriteAllText(Path.Combine(_directory, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var project = new ProjectNode(Path.Combine(_directory, "tests/A.Tests/A.Tests.csproj"), Path.Combine(_directory, "tests/A.Tests"), true, []);
        TestProjectSelection[] selections = [new(project, TestRunMode.Filtered, ["A.Tests.OrderTests"], "")];

        var invocation = TestInvocationBuilder.Build(selections, _directory, ["--no-build"]).Single();

        Assert.Equal(["test", "--project", "tests/A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.Tests.OrderTests.", "--no-build"], invocation.Arguments);
    }

    [Fact]
    public void Nunit_result_file_tells_failed_and_executed_classes()
    {
        var results = Path.Combine(_directory, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "App.Tests.xml"), """
            <test-run testcasecount="4"><test-suite><test-suite>
              <test-case fullname="App.OrderTests.Places" classname="App.OrderTests" result="Passed" />
              <test-case fullname="App.Outer+CartTests.Adds(1)" classname="App.Outer+CartTests" result="Failed" />
              <test-case fullname="App.SkipTests.Later" classname="App.SkipTests" result="Skipped" />
              <test-case fullname="App.WarnTests.Warns" classname="App.WarnTests" result="Warning" />
            </test-suite></test-suite></test-run>
            """);
        File.WriteAllText(Path.Combine(results, "other.xml"), "<settings />");

        var outcome = TrxReport.Read(results)!;

        Assert.Equal(4, outcome.Total);
        Assert.Equal(["App.Outer+CartTests"], outcome.FailedClasses);
        Assert.Equal(["App.OrderTests", "App.Outer+CartTests", "App.WarnTests"], outcome.ExecutedClasses.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Patched_deps_json_keeps_every_sdk_written_byte()
    {
        // A real SDK-written file: this test project's own, with package hashes full of '+' and '/',
        // which already lists the runtime as a project reference.
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestDetta.Tests.deps.json")).ReplaceLineEndings("\n").TrimEnd();

        // The writer gives the SDK's bytes back for what it does not change...
        var rewritten = JsonNode.Parse(original)!.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Assert.Equal(original, rewritten.ReplaceLineEndings("\n").TrimEnd());

        // ...and an entry the SDK wrote for the runtime is kept as it is.
        Assert.Equal(original, RuntimeDeployment.AddRuntime(original).ReplaceLineEndings("\n").TrimEnd());
        Assert.DoesNotContain("\\u002B", RuntimeDeployment.AddRuntime(original), StringComparison.Ordinal);
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

        var entry = Assert.Single(target, e => e.Key.StartsWith("TestDetta.Coverage.Runtime/", StringComparison.Ordinal));
        Assert.NotNull(entry.Value!["runtime"]!["TestDetta.Coverage.Runtime.dll"]);
        Assert.Equal("project", patched["libraries"]![entry.Key]!["type"]!.GetValue<string>());
        Assert.NotNull(target["App.Tests/1.0.0"]);
    }

    [Fact]
    public void Patched_deps_json_keeps_characters_unescaped()
    {
        // xUnit 2.3's adapter could not read \u002B and found no tests in Newtonsoft.Json.
        const string deps = """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": { ".NETCoreApp,Version=v10.0": { "App.Tests/1.0.0": { "runtime": { "App.Tests.dll": {} } } } },
              "libraries": { "App.Tests/1.0.0": { "type": "project" }, "xunit/2.3.1": { "type": "package", "sha512": "sha512-a+b/c==" } }
            }
            """;

        var patched = RuntimeDeployment.AddRuntime(deps);

        Assert.Contains("\"sha512-a+b/c==\"", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", patched, StringComparison.Ordinal);
    }

    public void Dispose() => Paths.DeleteTree(_directory);
}

/// <summary>Records coverage for a real xUnit v3 project under Microsoft.Testing.Platform. Slow.</summary>
[Trait("Category", "Slow")]
public sealed class TestingPlatformRecorderTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    /// <summary>Writes the solution; xUnit v3 runs its tests in the test executable under both runners.</summary>
    /// <param name="testingPlatform">True for Microsoft.Testing.Platform, false for VSTest.</param>
    /// <param name="mtpV2">True for xunit.v3.mtp-v2, whose runner rejects the VSTest --filter.</param>
    private void WriteSolution(bool testingPlatform, bool mtpV2 = false)
    {
        _repo.Write("global.json", testingPlatform
            ? """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }"""
            : """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public class Calculator { public int Add(int a, int b) => a + b; public int Mul(int a, int b) => a * b; public int Warm() => 0; }
            """);
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup>
                {{(testingPlatform
                    ? mtpV2 ? "<PackageReference Include=\"xunit.v3.mtp-v2\" Version=\"3.2.0\" />" : "<PackageReference Include=\"xunit.v3\" Version=\"4.0.1\" />"
                    // Before 3.0, xUnit v3 did not bring the testing platform along, so VSTest runs it (AutoFixture does this).
                    : "<PackageReference Include=\"xunit.v3\" Version=\"1.0.1\" /><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" /><PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.0.1\" />")}}
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
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Commit("base");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Records_an_xunit_v3_project_in_one_and_in_several_processes(bool testingPlatform, bool mtpV2)
    {
        WriteSolution(testingPlatform, mtpV2);
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
                (map, passed) = recorder.Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: parallelism)), output);
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
        Assert.False(File.Exists(Path.Combine(outputDirectory, "TestDetta.Coverage.Runtime.dll")));
        Assert.DoesNotContain("TestDetta.Coverage.Runtime", File.ReadAllText(Path.Combine(outputDirectory, "Calc.Tests.deps.json")));
    }

    [Fact]
    public void Project_named_like_a_test_project_without_tests_is_reported_as_not_recorded()
    {
        WriteSolution(testingPlatform: true);
        // Like an AOT smoke-test app: named *Test, but an ordinary executable with no tests.
        _repo.Write("tests/Calc.AotTest/Calc.AotTest.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/Calc/Calc.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.AotTest/Program.cs", "System.Console.WriteLine(new Calc.Calculator().Add(1, 2));");
        _repo.Commit("aot smoke test");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var aot = graph.Projects.Single(p => p.Path.EndsWith("Calc.AotTest.csproj", StringComparison.Ordinal));

        Assert.True(aot.IsTestProject);
        Assert.Throws<NoTestsRecordedException>(() =>
            new CoverageRecorder(NullLoggerFactory.Instance).Record(new RecordRequest(_repo.Root, graph, aot, new RecordOptions()), TextWriter.Null));
    }

    private static HashSet<CoveredMethod> Ran(CoverageMap map, string testClass) =>
        [.. map.Classes[testClass].Select(id => map.Methods[id])];

    public void Dispose() => _repo.Dispose();
}
