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

    [Fact]
    public void Test_project_is_recognised_from_its_sdk()
    {
        _repo.Write("src/Checks/Checks.csproj", """<Project Sdk="MSTest.Sdk/3.8.0" />""");

        Assert.True(Single().IsTestProject);
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
