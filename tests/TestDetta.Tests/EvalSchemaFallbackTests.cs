using System.Text.Json;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// The switches of a schema add code to every sampled method. A suite that notices (here a test that
/// checks a method's IL size) fails differently with them; the evaluation must then fall back to
/// writing each mutant in alone and still produce its results. Slow: builds, records and runs.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalSchemaFallbackTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "testdetta-eval-fallback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Suite_that_notices_the_switches_is_evaluated_one_mutant_at_a_time()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public static class Calculator { public static int Add(int a, int b) => a + b; public static int Neg(int a) => -a; }
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
            // Notices any code added to Neg, as a guard does.
            public class ShapeTests
            {
                [Fact]
                public void Neg_is_tiny() => Assert.True(typeof(Calculator).GetMethod("Neg")!.GetMethodBody()!.GetILAsByteArray()!.Length < 8);
            }
            """);
        _repo.Commit("base");

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "5", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        Assert.False(run.GetProperty("schema").GetBoolean());
        var mutants = run.GetProperty("mutants").EnumerateArray().ToDictionary(static m => m.GetProperty("target").GetString()!);
        Assert.Contains("Calc.Tests: Calc.Tests.AddTests", mutants["Calc.Calculator.Add"].GetProperty("killed").EnumerateArray().Select(static c => c.GetString()));
        Assert.Contains("Calc.Tests: Calc.Tests.NegTests", mutants["Calc.Calculator.Neg"].GetProperty("killed").EnumerateArray().Select(static c => c.GetString()));
    }

    [Fact]
    public void Analysis_that_does_not_finish_in_time_is_reported_before_any_binary_is_touched()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc; public static class Calculator { public static int Add(int a, int b) => a + b; }");
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
        _repo.Write("tests/Calc.Tests/Tests.cs", "using Xunit; namespace Calc.Tests; public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calculator.Add(1, 2)); }");
        _repo.Commit("base");

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            // No analysis finishes in a millisecond: it stands for a hang in TestDetta.
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "2", "--out", _output, "--analysis-timeout", "00:00:00.001"]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.Contains("did not finish within", run.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("left running", run.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_output, "schema-backup")));

        // The analysis was stopped and waited for before the evaluation returned: the clone is back as it was, and
        // nothing writes into it while the folder is deleted below.
        var clone = Path.Combine(_output, "repo");
        Assert.False(Directory.Exists(Path.Combine(clone, ".testdetta")));
        Assert.Equal(
            "namespace Calc; public static class Calculator { public static int Add(int a, int b) => a + b; }",
            File.ReadAllText(Path.Combine(clone, "src", "Calc", "Calculator.cs")));
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, recursive: true);
        }
    }
}
