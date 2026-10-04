using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>dotnet-tia's <c>analyze --json</c> report read as decisions per test project (docs/design/tia-comparison.md).</summary>
public sealed class TiaSelectorTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public TiaSelectorTests()
    {
        foreach (var name in (string[])["A.Tests", "B.Tests", "C.Tests"])
        {
            _repo.Write($"tests/{name}/{name}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>
                """);
        }

        _repo.Commit("tests");
    }

    [Fact]
    public void Selective_report_runs_listed_classes_unfiltered_projects_and_nothing_else()
    {
        var a = Path.Combine(_repo.Root, "tests/A.Tests/A.Tests.csproj");
        var b = Path.Combine(_repo.Root, "tests/B.Tests/B.Tests.csproj");
        var json = $$"""
            {
              "mode": "selective",
              "elapsedSeconds": 3.5,
              "projects": [
                { "projectPath": "{{a}}", "filtered": true, "tests": ["A.Tests.OrderTests.Adds", "A.Tests.OrderTests.Removes", "A.Tests.Outer+Inner.Works"] },
                { "projectPath": "{{b}}", "filtered": false, "tests": ["B.Tests.X.Y"] }
              ]
            }
            """;

        var result = TiaSelector.FromReport(json, Graph());

        Assert.False(result.FullRun);
        Assert.Equal(3.5, result.Seconds);
        var byProject = result.Tests.ToDictionary(static t => Path.GetFileNameWithoutExtension(t.Project.Path));
        Assert.Equal(TestRunMode.Filtered, byProject["A.Tests"].Mode);
        Assert.Equal(["A.Tests.OrderTests", "A.Tests.Outer+Inner"], byProject["A.Tests"].TestClasses);
        Assert.Equal(TestRunMode.All, byProject["B.Tests"].Mode);
        Assert.Equal(TestRunMode.Skipped, byProject["C.Tests"].Mode);
    }

    [Fact]
    public void Full_run_runs_every_test_project_and_keeps_the_reason()
    {
        var result = TiaSelector.FromReport("""{ "mode": "full", "fullRunReasons": ["Directory.Build.props changed"], "projects": [] }""", Graph());

        Assert.True(result.FullRun);
        Assert.Equal("Directory.Build.props changed", result.Reason);
        Assert.All(result.Tests, static t => Assert.Equal(TestRunMode.All, t.Mode));
        Assert.Equal(3, result.Tests.Count);
    }

    [Fact]
    public void Comparison_is_switched_on_for_one_repository_or_a_corpus_run()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--compare-tia", "/tools/tia/Tia.Cli.dll", "--tia-solution", "All.sln"], out var options, out _));
        Assert.Equal("/tools/tia/Tia.Cli.dll", options.Run.TiaCli);
        Assert.Equal("All.sln", options.Run.TiaSolution);

        var entry = new CorpusEntry { Name = "x", Url = "https://example.org/x.git", Synthetic = 10, TiaSolution = "src/X.sln" };
        Assert.True(BatchOptions.TryParse(["--corpus", "c.json", "--compare-tia", "/tools/tia/Tia.Cli.dll"], out var batch, out _));
        var run = BatchRunner.SettingsFor(entry, batch).Run;
        Assert.Equal(("/tools/tia/Tia.Cli.dll", "src/X.sln"), (run.TiaCli, run.TiaSolution));
    }

    [Fact]
    public void Affected_report_runs_listed_test_projects_whole_and_nothing_else()
    {
        var a = Path.Combine(_repo.Root, "tests/A.Tests/A.Tests.csproj");
        var json = $$"""[ { "Name": "Lib", "FilePath": "{{Path.Combine(_repo.Root, "src/Lib/Lib.csproj")}}" }, { "Name": "A.Tests", "FilePath": "{{a}}" } ]""";

        var byProject = AffectedSelector.FromReport(json, Graph()).ToDictionary(static t => Path.GetFileNameWithoutExtension(t.Project.Path));

        Assert.Equal(TestRunMode.All, byProject["A.Tests"].Mode);
        Assert.Equal(TestRunMode.Skipped, byProject["B.Tests"].Mode);
        Assert.Equal(TestRunMode.Skipped, byProject["C.Tests"].Mode);
        Assert.All(AffectedSelector.FromReport("[]", Graph()), static t => Assert.Equal(TestRunMode.Skipped, t.Mode));

        Assert.True(EvalOptions.TryParse(["--repo", "r", "--compare-affected", "/tools/dotnet-affected"], out var options, out _));
        Assert.Equal("/tools/dotnet-affected", options.Run.AffectedCli);
    }

    private ProjectGraph Graph() => new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

    public void Dispose() => _repo.Dispose();
}
