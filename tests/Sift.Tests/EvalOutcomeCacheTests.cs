using System.Text.Json;
using Sift.Eval;

namespace Sift.Tests;

/// <summary>
/// A second evaluation of a commit reuses what the tests did against each mutant and computes only
/// Sift's selection again; its results must equal those of the first evaluation, which ran every
/// test. Slow: builds, records and runs twice.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalOutcomeCacheTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sift-eval-cache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Second_evaluation_reuses_the_test_outcomes_and_gets_the_same_results()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
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
