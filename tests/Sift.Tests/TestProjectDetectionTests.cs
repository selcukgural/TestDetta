using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

/// <summary>Which projects count as test projects; a missed one gets no tests selected at all.</summary>
public sealed class TestProjectDetectionTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Theory]
    // xUnit v3 brings its own runner; no Microsoft.NET.Test.Sdk (AwesomeAssertions).
    [InlineData("Checks", """<ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup>""")]
    [InlineData("Checks", """<ItemGroup><PackageReference Include="MSTest" /></ItemGroup>""")]
    [InlineData("Checks", """<ItemGroup><PackageReference Include="NUnit3TestAdapter" /></ItemGroup>""")]
    [InlineData("Checks", """<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>""")]
    [InlineData("Lib.Specs", "")]
    public void Test_project_is_recognised_from_its_file(string name, string content)
    {
        _repo.Write($"src/{name}/{name}.csproj", $"""<Project Sdk="Microsoft.NET.Sdk">{content}</Project>""");

        Assert.True(Single().IsTestProject);
    }

    [Theory]
    [InlineData("MSTest.Sdk/3.8.0")]
    // A company SDK that brings the test packages along (Meziantou.Analyzer).
    [InlineData("Meziantou.NET.Sdk.Test")]
    public void Test_project_is_recognised_from_its_sdk(string sdk)
    {
        _repo.Write("src/Checks/Checks.csproj", $"""<Project Sdk="{sdk}" />""");

        Assert.True(Single().IsTestProject);
    }

    [Fact]
    public void Test_project_is_recognised_from_a_name_with_a_suffix_after_test()
    {
        // One test project per Roslyn version, all with the content in a shared Directory.Build.props (Meziantou.Analyzer).
        _repo.Write("tests/Lib.Test/Lib.Test.roslyn5.9.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        Assert.True(Single().IsTestProject);
    }

    [Fact]
    public void Project_reference_that_cannot_be_resolved_makes_the_project_depend_on_every_project()
    {
        // The reference sits in a Directory.Build.props and names the file through a property only
        // MSBuild can compute (Meziantou.Analyzer's $(RoslynVersion)).
        _repo.Write("src/Lib/Lib.roslyn5.9.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Other/Other.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("tests/Lib.Tests/Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <RoslynVersion>$([System.Text.RegularExpressions.Regex]::Match($(MSBuildProjectName), 'roslyn[0-9.]+$'))</RoslynVersion>
                <RoslynVersion Condition="'$(RoslynVersion)' == ''">roslyn5.9</RoslynVersion>
              </PropertyGroup>
              <ItemGroup><ProjectReference Include="$(MSBuildThisFileDirectory)../../src/Lib/Lib.$(RoslynVersion).csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Lib.Tests.roslyn4.8.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Lib.roslyn4.8.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("tests/Plain.Tests/Plain.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Other/Other.csproj" /></ItemGroup></Project>
            """);

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        // The fallback value names an existing project, but the value MSBuild computes first is unknown.
        var lib = graph.Projects.Single(p => p.Path.EndsWith("Lib.roslyn4.8.csproj", StringComparison.Ordinal)).Path;
        var tests = graph.Projects.Single(p => p.Path.EndsWith("Lib.Tests.roslyn4.8.csproj", StringComparison.Ordinal));
        var plain = graph.Projects.Single(p => p.Path.EndsWith("Plain.Tests.csproj", StringComparison.Ordinal)).Path;

        var other = graph.Projects.Single(p => p.Path.EndsWith("Other.csproj", StringComparison.Ordinal)).Path;
        var lib59 = graph.Projects.Single(p => p.Path.EndsWith("Lib.roslyn5.9.csproj", StringComparison.Ordinal)).Path;

        Assert.True(tests.HasUnknownReferences);
        Assert.Contains(tests.Path, graph.WithDependents([lib]));
        Assert.Contains(tests.Path, graph.WithDependents([lib59]));
        // The unknown part is only the version: a project with another name is not a candidate.
        Assert.DoesNotContain(tests.Path, graph.WithDependents([other]));
        // A reference that resolves keeps the graph exact.
        Assert.DoesNotContain(plain, graph.WithDependents([lib]));
    }

    [Fact]
    public void Project_reference_to_an_item_list_is_resolved_from_the_items()
    {
        // Open-XML-SDK: <ProjectReference Include="@(OoxGenerator)" /> in an imported targets file.
        _repo.Write("gen/Gen/Gen.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("gen/Gen/Generator.targets", """
            <Project>
              <ItemGroup><OoxGenerator Include="$(MSBuildThisFileDirectory)Gen.csproj" /></ItemGroup>
              <ItemGroup><ProjectReference Include="@(OoxGenerator)" OutputItemType="Analyzer" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><Import Project="../../gen/Gen/Generator.targets" /></Project>""");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var lib = graph.Projects.Single(p => p.Path.EndsWith("Lib.csproj", StringComparison.Ordinal));

        Assert.False(lib.HasUnknownReferences);
        Assert.Contains(graph.Projects.Single(p => p.Path.EndsWith("Gen.csproj", StringComparison.Ordinal)).Path, lib.References);
    }

    [Fact]
    public void Project_reference_declared_in_an_import_is_followed()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("tests/Lib.Tests/Directory.Build.props", """
            <Project><ItemGroup><ProjectReference Include="$(MSBuildThisFileDirectory)../../src/Lib/Lib.csproj" /></ItemGroup></Project>
            """);
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var lib = graph.Projects.Single(p => p.Path.EndsWith("Lib.csproj", StringComparison.Ordinal)).Path;
        var tests = graph.Projects.Single(p => p.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.False(tests.HasUnknownReferences);
        Assert.Contains(lib, tests.References);
    }

    [Fact]
    public void Test_package_added_by_an_import_counts_unless_it_is_conditional()
    {
        _repo.Write("src/Directory.Build.props", """
            <Project>
              <ItemGroup Condition="'$(MSBuildProjectName)' == 'Other'"><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Assert.False(Single().IsTestProject);

        _repo.Write("src/Directory.Build.props", """
            <Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>
            """);
        Assert.True(Single().IsTestProject);
    }

    [Fact]
    public void Library_of_test_helpers_is_not_a_test_project()
    {
        // Referencing a framework package is what helper libraries (AutoFixture.xUnit3) do.
        _repo.Write("src/Helpers/Helpers.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit.v3.extensibility.core" /><PackageReference Include="NUnit" /></ItemGroup></Project>
            """);

        Assert.False(Single().IsTestProject);
    }

    private ProjectNode Single() =>
        new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root).Projects.Single();

    public void Dispose() => _repo.Dispose();
}
