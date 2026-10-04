using System.Text.Json;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// Mutants switched on in a schema can run at once on one clone. The results must equal those of one
/// mutant at a time. Each worker runs in its own copy of the clone, so tests that claim a fixed file in
/// the repository do not meet; a suite whose parallel runs still interfere (a fixed file outside the
/// repository) must be evaluated one mutant at a time. Slow: builds, records and runs several times.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalParallelMutantsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testdetta-eval-parallel-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Mutants_run_at_once_give_the_results_of_one_at_a_time()
    {
        WriteRepository(SharedFile.None);

        var sequential = Evaluate(Path.Combine(_root, "one"), parallelism: 1);
        var parallel = Evaluate(Path.Combine(_root, "three"), parallelism: 3);

        Assert.Equal(1, sequential.Parallelism);
        Assert.Equal(3, parallel.Parallelism);
        Assert.NotEmpty(sequential.Results);
        Assert.Equal(sequential.Results, parallel.Results);
    }

    [Theory]
    [InlineData(SharedFile.InRepository)]
    [InlineData(SharedFile.InTemporaryDirectory)]
    public void Tests_claiming_a_fixed_file_run_apart_in_worker_copies(SharedFile sharedFile)
    {
        WriteRepository(sharedFile);

        var sequential = Evaluate(Path.Combine(_root, "one"), parallelism: 1);
        var parallel = Evaluate(Path.Combine(_root, "three"), parallelism: 3);

        Assert.Equal(3, parallel.Parallelism);
        Assert.Equal(sequential.Results, parallel.Results);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "three"), "*.worker-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Suite_whose_parallel_runs_interfere_runs_one_mutant_at_a_time()
    {
        WriteRepository(SharedFile.OutsideRepository);

        var run = Evaluate(Path.Combine(_root, "interfering"), parallelism: 3);

        Assert.Equal(1, run.Parallelism);
        Assert.Contains(run.Results, static r => r.Contains("killed=Calc.Tests: Calc.Tests.AddTests", StringComparison.Ordinal));
    }

    /// <summary>Where a test class holds a fixed file exclusively for a while, as some suites do.</summary>
    public enum SharedFile
    {
        /// <summary>No such class.</summary>
        None,

        /// <summary>Next to the test binaries, inside the repository.</summary>
        InRepository,

        /// <summary>In the system's temporary directory, which each worker gets its own of.</summary>
        InTemporaryDirectory,

        /// <summary>In a fixed directory outside the repository, which every worker shares.</summary>
        OutsideRepository,
    }

    /// <summary>A small repository with three members and a test class each, and maybe a class holding a fixed file.</summary>
    /// <param name="sharedFile">Where that class's file is.</param>
    private void WriteRepository(SharedFile sharedFile)
    {
        var lockDirectory = sharedFile switch
        {
            SharedFile.OutsideRepository => $"\"{_root.Replace("\\", "\\\\", StringComparison.Ordinal)}\"",
            SharedFile.InTemporaryDirectory => "System.IO.Path.GetTempPath()",
            _ => "System.AppContext.BaseDirectory",
        };
        Directory.CreateDirectory(_root);
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
                public static int Neg(int a) => -a;
                public static int Twice(int a) => Add(a, a);
            }
            """);
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        var claim = sharedFile != SharedFile.None
            ? $$"""
              public class ClaimTests
              {
                  [Fact]
                  public void Holds_the_lock_file()
                  {
                      using var file = new System.IO.FileStream(System.IO.Path.Combine({{lockDirectory}}, "suite.lock"), System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None, 1, System.IO.FileOptions.DeleteOnClose);
                      System.Threading.Thread.Sleep(3000);
                  }
              }
              """
            : string.Empty;
        _repo.Write("tests/Calc.Tests/Tests.cs", $$"""
            using Xunit;
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calculator.Add(1, 2)); }
            public class NegTests { [Fact] public void Negates() => Assert.Equal(-2, Calculator.Neg(2)); }
            public class TwiceTests { [Fact] public void Doubles() => Assert.Equal(4, Calculator.Twice(2)); }
            {{claim}}
            """);
        _repo.Commit("base");
    }

    /// <summary>Runs a synthetic evaluation and returns the parallelism it used and, per mutant, its result.</summary>
    private (int Parallelism, List<string> Results) Evaluate(string output, int parallelism)
    {
        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "10", "--out", output,
                "--mutant-parallelism", parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        Assert.True(run.GetProperty("schema").GetBoolean());
        List<string> results = [.. run.GetProperty("mutants").EnumerateArray().Select(static m =>
            $"{m.GetProperty("target").GetString()} methods={m.GetProperty("mutatedMethods").GetInt32()} " +
            $"missed={string.Join(",", m.GetProperty("missed").EnumerateArray().Select(static c => c.GetString()))} " +
            $"killed={string.Join(",", m.GetProperty("killed").EnumerateArray().Select(static c => c.GetString()))}")];
        return (run.GetProperty("mutantParallelism").GetInt32(), results);
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
