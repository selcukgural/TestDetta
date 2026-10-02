using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

/// <summary>
/// Several projects in one directory compile the same files (Meziantou.Analyzer builds one project
/// per Roslyn version from one folder, and tests each with its own test project in one folder). A
/// file there belongs to all of them, or every project but one is never selected.
/// </summary>
public sealed class SharedDirectoryTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void File_in_a_directory_with_several_projects_belongs_to_each()
    {
        _repo.Write("src/Lib/Lib.roslyn4.8.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Lib.roslyn5.0.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Rule.cs", "namespace Lib; public static class Rule { public static int Check() => 1; }");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var owners = graph.ResolveFile(Paths.Normalize(Path.Combine(_repo.Root, "src/Lib/Rule.cs")));

        Assert.Equal(
            ["Lib.roslyn4.8.csproj", "Lib.roslyn5.0.csproj"],
            owners.Where(static o => o.Role == FileRole.Source).Select(static o => Path.GetFileName(o.Project.Path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Change_selects_the_test_classes_of_every_test_project_sharing_the_directory()
    {
        _repo.Write("src/Lib/Lib.roslyn4.8.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Lib.roslyn5.0.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Rule.cs", "namespace Lib; public static class Rule { public static int Check() => 1; }");
        foreach (var version in new[] { "roslyn4.8", "roslyn5.0" })
        {
            _repo.Write($"tests/Lib.Tests/Lib.Tests.{version}.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><PackageReference Include="xunit.v3" /><ProjectReference Include="../../src/Lib/Lib.{version}.csproj" /></ItemGroup>
                </Project>
                """);
        }

        _repo.Write("tests/Lib.Tests/RuleTests.cs", "namespace Lib.Tests; public class RuleTests { [Xunit.Fact] public void Checks() => Xunit.Assert.Equal(1, Lib.Rule.Check()); }");
        _repo.Commit("base");

        _repo.Replace("src/Lib/Rule.cs", "=> 1;", "=> 0 + 1;");
        var tests = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests;

        Assert.Equal(2, tests.Count);
        Assert.All(tests, t => Assert.True(t.Mode == TestRunMode.All || t.TestClasses.Contains("Lib.Tests.RuleTests"), t.Reason));
    }

    [Fact]
    public void Linked_file_whose_path_needs_evaluation_belongs_to_the_linking_project()
    {
        // OpenTelemetry compiles test files of one test project into others through $(RepoRoot),
        // which a property function computes: the linking project must own them too.
        _repo.Write("build/Common.props", """
            <Project><PropertyGroup><RepoRoot>$([System.IO.Directory]::GetParent($(MSBuildThisFileDirectory)).Parent.FullName)</RepoRoot></PropertyGroup></Project>
            """);
        _repo.Write("test/Directory.Build.props", """<Project><Import Project="../build/Common.props" /></Project>""");
        _repo.Write("test/Core.Tests/Core.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("test/Core.Tests/Metrics/MetricApiTests.cs", "namespace Core.Tests; public class MetricApiTests { }");
        _repo.Write("test/Hosting.Tests/Hosting.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="$(RepoRoot)\test\Core.Tests\Metrics\MetricApiTests.cs" Link="Includes\MetricApiTests.cs" /></ItemGroup>
            </Project>
            """);
        _repo.Write("test/Core.Tests/Other.cs", "namespace Core.Tests; public class Other { }");
        // Paths with nothing literal to match (OpenTelemetry's Common.prod.props) must not claim every file.
        _repo.Write("build/Common.prod.props", """
            <Project><ItemGroup>
              <Content Include="@(SigstoreBundleWithTfm)" Pack="True" />
              <Compile Include="$(SharedSources)**/*.cs" />
              <None Include="$(RepoRoot)\test\Core.Tests\Metrics\MetricApiTests.cs" />
            </ItemGroup></Project>
            """);
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><Import Project="../../build/Common.prod.props" /></Project>""");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        string[] Owners(string file) => [.. graph.ResolveFile(Paths.Normalize(Path.Combine(_repo.Root, file)))
            .Where(static o => o.Role == FileRole.Source).Select(static o => Path.GetFileName(o.Project.Path)).Order(StringComparer.Ordinal)];

        Assert.Equal(["Core.Tests.csproj", "Hosting.Tests.csproj"], Owners("test/Core.Tests/Metrics/MetricApiTests.cs"));
        // Only the linked file: the pattern's unknown part is the root, not the file name.
        Assert.Equal(["Core.Tests.csproj"], Owners("test/Core.Tests/Other.cs"));
    }

    public void Dispose() => _repo.Dispose();
}
