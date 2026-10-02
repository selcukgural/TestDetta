using System.Text.Json;
using Sift.Eval;

namespace Sift.Tests;

/// <summary>
/// Recorded coverage maps are reused between evaluations of a commit while Sift's recording code and
/// the recording settings are unchanged, and only then. Slow: builds, records and runs twice.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalRecordingCacheTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sift-eval-recording-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Second_evaluation_reuses_the_recording()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
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

        var cache = Path.Combine(_root, "cache");
        var first = RecordedAt(Evaluate(Path.Combine(_root, "first"), cache));
        var second = RecordedAt(Evaluate(Path.Combine(_root, "second"), cache));

        // A new recording would carry a new time; the reused one carries the first's.
        Assert.Equal(first, second);
        Assert.Single(Directory.GetDirectories(cache));
    }

    [Fact]
    public void Recording_key_changes_with_the_settings_that_shape_a_recording()
    {
        var settings = new RunSettings(Parallelism: 4, Mutants: 2, Projects: [], new RepoCommands(Setup: null, Cleanup: null), Framework: "net10.0");

        Assert.Equal(EvalSteps.RecordingKey(settings), EvalSteps.RecordingKey(settings with { Mutants = 5, MutantParallelism = 4 }));
        Assert.NotEqual(EvalSteps.RecordingKey(settings), EvalSteps.RecordingKey(settings with { Parallelism = 1 }));
        Assert.NotEqual(EvalSteps.RecordingKey(settings), EvalSteps.RecordingKey(settings with { Framework = "net8.0" }));
        Assert.NotEqual(EvalSteps.RecordingKey(settings), EvalSteps.RecordingKey(settings with { Excluded = ["tests/Slow"] }));
        Assert.NotEqual(EvalSteps.RecordingKey(settings), EvalSteps.RecordingKey(settings with { Commands = new RepoCommands("mkdir -p x", null) }));
    }

    /// <summary>Runs a synthetic evaluation and returns its output directory.</summary>
    private string Evaluate(string output, string cache)
    {
        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "2", "--out", output, "--recording-cache", cache]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        return output;
    }

    /// <summary>The recording time of the evaluation's map.</summary>
    private static string RecordedAt(string output)
    {
        var map = Directory.GetFiles(Path.Combine(output, "maps"), "*.json", SearchOption.AllDirectories).Single();
        return JsonDocument.Parse(File.ReadAllText(map)).RootElement.GetProperty("recordedAt").GetString()!;
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
