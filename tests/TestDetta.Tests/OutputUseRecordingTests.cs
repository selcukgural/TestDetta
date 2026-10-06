using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Recording keeps, by name, which outputs each test class started as a process or loaded as an
/// assembly (docs/design/runtime-file-reads.md): through the repository's own calls, and through the
/// startup hook for a .NET process started by code the recording cannot rewrite (here reflection,
/// standing in for a third-party library). Slow (it runs dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class OutputUseRecordingTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public OutputUseRecordingTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("tools/Helper/Helper.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("tools/Helper/Program.cs", """
            namespace Helper;
            public static class Program
            {
                public static int Main() { Console.WriteLine("ok"); return 0; }
                public static string Name() => "helper";
            }
            """);
        _repo.Write("tests/Tools.Tests/Tools.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../tools/Helper/Helper.csproj" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Tools.Tests/Tests.cs", """
            using System.Diagnostics;
            using System.Reflection;
            using Xunit;
            namespace Tools.Tests;

            static class HelperPath
            {
                public static string Dll()
                {
                    var root = AppContext.BaseDirectory;
                    while (!File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root)!;
                    return Path.Combine(root, "tools", "Helper", "bin", "Debug", "net10.0", "Helper.dll");
                }

                public static ProcessStartInfo Info() => new("dotnet", $"\"{Dll()}\"") { RedirectStandardOutput = true };
            }

            // Starts the helper through its own call.
            public class StartTests
            {
                [Fact]
                public void Runs()
                {
                    using var process = new Process { StartInfo = HelperPath.Info() };
                    process.Start();
                    Assert.Equal("ok", process.StandardOutput.ReadToEnd().Trim());
                    process.WaitForExit();
                }
            }

            // Starts it through reflection, as a third-party library would: no call to rewrite.
            public class HiddenStartTests
            {
                [Fact]
                public void Runs()
                {
                    var start = typeof(Process).GetMethod(nameof(Process.Start), [typeof(ProcessStartInfo)])!;
                    using var process = (Process)start.Invoke(null, [HelperPath.Info()])!;
                    Assert.Equal("ok", process.StandardOutput.ReadToEnd().Trim());
                    process.WaitForExit();
                }
            }

            // Loads it as an assembly.
            public class LoadTests
            {
                [Fact] public void Loads() => Assert.Equal("Helper", Assembly.LoadFrom(HelperPath.Dll()).GetName().Name);
            }

            public class PlainTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Records_the_outputs_each_class_started_or_loaded()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();

        CoverageMap map;
        bool passed;
        try
        {
            (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
                .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
        }

        Assert.True(passed, output.ToString());
        var uses = Assert.IsType<FileReads>(map.OutputUses);
        Assert.Contains("Helper", uses.Classes["Tools.Tests.StartTests"]);
        Assert.Contains("Helper", uses.Classes["Tools.Tests.HiddenStartTests"]);
        Assert.Contains("Helper", uses.Classes["Tools.Tests.LoadTests"]);
        Assert.False(uses.Classes.ContainsKey("Tools.Tests.PlainTests"));
        Assert.DoesNotContain("Helper", uses.Ambient);

        var path = Path.Combine(_repo.Root, ".testdetta", "roundtrip.json");
        CoverageMapStore.Write(path, map);
        Assert.Equal(uses.Classes["Tools.Tests.HiddenStartTests"], CoverageMapStore.Read(path)!.OutputUses!.Classes["Tools.Tests.HiddenStartTests"]);
    }
}
