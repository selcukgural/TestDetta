using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A legacy solution (non-SDK project files, <c>packages.config</c>, .NET Framework 4.8): TestDetta
/// recognises it, records it with the full MSBuild and selects and runs its tests from the compiled
/// assembly (docs/design/legacy-projects.md). The end-to-end test needs the full MSBuild (Mono's
/// <c>msbuild</c> outside Windows, which also runs the .NET Framework test host), so it is in the
/// <c>Legacy</c> category and returns at once without it.
/// </summary>
public sealed class LegacyProjectTests : IDisposable
{
    private const string CalcProject = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"" Condition=""Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')"" />
  <PropertyGroup>
    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>
    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>
    <ProjectGuid>{11111111-1111-1111-1111-111111111111}</ProjectGuid>
    <OutputType>Library</OutputType>
    <RootNamespace>Calc</RootNamespace>
    <AssemblyName>Calc</AssemblyName>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <DebugType>portable</DebugType>
    <DebugSymbols>true</DebugSymbols>
    <OutputPath>bin\$(Configuration)\</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""System"" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include=""Calculator.cs"" />
  </ItemGroup>
  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />
</Project>
";

    private const string TestProject = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""..\..\packages\xunit.runner.visualstudio.2.4.5\build\net462\xunit.runner.visualstudio.props"" Condition=""Exists('..\..\packages\xunit.runner.visualstudio.2.4.5\build\net462\xunit.runner.visualstudio.props')"" />
  <Import Project=""..\..\packages\xunit.core.2.4.2\build\xunit.core.props"" Condition=""Exists('..\..\packages\xunit.core.2.4.2\build\xunit.core.props')"" />
  <Import Project=""$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"" Condition=""Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')"" />
  <PropertyGroup>
    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>
    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>
    <ProjectGuid>{22222222-2222-2222-2222-222222222222}</ProjectGuid>
    <OutputType>Library</OutputType>
    <RootNamespace>Calc.Tests</RootNamespace>
    <AssemblyName>Calc.Tests</AssemblyName>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <DebugType>portable</DebugType>
    <DebugSymbols>true</DebugSymbols>
    <OutputPath>bin\$(Configuration)\</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""System"" />
    <Reference Include=""xunit.abstractions""><HintPath>..\..\packages\xunit.abstractions.2.0.3\lib\net35\xunit.abstractions.dll</HintPath></Reference>
    <Reference Include=""xunit.assert""><HintPath>..\..\packages\xunit.assert.2.4.2\lib\netstandard1.1\xunit.assert.dll</HintPath></Reference>
    <Reference Include=""xunit.core""><HintPath>..\..\packages\xunit.extensibility.core.2.4.2\lib\net452\xunit.core.dll</HintPath></Reference>
    <Reference Include=""xunit.execution.desktop""><HintPath>..\..\packages\xunit.extensibility.execution.2.4.2\lib\net452\xunit.execution.desktop.dll</HintPath></Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include=""CalculatorTests.cs"" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include=""..\..\src\Calc\Calc.csproj""><Project>{11111111-1111-1111-1111-111111111111}</Project><Name>Calc</Name></ProjectReference>
  </ItemGroup>
  <ItemGroup>
    <None Include=""packages.config"" />
  </ItemGroup>
  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />
  <Import Project=""..\..\packages\xunit.core.2.4.2\build\xunit.core.targets"" Condition=""Exists('..\..\packages\xunit.core.2.4.2\build\xunit.core.targets')"" />
</Project>
";

    private const string Packages = @"<?xml version=""1.0"" encoding=""utf-8""?>
<packages>
  <package id=""xunit"" version=""2.4.2"" targetFramework=""net48"" />
  <package id=""xunit.abstractions"" version=""2.0.3"" targetFramework=""net48"" />
  <package id=""xunit.assert"" version=""2.4.2"" targetFramework=""net48"" />
  <package id=""xunit.core"" version=""2.4.2"" targetFramework=""net48"" />
  <package id=""xunit.extensibility.core"" version=""2.4.2"" targetFramework=""net48"" />
  <package id=""xunit.extensibility.execution"" version=""2.4.2"" targetFramework=""net48"" />
  <package id=""xunit.runner.visualstudio"" version=""2.4.5"" targetFramework=""net48"" developmentDependency=""true"" />
</packages>
";

    private const string Solution = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Calc"", ""src\Calc\Calc.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Calc.Tests"", ""tests\Calc.Tests\Calc.Tests.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
	EndGlobalSection
EndGlobal
";

    private readonly GitRepoFixture _repo = new();

    public LegacyProjectTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("Legacy.sln", Solution);
        _repo.Write("src/Calc/Calc.csproj", CalcProject);
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc { public static class Calculator { public static int Add(int a, int b) { return a + b; } public static int Sub(int a, int b) { return a - b; } } }");
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", TestProject);
        _repo.Write("tests/Calc.Tests/packages.config", Packages);
        _repo.Write("tests/Calc.Tests/CalculatorTests.cs", """
            using Xunit;
            namespace Calc.Tests
            {
                public class AddTests { [Fact] public void Adds() { Assert.Equal(3, Calculator.Add(1, 2)); } }
                public class SubTests { [Fact] public void Subtracts() { Assert.Equal(1, Calculator.Sub(3, 2)); } }
            }
            """);
        _repo.Write(".gitignore", "bin/\nobj/\npackages/\n.testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Legacy_project_files_are_recognised_and_need_the_full_msbuild()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var tests = graph.Projects.Single(static p => p.Path.EndsWith("Calc.Tests.csproj", StringComparison.Ordinal));

        Assert.False(tests.SdkStyle);
        Assert.True(tests.IsTestProject);
        Assert.True(graph.NeedsFullMsBuild(tests));
        Assert.Equal(Paths.Normalize(Path.Combine(_repo.Root, "Legacy.sln")), FullMsBuild.SolutionOf(_repo.Root, tests.Path));
    }

    [Fact]
    public void An_sdk_project_that_references_a_legacy_one_needs_the_full_msbuild_too()
    {
        _repo.Write("tests/New.Tests/New.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/Calc/Calc.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Modern/Modern.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

        Assert.True(graph.NeedsFullMsBuild(graph.Projects.Single(static p => p.Path.EndsWith("New.Tests.csproj", StringComparison.Ordinal))));
        Assert.False(graph.NeedsFullMsBuild(graph.Projects.Single(static p => p.Path.EndsWith("Modern.csproj", StringComparison.Ordinal))));
    }

    [Fact]
    public void The_assembly_run_drops_the_arguments_only_a_project_build_takes()
    {
        Assert.Equal(
            ["--logger", "trx", "--", "x=y"],
            TestInvocationBuilder.WithoutBuildArguments(["-c", "Release", "--no-build", "--logger", "trx", "-f", "net48", "--no-restore", "--", "x=y"]));
    }

    [Fact]
    [Trait("Category", "Legacy")]
    public void A_legacy_solution_is_recorded_and_its_changed_class_runs_alone()
    {
        if (FullMsBuild.Locate() is null || (!OperatingSystem.IsWindows() && !OnPath("mono")))
        {
            return;
        }

        var record = new StringWriter();
        Assert.True(CliApp.Run(["record", "--repo", _repo.Root], record, record) == 0, record.ToString());
        var map = CoverageMapStore.Read(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Calc.Tests/Calc.Tests.csproj")))!;
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Add"), map.Classes["Calc.Tests.AddTests"].Select(id => map.Methods[id]));

        _repo.Replace("src/Calc/Calculator.cs", "return a + b;", "return b + a;");
        var test = new StringWriter();
        var host = new CliHost(static _ => null);
        var exit = CliApp.Run(["test", "--repo", _repo.Root, "--base", "HEAD"], test, test, host);

        Assert.True(exit == 0, test.ToString());
        Assert.Contains("Calc.Tests.AddTests", test.ToString(), StringComparison.Ordinal);
        Assert.Contains("Passed!  - Failed:     0, Passed:     1,", test.ToString(), StringComparison.Ordinal);
    }

    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, program)));
}
