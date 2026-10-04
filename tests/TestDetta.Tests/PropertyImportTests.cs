using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Imports whose path is built from a property the project sets, as in
/// <c>&lt;Import Project="eng/$(ProjectType).targets" /&gt;</c>, which is how some repositories give
/// test projects their test packages.
/// </summary>
public sealed class PropertyImportTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public PropertyImportTests()
    {
        _repo.Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.0.0" />
                <PackageVersion Include="Json.Lib" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("Directory.Build.targets", """
            <Project>
              <Import Project="$(MsBuildThisFileDirectory)eng/$(Kind).targets" Condition="'$(Kind)' != ''" />
            </Project>
            """);
        _repo.Write("eng/Test.targets", """
            <Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>
            """);
        _repo.Write("eng/Library.targets", """
            <Project><ItemGroup><PackageReference Include="Json.Lib" /></ItemGroup></Project>
            """);
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Kind>Library</Kind></PropertyGroup></Project>
            """);
        _repo.Write("src/Lib/Code.cs", "namespace Lib; public class Code { }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><Kind>Test</Kind></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", "namespace Lib.Tests; public class CodeTests { [Fact] public void Exists() => Assert.NotNull(new Code()); }");
        _repo.Commit("base");
    }

    [Fact]
    public void Import_path_from_a_project_property_is_resolved()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var tests = graph.Projects.Single(p => p.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));
        var library = graph.Projects.Single(p => p.Path.EndsWith("Lib.csproj", StringComparison.Ordinal));

        Assert.Contains(tests.Imports, i => i.EndsWith("eng/Test.targets", StringComparison.Ordinal));
        Assert.Contains("Microsoft.NET.Test.Sdk", tests.PackageReferences);
        Assert.Contains(library.Imports, i => i.EndsWith("eng/Library.targets", StringComparison.Ordinal));
        Assert.DoesNotContain("Microsoft.NET.Test.Sdk", library.PackageReferences);
        Assert.False(tests.HasUnresolvedInputs);
    }

    [Fact]
    public void Test_package_bump_reaches_projects_that_get_it_through_a_property_import()
    {
        _repo.Replace("Directory.Packages.props", "Version=\"17.0.0\"", "Version=\"18.0.0\"");

        Assert.Equal(TestRunMode.All, Tests().Single().Mode);
    }

    [Fact]
    public void Change_to_the_imported_file_runs_its_importers()
    {
        _repo.Replace("eng/Test.targets", "<Project>", "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup>");

        var tests = Tests().Single();
        Assert.Equal(TestRunMode.All, tests.Mode);
        Assert.Contains("eng/Test.targets", tests.Reason);
    }

    [Fact]
    public void Package_bump_counts_for_projects_whose_imports_could_not_be_resolved()
    {
        // Kind is set nowhere now, so where the test package comes from is unknown.
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", "<PropertyGroup><Kind>Test</Kind></PropertyGroup>", "");
        _repo.Commit("no kind");
        _repo.Replace("Directory.Packages.props", "Version=\"17.0.0\"", "Version=\"18.0.0\"");

        Assert.Equal(TestRunMode.All, Tests().Single().Mode);
    }

    private IReadOnlyList<TestProjectSelection> Tests() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests;

    public void Dispose() => _repo.Dispose();
}
