using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Eval;

namespace Sift.Tests;

/// <summary>
/// Choosing the test projects to evaluate. Slow: asks MSBuild for each project's frameworks.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalStepsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Test_project_MSBuild_cannot_evaluate_is_left_out_instead_of_ending_the_evaluation()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("tests/Good.Tests/Good.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        // Like NSubstitute's old specs project: it imports a file the repository no longer has.
        _repo.Write("tests/Stale.Tests/Stale.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="../../Build/Gone.targets" />
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("base");

        var settings = new RunSettings(Parallelism: 1, Mutants: 1, Projects: [], new RepoCommands(Setup: null, Cleanup: null), Framework: "net10.0");
        var executor = new TestExecutor(new Sift.Eval.ExecutionContext(_repo.Root, DotnetTestMode.VSTest, settings, _repo.Root), NullLogger.Instance);
        var workspace = new EvalWorkspace(new EvalGit(_repo.Root), _repo.Root, Path.Combine(_repo.Root, "maps"), executor, NullLoggerFactory.Instance);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        Assert.Equal(2, graph.Projects.Count(p => p.IsTestProject));

        var projects = new EvalSteps(workspace, settings).TestProjects(graph);

        Assert.Equal("Good.Tests.csproj", Path.GetFileName(Assert.Single(projects).Path));
    }

    public void Dispose() => _repo.Dispose();
}
