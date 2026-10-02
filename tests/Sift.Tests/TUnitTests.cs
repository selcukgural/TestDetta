using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// TUnit ignores the VSTest <c>--filter</c> and then runs no test at all, and runs none either when
/// given a run settings file; recording and selected runs must use its tree-node filter and its own
/// parallelism option.
/// </summary>
public sealed class TUnitTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Tree_node_filter_names_classes_without_their_namespace()
    {
        Assert.Equal("/*/*/(OrderTests)|(Outer+CartTests)/**", TestFilter.TreeNodeFilter(["App.Tests.OrderTests", "App.Tests.Outer+CartTests"], exclude: false));
        Assert.Equal("/*/*/(!OrderTests)&(!Odd\\(1\\))/**", TestFilter.TreeNodeFilter(["App.OrderTests", "Odd(1)"], exclude: true));
    }

    [Fact]
    public void Selected_runs_of_a_tunit_project_use_its_tree_node_filter()
    {
        var project = new ProjectNode("/repo/tests/A.Tests/A.Tests.csproj", "/repo/tests/A.Tests", true, [])
        {
            PackageReferences = new HashSet<string>(["TUnit"], StringComparer.OrdinalIgnoreCase),
        };
        TestProjectSelection[] selections = [new(project, TestRunMode.Filtered, ["A.Tests.OrderTests"], "")];

        var invocation = TestInvocationBuilder.Build(selections, "/repo", [], DotnetTestMode.TestingPlatform).Single();

        Assert.Equal(["test", "--project", "tests/A.Tests/A.Tests.csproj", "--treenode-filter", "/*/*/(OrderTests)/**"], invocation.Arguments);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Tunit_project_is_recorded_in_shards_and_its_selection_runs_only_the_selected_class()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc; public static class Calculator { public static int Add(int a, int b) => a + b; public static int Mul(int a, int b) => a * b; }");
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup><PackageReference Include="TUnit" Version="1.70.1" /><ProjectReference Include="../../src/Calc/Calc.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using System.Threading.Tasks;
            using TUnit.Core;
            namespace Calc.Tests;
            public class AddTests { [Test] public async Task Adds() => await Assert.That(Calculator.Add(1, 2)).IsEqualTo(3); }
            public class MulTests { [Test] public async Task Muls() => await Assert.That(Calculator.Mul(2, 3)).IsEqualTo(6); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var recorder = new CoverageRecorder(NullLoggerFactory.Instance);
        // The first recording learns the classes; the second spreads them over two filtered processes
        // plus the process for everything else.
        var (first, _) = recorder.Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 2)), TextWriter.Null);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), first);
        using var output = new StringWriter();
        var (map, passed) = recorder.Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 2)), output);

        Assert.True(passed, output.ToString());
        Assert.Equal(["Calc.Tests.AddTests", "Calc.Tests.MulTests"], map.Classes.Keys.Order(StringComparer.Ordinal));
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        _repo.Replace("src/Calc/Calculator.cs", "=> a * b;", "=> b * a;");
        var analysis = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main");
        var invocation = TestInvocationBuilder.Build(analysis.Tests, analysis.RepoRoot, ["--no-build"], DotnetTestMode.TestingPlatform).Single();
        Assert.Contains("--treenode-filter", invocation.Arguments);

        using var run = new StringWriter();
        var exitCode = new DotnetProcess(NullLogger.Instance).Run(invocation.Arguments, analysis.RepoRoot, new Dictionary<string, string>(), run);
        Assert.True(exitCode == 0, string.Join(" ", invocation.Arguments) + "\n" + run);
        Assert.Contains("total: 1", run.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();
}
