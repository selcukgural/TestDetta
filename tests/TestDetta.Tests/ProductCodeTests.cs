using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Which projects' code also runs outside the tests (docs/design/loaded-outputs.md): their loads by name are the product
/// finding its modules, while a test or a helper only tests use may load an output by name to inspect it.
/// </summary>
public sealed class ProductCodeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Code_an_application_uses_runs_outside_the_tests_and_code_only_tests_use_does_not()
    {
        Project("src/Catalog/Catalog.csproj");
        Project("src/App/App.csproj", "../Catalog/Catalog.csproj");
        Project("src/Lonely/Lonely.csproj");
        Project("src/Lib/Lib.csproj");
        Project("tests/Arch.Helpers/Arch.Helpers.csproj", "../../src/Lib/Lib.csproj");
        Project("tests/Lib.Tests/Lib.Tests.csproj", "../../src/Catalog/Catalog.csproj", "../../src/Lib/Lib.csproj", "../Arch.Helpers/Arch.Helpers.csproj");
        _repo.Commit("base");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

        bool Runs(string name) => graph.RunsOutsideTests(graph.Projects.Single(p => Path.GetFileNameWithoutExtension(p.Path) == name).Path);

        Assert.True(Runs("Catalog"));
        Assert.True(Runs("App"));
        Assert.True(Runs("Lonely"));

        // A library only its tests reference may be inspected by them; so may a helper only tests use.
        Assert.False(Runs("Lib"));
        Assert.False(Runs("Arch.Helpers"));
        Assert.False(Runs("Lib.Tests"));
        Assert.False(graph.RunsOutsideTests(Path.Combine(_repo.Root, "src/Unknown/Unknown.csproj")));
    }

    /// <summary>Writes an SDK project referencing <paramref name="references"/>.</summary>
    /// <param name="path">Repository-relative project path.</param>
    /// <param name="references">Paths relative to the project's directory.</param>
    private void Project(string path, params string[] references)
    {
        var items = string.Concat(references.Select(static r => $"""<ProjectReference Include="{r}" />"""));
        var test = path.EndsWith(".Tests.csproj", StringComparison.Ordinal) ? """<PackageReference Include="Microsoft.NET.Test.Sdk" />""" : string.Empty;
        _repo.Write(path, $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{items}{test}</ItemGroup></Project>""");
    }
}
