using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A test project whose build uses the output of a tool it references without compiling against it
/// (docs/design/runtime-file-reads.md): when the build only copies the output next to the tests
/// (LibGit2Sharp's test app), what the build feeds the compiler stays the same and a change to the tool
/// selects the classes that start it; when the build runs the tool to write code it compiles, the
/// written code changes and the project runs as a whole. Slow (it records and builds).
/// </summary>
[Trait("Category", "Slow")]
public sealed class OutputUseBuildTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public OutputUseBuildTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("tools/Tool/Tool.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        _repo.Write("tools/Tool/Program.cs", """
            namespace Tool;
            public static class Program
            {
                public static void Main() => System.Console.Write(Code());
                public static string Code() => "namespace Tools.Tests; public static class Generated { public const string Value = \"1\"; }";
            }
            """);
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_tool_the_build_only_copies_selects_the_classes_that_start_it()
    {
        WriteTests(build: """
            <ItemGroup>
              <ProjectReference Include="../../tools/Tool/Tool.csproj" ReferenceOutputAssembly="false" OutputItemType="ToolDll" />
            </ItemGroup>
            <Target Name="CopyTool" AfterTargets="ResolveProjectReferences">
              <ItemGroup>
                <Content Include="@(ToolDll)" CopyToOutputDirectory="PreserveNewest" />
                <Content Include="@(ToolDll->'%(RootDir)%(Directory)%(Filename).runtimeconfig.json')" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Target>
            """, tests: """
            public class StartTests
            {
                [Fact]
                public void Runs()
                {
                    var info = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Tool.dll")}\"") { RedirectStandardOutput = true };
                    using var process = System.Diagnostics.Process.Start(info)!;
                    Assert.Contains("Generated", process.StandardOutput.ReadToEnd());
                    process.WaitForExit();
                }
            }
            """);
        Record();
        _repo.Replace("tools/Tool/Program.cs", "\\\"1\\\"", "\\\"2\\\"");

        var selection = Select();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Tools.Tests.StartTests"], selection.TestClasses);
    }

    [Fact]
    public void A_tool_the_build_runs_to_write_code_runs_the_whole_project()
    {
        WriteTests(build: """
            <ItemGroup>
              <ProjectReference Include="../../tools/Tool/Tool.csproj" ReferenceOutputAssembly="false" />
            </ItemGroup>
            <Target Name="WriteGenerated" BeforeTargets="CoreCompile" DependsOnTargets="ResolveProjectReferences">
              <Exec Command="dotnet &quot;$(MSBuildThisFileDirectory)../../tools/Tool/bin/$(Configuration)/net10.0/Tool.dll&quot; &gt; &quot;$(IntermediateOutputPath)Generated.g.cs&quot;" />
              <ItemGroup><Compile Include="$(IntermediateOutputPath)Generated.g.cs" /></ItemGroup>
            </Target>
            """, tests: """
            public class GeneratedTests { [Fact] public void Reads() => Assert.NotEmpty(Generated.Value); }
            """);
        Record();
        _repo.Replace("tools/Tool/Program.cs", "\\\"1\\\"", "\\\"2\\\"");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("what the build fed its compiler changed: Generated.g.cs", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copied_tool_of_a_multi_targeted_project_selects_its_starters_when_every_framework_builds_the_same()
    {
        ToolForBothFrameworks();
        WriteTests(build: CopyTool, tests: StartTests, frameworks: Frameworks);
        var map = Record();
        Assert.Equal(["net8.0", "net10.0"], map.GeneratedFrameworks!);
        Assert.Contains("tests/Tools.Tests/Tools.Tests.csproj", map.FrameworkBuildInputs!["net10.0"].Keys);
        _repo.Replace("tools/Tool/Program.cs", "\\\"1\\\"", "\\\"2\\\"");

        var selection = Select();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Tools.Tests.StartTests"], selection.TestClasses);
    }

    [Fact]
    public void A_tool_that_writes_code_runs_a_multi_targeted_project_completely()
    {
        ToolForBothFrameworks();
        WriteTests(build: WriteGenerated("net8.0"), tests: GeneratedTests, frameworks: Frameworks);
        Record();
        _repo.Replace("tools/Tool/Program.cs", "\\\"1\\\"", "\\\"2\\\"");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("build fed its compiler changed: Generated.g.cs", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_of_one_framework_answers_only_a_run_of_that_framework()
    {
        ToolForBothFrameworks();
        WriteTests(build: CopyTool, tests: StartTests, frameworks: Frameworks);
        var map = Record(framework: "net10.0");
        Assert.Null(map.FrameworkBuildInputs);
        _repo.Replace("tools/Tool/Program.cs", "\\\"1\\\"", "\\\"2\\\"");

        // dotnet test without -f runs net8.0 too, whose build the map does not hold.
        var everyFramework = Select();
        var oneFramework = Select(framework: "net10.0");

        Assert.Equal(TestRunMode.All, everyFramework.Mode);
        Assert.Contains("its map holds the build of net10.0 only", everyFramework.Reason, StringComparison.Ordinal);
        Assert.True(oneFramework.Mode == TestRunMode.Filtered, $"{oneFramework.Mode}: {oneFramework.Reason}");
        Assert.Equal(["Tools.Tests.StartTests"], oneFramework.TestClasses);
    }

    /// <summary>Both frameworks of the multi-targeted test project, the one recorded without -f first.</summary>
    private const string Frameworks = "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>";

    /// <summary>A build that only copies the tool next to the tests.</summary>
    private const string CopyTool = """
        <ItemGroup>
          <ProjectReference Include="../../tools/Tool/Tool.csproj" ReferenceOutputAssembly="false" OutputItemType="ToolDll" />
        </ItemGroup>
        <Target Name="CopyTool" AfterTargets="ResolveProjectReferences">
          <ItemGroup>
            <Content Include="@(ToolDll)" CopyToOutputDirectory="PreserveNewest" />
            <Content Include="@(ToolDll->'%(RootDir)%(Directory)%(Filename).runtimeconfig.json')" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>
        </Target>
        """;

    /// <summary>A test class that starts the copied tool.</summary>
    private const string StartTests = """
        public class StartTests
        {
            [Fact]
            public void Runs()
            {
                var info = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Tool.dll")}\"") { RedirectStandardOutput = true };
                using var process = System.Diagnostics.Process.Start(info)!;
                Assert.Contains("Generated", process.StandardOutput.ReadToEnd());
                process.WaitForExit();
            }
        }
        """;

    /// <summary>A test class that reads the code the tool wrote.</summary>
    private const string GeneratedTests = "public class GeneratedTests { [Fact] public void Reads() => Assert.NotEmpty(Generated.Value); }";

    /// <summary>A build that runs the tool, built for <paramref name="toolFramework"/>, to write code it compiles.</summary>
    /// <param name="toolFramework">The tool's framework, in its output path.</param>
    private static string WriteGenerated(string toolFramework) => $"""
        <ItemGroup>
          <ProjectReference Include="../../tools/Tool/Tool.csproj" ReferenceOutputAssembly="false" />
        </ItemGroup>
        <Target Name="WriteGenerated" BeforeTargets="CoreCompile" DependsOnTargets="ResolveProjectReferences">
          <Exec Command="dotnet &quot;$(MSBuildThisFileDirectory)../../tools/Tool/bin/$(Configuration)/{toolFramework}/Tool.dll&quot; &gt; &quot;$(IntermediateOutputPath)Generated.g.cs&quot;" />
          <ItemGroup><Compile Include="$(IntermediateOutputPath)Generated.g.cs" /></ItemGroup>
        </Target>
        """;

    private void WriteTests(string build, string tests, string frameworks = "<TargetFramework>net10.0</TargetFramework>")
    {
        _repo.Write("tests/Tools.Tests/Tools.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>{frameworks}<ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              {build}
            </Project>
            """);
        _repo.Write("tests/Tools.Tests/Tests.cs", $$"""
            using Xunit;
            namespace Tools.Tests;
            {{tests}}
            public class PlainTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Commit("base");
    }

    private CoverageMap Record(string? framework = null)
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();
        try
        {
            var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
                .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Framework: framework)), output);
            Assert.True(passed, output.ToString());
            Assert.NotNull(map.BuildInputs);
            CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
            return CoverageMapStore.Read(CoverageMapStore.PathFor(_repo.Root, testProject.Path))!;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
        }
    }

    private TestProjectSelection Select(string? framework = null) =>
        new AnalysisPipeline(NullLoggerFactory.Instance)
            .Run(_repo.Root, "main", new AnalysisOptions { GeneratedCode = new BuildGeneratedCodeReader(NullLoggerFactory.Instance, new GeneratedCodeBuild(Build: true)), Framework = framework })
            .Tests.Single(static t => t.Project.IsTestProject);

    /// <summary>The tool for a test project that targets net8.0 and net10.0: one it can reference from both.</summary>
    private void ToolForBothFrameworks() =>
        _repo.Replace("tools/Tool/Tool.csproj", "<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net8.0</TargetFramework>");
}
