using Sift.Core;

namespace Sift.Tests;

public sealed class ImpactAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sift-" + Guid.NewGuid().ToString("N"));

    public ImpactAnalyzerTests()
    {
        // Domain <- App <- App.Tests ; Domain <- Domain.Tests ; Other <- Other.Tests
        Project("src/Domain/Domain.csproj");
        Project("src/App/App.csproj", refs: ["../Domain/Domain.csproj"]);
        Project("src/Other/Other.csproj");
        Project("tests/App.Tests/App.Tests.csproj", test: true, refs: [@"..\..\src\App\App.csproj"]);
        Project("tests/Domain.Tests/Domain.Tests.csproj", test: true, refs: ["../../src/Domain/Domain.csproj"]);
        Project("tests/Other.Tests/Other.Tests.csproj", test: true, refs: ["../../src/Other/Other.csproj"]);
    }

    [Fact]
    public void Change_in_leaf_library_selects_all_transitive_dependents()
    {
        var result = Analyze("src/Domain/Order.cs");

        Assert.Equal(["App.Tests", "Domain.Tests"], TestNames(result));
        Assert.Equal(ImpactReason.DirectChange, Reason(result, "Domain"));
        Assert.Equal(ImpactReason.Dependency, Reason(result, "App"));
    }

    [Fact]
    public void Change_in_unrelated_project_does_not_select_other_tests()
    {
        Assert.Equal(["Other.Tests"], TestNames(Analyze("src/Other/Thing.cs")));
    }

    [Fact]
    public void Root_build_props_affects_every_project()
    {
        var result = Analyze("Directory.Build.props");

        Assert.Equal(6, result.Affected.Count);
        Assert.All(result.Affected, a => Assert.Equal(ImpactReason.BuildConfiguration, a.Reason));
    }

    [Fact]
    public void Nested_build_props_affects_only_projects_beneath_it()
    {
        Assert.Equal(["App.Tests", "Domain.Tests", "Other.Tests"], TestNames(Analyze("tests/Directory.Build.props")));
    }

    [Fact]
    public void Files_outside_projects_are_reported_as_unmapped()
    {
        var result = Analyze("README.md", "docs/guide.md");

        Assert.Empty(result.Affected);
        Assert.Equal(["README.md", "docs/guide.md"], result.UnmappedFiles);
    }

    [Fact]
    public void Nested_project_directory_owns_its_files()
    {
        Project("src/Other/Plugin/Plugin.csproj");

        var result = Analyze("src/Other/Plugin/P.cs");

        Assert.Equal(["Plugin"], result.Affected.Select(a => Name(a.Project)));
    }

    private ImpactResult Analyze(params string[] changed) =>
        ImpactAnalyzer.Analyze(ProjectGraph.Load(_root), _root, changed);

    private static string Name(ProjectNode p) => Path.GetFileNameWithoutExtension(p.Path);

    private static string[] TestNames(ImpactResult r) => r.Tests.Select(a => Name(a.Project)).Order().ToArray();

    private static ImpactReason Reason(ImpactResult r, string name) =>
        r.Affected.Single(a => Name(a.Project) == name).Reason;

    private void Project(string relativePath, bool test = false, string[]? refs = null)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var references = string.Concat((refs ?? []).Select(r => $"""<ProjectReference Include="{r}" />"""));
        var testSdk = test ? """<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" />""" : "";
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>{references}{testSdk}</ItemGroup>
            </Project>
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
