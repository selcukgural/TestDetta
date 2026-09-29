using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// Records coverage for a real xUnit project: build, IL instrumentation, the in-process collector
/// in the test host, and restoring the original assemblies. Slow (it runs dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class CoverageRecorderTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public CoverageRecorderTests()
    {
        // The fixture lives outside this repository, so it pins the SDK and package versions itself.
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public async Task<int> MultiplyAsync(int a, int b) { await Task.Yield(); return Enumerable.Repeat(a, b).Sum(x => x); }
                public int Unused() => 0;
            }
            """);
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using Xunit;
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, new Calculator().Add(1, 2)); }
            public class MultiplyTests { [Fact] public async Task Multiplies() => Assert.Equal(6, await new Calculator().MultiplyAsync(2, 3)); }

            // The fixture's constructor runs before the class is created; its hits belong to the fixture.
            // Its background worker keeps the fixture's execution context, but works for the running test.
            public class WarmCalculator
            {
                private readonly System.Collections.Concurrent.BlockingCollection<(int, int, TaskCompletionSource<int>)> _work = new();

                public WarmCalculator()
                {
                    new Calculator().Unused();
                    new Thread(() => { foreach (var (a, b, done) in _work.GetConsumingEnumerable()) { done.SetResult(new Calculator().Add(a, b)); } }) { IsBackground = true }.Start();
                }

                public int AddInBackground(int a, int b)
                {
                    var done = new TaskCompletionSource<int>();
                    _work.Add((a, b, done));
                    return done.Task.GetAwaiter().GetResult();
                }
            }

            public class FixtureTests(WarmCalculator warm) : IClassFixture<WarmCalculator>
            {
                [Fact] public void Adds_through_the_fixture_worker() => Assert.Equal(5, warm.AddInBackground(2, 3));
            }

            // Like a test server handling a request: the work runs without the test's execution context.
            public class DetachedTests
            {
                [Fact]
                public void Adds_on_a_detached_thread()
                {
                    int result;
                    using (ExecutionContext.SuppressFlow())
                    {
                        var task = Task.Run(() => new Calculator().Add(2, 2));
                        result = task.GetAwaiter().GetResult();
                    }

                    Assert.Equal(4, result);
                }
            }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Records_which_class_ran_which_member_and_restores_the_assemblies()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();

        CoverageMap map;
        bool passed;
        try
        {
            (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
                .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
        }

        Assert.True(passed, output.ToString());
        Assert.Equal(
            ["Calc.Tests.AddTests", "Calc.Tests.DetachedTests", "Calc.Tests.FixtureTests", "Calc.Tests.MultiplyTests"],
            map.Classes.Keys.Order());

        var add = Ran(map, "Calc.Tests.AddTests");
        var multiply = Ran(map, "Calc.Tests.MultiplyTests");
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Add"), add);
        Assert.DoesNotContain(new CoveredMethod("Calc.Calculator", "MultiplyAsync"), add);
        Assert.Contains(new CoveredMethod("Calc.Calculator", "MultiplyAsync"), multiply);
        Assert.DoesNotContain(new CoveredMethod("Calc.Calculator", "Add"), multiply);
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Unused"), map.Methods);

        // Fixture setup is kept apart from every test class.
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Unused"), map.Fixtures["Calc.Tests.WarmCalculator"].Select(id => map.Methods[id]));
        Assert.All(map.Classes.Keys, c => Assert.DoesNotContain(new CoveredMethod("Calc.Calculator", "Unused"), Ran(map, c)));

        // Work on a detached thread still counts for the class that was running, and only for it.
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Add"), Ran(map, "Calc.Tests.DetachedTests"));

        // The fixture's worker thread runs Add for FixtureTests: the class gets it, the fixture setup does not.
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Add"), Ran(map, "Calc.Tests.FixtureTests"));
        Assert.DoesNotContain(new CoveredMethod("Calc.Calculator", "Add"), map.Fixtures["Calc.Tests.WarmCalculator"].Select(id => map.Methods[id]));

        // Every class and fixture has a recorded time.
        Assert.Equal(map.Classes.Keys.Order(), map.Timings!.ClassSeconds.Keys.Order());
        Assert.All(map.Timings.ClassSeconds.Values, seconds => Assert.True(seconds >= 0));
        Assert.True(map.Timings.ClassSeconds.Values.Sum() > 0);
        Assert.True(map.Timings.FixtureSeconds["Calc.Tests.WarmCalculator"] > 0);
        Assert.True(map.Timings.StartupSeconds > 0);

        // A plain run after recording must not find any instrumentation left behind.
        var library = Path.Combine(_repo.Root, "tests/Calc.Tests/bin/Debug/net10.0/Calc.dll");
        Assert.DoesNotContain("Sift.Coverage.Runtime", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(library)));
    }

    [Fact]
    public void Recording_in_several_processes_gives_the_same_map()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var recorder = new CoverageRecorder(NullLoggerFactory.Instance);

        var (sequential, _) = recorder.Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), TextWriter.Null);
        var (parallel, passed) = recorder.Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 3)), TextWriter.Null);

        Assert.True(passed);
        Assert.Equal(sequential.Classes.Keys.Order(), parallel.Classes.Keys.Order());
        foreach (var testClass in sequential.Classes.Keys)
        {
            Assert.Equal(Ran(sequential, testClass), Ran(parallel, testClass));
        }

        Assert.Equal(sequential.Fixtures.Keys.Order(), parallel.Fixtures.Keys.Order());
    }

    private static HashSet<CoveredMethod> Ran(CoverageMap map, string testClass) =>
        [.. map.Classes[testClass].Select(id => map.Methods[id])];

    public void Dispose() => _repo.Dispose();
}
