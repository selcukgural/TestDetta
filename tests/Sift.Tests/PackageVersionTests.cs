using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

/// <summary>A central package version bump runs only the projects that use the package.</summary>
public sealed class PackageVersionTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public PackageVersionTests()
    {
        _repo.Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Json.Lib" Version="1.0.0" />
                <PackageVersion Include="Http.Lib" Version="2.0.0" />
                <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.0.0" />
              </ItemGroup>
            </Project>
            """);
        Project("src/Parser", "Parser", "Json.Lib");
        Project("src/Client", "Client", "Http.Lib");
        TestProject("tests/Parser.Tests", "Parser");
        TestProject("tests/Client.Tests", "Client");
        _repo.Commit("base");
    }

    [Fact]
    public void Version_bump_runs_only_projects_that_reference_the_package()
    {
        _repo.Replace("Directory.Packages.props", "\"Json.Lib\" Version=\"1.0.0\"", "\"Json.Lib\" Version=\"1.1.0\"");

        var tests = Tests();
        Assert.Equal(TestRunMode.All, tests.Single(t => t.Project.Path.Contains("Parser.Tests", StringComparison.Ordinal)).Mode);
        Assert.DoesNotContain(tests, t => t.Project.Path.Contains("Client.Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void Test_package_bump_runs_every_test_project()
    {
        _repo.Replace("Directory.Packages.props", "Version=\"17.0.0\"", "Version=\"18.0.0\"");

        Assert.All(Tests(), t => Assert.Equal(TestRunMode.All, t.Mode));
        Assert.Equal(2, Tests().Count);
    }

    [Theory]
    [InlineData("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
        "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>")]
    [InlineData("<ItemGroup>", "<ItemGroup><GlobalPackageReference Include=\"Analyzers\" Version=\"1.0.0\" />")]
    public void Other_changes_still_affect_every_project(string oldValue, string newValue)
    {
        _repo.Replace("Directory.Packages.props", oldValue, newValue);
        _repo.Replace("Directory.Packages.props", "\"Json.Lib\" Version=\"1.0.0\"", "\"Json.Lib\" Version=\"1.1.0\"");

        Assert.Equal(2, Tests().Count(t => t.Mode == TestRunMode.All));
    }

    [Fact]
    public void Comments_and_whitespace_do_not_widen_a_version_bump()
    {
        _repo.Replace("Directory.Packages.props", "<ItemGroup>", "<!-- bumped by a bot -->\n    <ItemGroup>");
        _repo.Replace("Directory.Packages.props", "\"Json.Lib\" Version=\"1.0.0\"", "\"Json.Lib\" Version=\"1.1.0\"");

        Assert.Single(Tests());
    }

    private IReadOnlyList<TestProjectSelection> Tests() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests;

    private void Project(string directory, string name, string package) =>
        _repo.Write($"{directory}/{name}.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="{package}" /></ItemGroup>
            </Project>
            """);

    private void TestProject(string directory, string library) =>
        _repo.Write($"{directory}/{library}.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/{library}/{library}.csproj" />
              </ItemGroup>
            </Project>
            """);

    public void Dispose() => _repo.Dispose();
}
