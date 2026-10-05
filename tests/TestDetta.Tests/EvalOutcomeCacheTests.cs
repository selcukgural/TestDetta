using System.Text.Json;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// A second evaluation of a commit reuses what the tests did against each mutant and computes only
/// TestDetta's selection again; its results must equal those of the first evaluation, which ran every
/// test. Slow: builds, records and runs twice.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalOutcomeCacheTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testdetta-eval-cache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Second_evaluation_reuses_the_test_outcomes_and_gets_the_same_results()
    {
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
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using Xunit;
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calculator.Add(1, 2)); }
            public class NegTests { [Fact] public void Negates() => Assert.Equal(-2, Calculator.Neg(2)); }
            public class TwiceTests { [Fact] public void Doubles() => Assert.Equal(4, Calculator.Twice(2)); }
            """);
        _repo.Commit("base");

        var cache = Path.Combine(_root, "cache");
        var first = Evaluate(Path.Combine(_root, "first"), cache);
        var second = Evaluate(Path.Combine(_root, "second"), cache);

        Assert.NotEmpty(first);
        Assert.All(first, m => Assert.False(m.Reused));
        Assert.All(second, m => Assert.True(m.Reused));
        Assert.Equal(first.Select(static m => m.Result), second.Select(static m => m.Result));
        Assert.Contains(first, static m => m.Result.Contains("killed=", StringComparison.Ordinal) && !m.Result.EndsWith("killed=", StringComparison.Ordinal));
    }

    [Fact]
    public void Outcomes_recorded_while_a_test_project_was_left_out_are_not_reused_once_it_runs()
    {
        // Other.Tests crashes its test host while the marker file exists, so the first evaluation
        // leaves it out; the second evaluates it and must see its failures (Mapperly in round 2c/2d).
        var marker = Path.Combine(_root, "crash-marker");
        Directory.CreateDirectory(_root);
        File.WriteAllText(marker, "");
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc; public static class Calculator { public static int Add(int a, int b) => a + b; }");
        foreach (var name in new[] { "Calc.Tests", "Other.Tests" })
        {
            _repo.Write($"tests/{name}/{name}.csproj", """
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
        }

        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using Xunit;
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calculator.Add(1, 2)); }
            """);
        _repo.Write("tests/Other.Tests/Tests.cs", $$"""
            using Calc;
            using Xunit;
            namespace Other.Tests;
            public class SumTests { [Fact] public void Sums() => Assert.Equal(5, Calculator.Add(2, 3)); }
            public class CrashTests
            {
                [Fact]
                public void Crashes_while_the_marker_exists()
                {
                    if (System.IO.File.Exists(@"{{marker}}")) { System.Environment.FailFast("left out on purpose"); }
                }
            }
            """);
        _repo.Commit("base");

        var cache = Path.Combine(_root, "cache");
        var first = Evaluate(Path.Combine(_root, "first"), cache);
        File.Delete(marker);
        var second = Evaluate(Path.Combine(_root, "second"), cache);

        Assert.Contains(first, static m => m.Result.StartsWith("Calc.Calculator.Add", StringComparison.Ordinal) && !m.Result.Contains("Other.Tests", StringComparison.Ordinal));
        var add = second.Where(static m => m.Result.StartsWith("Calc.Calculator.Add", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(add);
        Assert.All(add, static m => Assert.False(m.Reused));
        Assert.Contains(add, static m => m.Result.Contains("Other.Tests: Other.Tests.SumTests", StringComparison.Ordinal));
    }

    /// <summary>Runs a synthetic evaluation and returns, per mutant, its result and whether it was reused.</summary>
    /// <param name="output">Output directory of this evaluation.</param>
    /// <param name="cache">Outcome cache directory shared by the evaluations.</param>
    private List<(string Result, bool Reused)> Evaluate(string output, string cache)
    {
        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "10", "--out", output, "--outcome-cache", cache]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "synthetic.json"))).RootElement;
        return [.. run.GetProperty("mutants").EnumerateArray().Select(static m => (
            $"{m.GetProperty("target").GetString()} methods={m.GetProperty("mutatedMethods").GetInt32()} " +
            $"missed={string.Join(",", m.GetProperty("missed").EnumerateArray().Select(static c => c.GetString()))} " +
            $"killed={string.Join(",", m.GetProperty("killed").EnumerateArray().Select(static c => c.GetString()))}",
            m.GetProperty("reused").GetBoolean()))];
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
