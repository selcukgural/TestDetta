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

    public void Dispose() => _repo.Dispose();
}
