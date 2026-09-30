using System.Text.Json;
using Sift.Eval;

namespace Sift.Tests;

/// <summary>
/// The evaluation tool must itself find missed tests, or its "nothing missed" means nothing. It is
/// checked on a change Sift handles and on one Sift is known to miss. Slow: builds, records and runs.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalToolTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "sift-eval-test-" + Guid.NewGuid().ToString("N"));

    public EvalToolTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <!-- A source file shared from outside the project directory. -->
              <ItemGroup><Compile Include="../Shared/Formatter.cs" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public class Calculator { public int Add(int a, int b) => a + b; public int Mul(int a, int b) => a * b; }
            """);
        _repo.Write("src/Shared/Formatter.cs", """
            namespace Calc;
            public static class Formatter { public static string Format(int value) => value.ToString(); }
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
            public class MulTests { [Fact] public void Muls() => Assert.Equal(6, new Calculator().Mul(2, 3)); }
            public class FormatTests { [Fact] public void Formats() => Assert.Equal("7", Formatter.Format(7)); }

            // Reaches a type only through its name in a string: no reference, base list or attribute to trace.
            public class PluginTests
            {
                [Fact]
                public void Runs_the_plugin() =>
                    Assert.Equal(42, (int)System.Type.GetType("Calc.Plugin, Calc")!.GetMethod("Run")!.Invoke(null, null)!);
            }
            """);
        _repo.Write("src/Calc/Plugin.cs", """
            namespace Calc;
            public static class Plugin { public static int Run() => 42; }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Change_sift_handles_is_reported_without_misses()
    {
        _repo.Replace("src/Calc/Calculator.cs", "a * b;", "checked(a * b);");
        _repo.Commit("checked multiply");

        var (exitCode, merge) = Evaluate();

        Assert.Equal(0, exitCode);
        Assert.Contains("Calc.Tests.MulTests", merge.GetProperty("mutants")[0].GetProperty("killed").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(merge.GetProperty("mutants")[0].GetProperty("missed").EnumerateArray());
        Assert.Empty(merge.GetProperty("coverageMisses").EnumerateArray());
    }

    [Fact]
    public void Missed_test_class_is_found_by_mutants_and_by_coverage()
    {
        // Plugin is named by nothing and has no base list or attributes, so Sift treats it as unused;
        // PluginTests finds it by a string. Sift is blind here by design; the evaluation must not be.
        _repo.Replace("src/Calc/Plugin.cs", "=> 42;", "=> 40 + 2;");
        _repo.Commit("compute the answer");

        var (exitCode, merge) = Evaluate();

        Assert.Equal(3, exitCode);
        Assert.Contains("Calc.Tests.PluginTests", merge.GetProperty("mutants")[0].GetProperty("missed").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(merge.GetProperty("coverageMisses").EnumerateArray(), e => e.GetString()!.EndsWith("Calc.Tests.PluginTests", StringComparison.Ordinal));
    }

    [Fact]
    public void Change_to_a_linked_source_file_is_selected()
    {
        _repo.Replace("src/Shared/Formatter.cs", "value.ToString()", "value.ToString(System.Globalization.CultureInfo.InvariantCulture)");
        _repo.Commit("invariant format");

        var (exitCode, merge) = Evaluate();

        Assert.Equal(0, exitCode);
        Assert.Contains("Calc.Tests.FormatTests", merge.GetProperty("mutants")[0].GetProperty("killed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Synthetic_changes_find_the_same_miss_and_nothing_else()
    {
        var stdout = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(TextWriter.Null);
            exitCode = EvalApp.Run(["--repo", _repo.Root, "--synthetic", "10", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        var mutants = run.GetProperty("mutants").EnumerateArray().ToDictionary(m => m.GetProperty("target").GetString()!);

        Assert.Equal(3, exitCode);
        Assert.Equal(["Calc.Calculator.Add", "Calc.Calculator.Mul", "Calc.Formatter.Format", "Calc.Plugin.Run"], mutants.Keys.Order(StringComparer.Ordinal));
        Assert.All(mutants.Values, m => Assert.True(m.GetProperty("detected").GetBoolean()));
        Assert.Equal(["Calc.Tests: Calc.Tests.PluginTests"], Strings(mutants["Calc.Plugin.Run"], "missed"));
        Assert.Equal(["Calc.Tests: Calc.Tests.MulTests"], Strings(mutants["Calc.Calculator.Mul"], "killed"));
        Assert.Empty(Strings(mutants["Calc.Calculator.Mul"], "missed"));
        Assert.Empty(Strings(mutants["Calc.Formatter.Format"], "missed"));
    }

    /// <summary>A string array property of a JSON object.</summary>
    private static string[] Strings(JsonElement element, string property) =>
        [.. element.GetProperty(property).EnumerateArray().Select(static e => e.GetString()!)];

    /// <summary>Evaluates the latest commit and returns the exit code and its JSON entry.</summary>
    private (int ExitCode, JsonElement Merge) Evaluate()
    {
        var stdout = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(TextWriter.Null);
            exitCode = EvalApp.Run(["--repo", _repo.Root, "--merges", "1", "--mutants", "2", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "results.json")));
        var merge = results.RootElement.GetProperty("merges")[0];
        Assert.True(merge.GetProperty("error").ValueKind == JsonValueKind.Null, merge.GetProperty("error").ToString());
        return (exitCode, merge);
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_output))
        {
            if (Environment.GetEnvironmentVariable("SIFT_KEEP_EVAL") is null)
            {
                Directory.Delete(_output, recursive: true);
            }
        }
    }
}
