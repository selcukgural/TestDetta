using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// A mutant can make a test wait forever; the evaluation must kill the run and go on. Slow: builds,
/// records and runs a small repository.
/// </summary>
[Trait("Category", "Slow")]
[Trait("Category", "Eval")]
public sealed class EvalHangTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "testdetta-eval-hang-" + Guid.NewGuid().ToString("N"));
    private readonly TimeSpan _minimumTimeout = TestExecutor.MinimumTimeout;

    [Fact]
    public void Hanging_mutant_run_is_killed_and_counts_as_caught_when_the_hang_is_in_the_selection()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Signals/Signals.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Signals/Waiter.cs", """
            namespace Signals;
            public static class Waiter { public static void Signal(TaskCompletionSource done) => done.SetResult(); }
            """);
        _repo.Write("tests/Signals.Tests/Signals.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Signals/Signals.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Signals.Tests/Tests.cs", """
            using Xunit;
            namespace Signals.Tests;
            public class WaiterTests
            {
                // When Signal throws, the exception is swallowed and the wait never ends.
                [Fact]
                public void Signals()
                {
                    var done = new TaskCompletionSource();
                    _ = Task.Run(() => { try { Waiter.Signal(done); } catch (InvalidOperationException) { } });
                    done.Task.Wait();
                }
            }
            """);
        _repo.Commit("base");
        TestExecutor.MinimumTimeout = TimeSpan.FromSeconds(10);

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "5", "--seed", "1", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        var mutant = Assert.Single(run.GetProperty("mutants").EnumerateArray());
        Assert.Equal("Signals.Waiter.Signal", mutant.GetProperty("target").GetString());
        // WaiterTests hangs and TestDetta selected it; everything left out passes, so the mutant was caught.
        Assert.False(mutant.GetProperty("inconclusive").GetBoolean());
        Assert.True(mutant.GetProperty("caughtByCrash").GetBoolean());
    }

    [Fact]
    public void Recording_that_hangs_is_killed_and_reported()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("tests/Waits.Tests/Waits.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        // Stands for a test waiting on a database server that is not there.
        _repo.Write("tests/Waits.Tests/Tests.cs", """
            using Xunit;
            namespace Waits.Tests;
            public class WaitTests { [Fact] public void Waits() => Thread.Sleep(Timeout.Infinite); }
            """);
        _repo.Commit("base");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var project = graph.Projects.Single(p => p.IsTestProject);

        Assert.Throws<RecordingTimedOutException>(() =>
            new CoverageRecorder(NullLoggerFactory.Instance).Record(
                new RecordRequest(_repo.Root, graph, project, new RecordOptions(Timeout: TimeSpan.FromSeconds(10))),
                TextWriter.Null));
    }

    public void Dispose()
    {
        TestExecutor.MinimumTimeout = _minimumTimeout;
        _repo.Dispose();
        if (Directory.Exists(_output))
        {
            Paths.DeleteTree(_output);
        }
    }
}
