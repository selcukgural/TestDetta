using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A helper program the tests compile against is instrumented like any repository assembly; when the tests start
/// it from their output as a process of its own, its <c>deps.json</c> must list TestDetta's runtime, or its probes
/// find no tracker and it dies (CliWrap's <c>CliWrap.Tests.Dummy</c>, exit code 134). Slow (dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class InstrumentedApplicationTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public InstrumentedApplicationTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("tools/Dummy/Dummy.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("tools/Dummy/Program.cs", """
            namespace Dummy;
            public static class Program
            {
                public const string Greeting = "ok";
                public static int Main() { Console.WriteLine(Greeting); return 0; }
            }
            """);
        _repo.Write("tests/Dummy.Tests/Dummy.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../tools/Dummy/Dummy.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Dummy.Tests/Tests.cs", """
            using System.Diagnostics;
            using Xunit;
            namespace Dummy.Tests;

            // Starts the copy of the helper in the test output, which the recording instrumented.
            public class StartTests
            {
                [Fact]
                public void Runs()
                {
                    var info = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Dummy.dll")}\"") { RedirectStandardOutput = true };
                    using var process = Process.Start(info)!;
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();
                    Assert.Equal(0, process.ExitCode);
                    Assert.Equal(Dummy.Program.Greeting, output);
                }
            }
            """);
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_helper_program_the_tests_compile_against_and_start_runs_while_recording()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        var output = new StringWriter();

        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance).Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);

        Assert.True(passed, output.ToString());
        Assert.Empty(map.FailedClasses!);
        Assert.Contains("Dummy.Tests.StartTests", map.Classes.Keys);
        var deps = File.ReadAllText(Path.Combine(_repo.Root, "tests", "Dummy.Tests", "bin", "Debug", "net10.0", "Dummy.deps.json"));
        Assert.DoesNotContain("TestDetta.Coverage.Runtime", deps, StringComparison.Ordinal);
    }
}
