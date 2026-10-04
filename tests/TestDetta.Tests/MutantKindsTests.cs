using System.Text.Json;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// The synthetic evaluation's other mutant kinds: a changed return value, an inverted branch and a
/// changed field value, which coverage cannot see being read. Slow: builds, records and runs.
/// </summary>
[Trait("Category", "Slow")]
public sealed class MutantKindsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "testdetta-eval-kinds-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("return-default")]
    [InlineData("flip-branch")]
    [InlineData("field-value")]
    [InlineData("all")]
    public void Each_kind_is_applied_and_nothing_is_missed(string kinds)
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Checker.cs", """
            namespace Calc;
            public static class Limits { public static readonly int Max = 3; }
            public class Checker
            {
                public bool IsSmall(int x)
                {
                    if (x < Limits.Max)
                    {
                        return true;
                    }

                    return false;
                }

                public int Twice(int x) => x * 2;
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
            public class LimitTests { [Fact] public void Max_is_three() => Assert.Equal(3, Limits.Max); }
            public class CheckerTests
            {
                [Fact] public void Small() => Assert.True(new Checker().IsSmall(1));
                [Fact] public void Large() => Assert.False(new Checker().IsSmall(5));
                [Fact] public void Doubles() => Assert.Equal(4, new Checker().Twice(2));
            }
            """);
        _repo.Commit("base");

        var stdout = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(TextWriter.Null);
            exitCode = EvalApp.Run(["--repo", _repo.Root, "--synthetic", "10", "--mutant-kinds", kinds, "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        var mutants = run.GetProperty("mutants").EnumerateArray().ToList();

        Assert.Equal(0, exitCode);
        // Every kind runs switched on in a schema; the guards did not change the suite.
        Assert.True(run.GetProperty("schema").GetBoolean());
        Assert.All(mutants, m => Assert.Empty(m.GetProperty("missed").EnumerateArray()));
        var byTarget = mutants.ToDictionary(m => m.GetProperty("target").GetString()!);
        switch (kinds)
        {
            case "return-default":
                // Twice returns 0 and IsSmall false: both fail a test.
                Assert.True(Killing(byTarget["Calc.Checker.Twice"]));
                Assert.True(Killing(byTarget["Calc.Checker.IsSmall"]));
                break;

            case "flip-branch":
                // IsSmall has a branch to invert; Twice has none, so nothing is changed there.
                Assert.True(Killing(byTarget["Calc.Checker.IsSmall"]));
                Assert.Equal(0, byTarget["Calc.Checker.Twice"].GetProperty("mutatedMethods").GetInt32());
                break;

            case "field-value":
                var field = Assert.Single(mutants);
                Assert.Equal("Calc.Limits.Max", field.GetProperty("target").GetString());
                Assert.Contains("Calc.Tests: Calc.Tests.LimitTests", field.GetProperty("killed").EnumerateArray().Select(e => e.GetString()));
                break;

            default:
                Assert.Contains(mutants, m => m.GetProperty("kind").GetString() == "field-value");
                break;
        }
    }

    /// <summary>True when the mutant was applied and failed at least one test class.</summary>
    private static bool Killing(JsonElement mutant) =>
        mutant.GetProperty("mutatedMethods").GetInt32() > 0 && mutant.GetProperty("killed").GetArrayLength() > 0;

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_output) && Environment.GetEnvironmentVariable("TESTDETTA_KEEP_EVAL") is null)
        {
            Directory.Delete(_output, recursive: true);
        }
    }
}
