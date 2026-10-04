using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Assembly names set outside the project file or computed by MSBuild. The recorder finds the
/// assemblies to instrument by these names, so a wrong name silently leaves a library unrecorded.
/// </summary>
public sealed class AssemblyNameTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Name_set_in_a_props_file_from_the_project_name_is_resolved()
    {
        _repo.Write("Directory.Build.props", """
            <Project><PropertyGroup><AssemblyName>Acme.$(MSBuildProjectName)</AssemblyName></PropertyGroup></Project>
            """);
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        var project = Load().Single();

        Assert.Equal("Acme.Lib", project.AssemblyName);
        Assert.False(project.AssemblyNameUnresolved);
    }

    [Theory]
    [InlineData("$(MSBuildProjectName.Substring(0, 3))")]
    [InlineData("$([System.String]::Copy('Lib'))")]
    [InlineData("$(UnknownProperty).Core")]
    public void Name_that_needs_evaluation_is_marked_unresolved(string value)
    {
        _repo.Write("src/Lib/Lib.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>{value}</AssemblyName></PropertyGroup></Project>
            """);

        var project = Load().Single();

        Assert.True(project.AssemblyNameUnresolved);
        Assert.Equal("Lib", project.AssemblyName);
    }

    [Fact]
    public void Names_set_to_different_values_in_different_files_are_marked_unresolved()
    {
        // Conditions are not evaluated, so which one wins is unknown.
        _repo.Write("Directory.Build.props", """
            <Project><PropertyGroup><AssemblyName>Acme.$(MSBuildProjectName)</AssemblyName></PropertyGroup></Project>
            """);
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName Condition="'$(X)' == ''">Other</AssemblyName></PropertyGroup></Project>
            """);

        Assert.True(Load().Single().AssemblyNameUnresolved);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Unresolved_names_are_evaluated_by_msbuild()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Lib.Generators/Lib.Generators.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>$(MSBuildProjectName.Substring(0, 3))</AssemblyName></PropertyGroup>
            </Project>
            """);

        var names = new ProjectAssemblies(NullLogger.Instance).FileNames(LoadGraph(), _repo.Root);

        Assert.Contains("Lib.dll", names);
    }

    /// <summary>Loads the projects written so far.</summary>
    private ProjectGraph LoadGraph() => new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

    /// <summary>The projects written so far.</summary>
    private IEnumerable<ProjectNode> Load() => LoadGraph().Projects;

    public void Dispose() => _repo.Dispose();
}
