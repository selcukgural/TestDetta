using System.Text;
using System.Text.Json;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// A miss must be a class that fails because of the mutant. Slow: builds, records and runs a small
/// repository.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalConfirmTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "testdetta-eval-confirm-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Class_that_fails_alone_without_the_mutant_too_is_no_miss_and_sources_keep_their_bytes()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\nTestResults/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        // With a byte order mark, like NLog's sources: restoring them must not drop it.
        _repo.WriteBytes("src/Calc/Setup.cs", [.. Encoding.UTF8.GetPreamble(), .. """
            namespace Calc;
            public static class Setup { public static int Prepare() => 1; }
            """u8]);
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MSTest" Version="4.4.1" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        // LateTests passes only after EarlyTests ran (MSTest runs classes in declaration order): alone,
        // with or without the mutant, it fails. TestDetta rightly leaves it out for a change to Prepare.
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            namespace Calc.Tests;
            public static class Shared { public static bool Ready; }
            [TestClass] public class EarlyTests { [TestMethod] public void Prepares() { Assert.AreEqual(1, Calc.Setup.Prepare()); Shared.Ready = true; } }
            [TestClass] public class LateTests { [TestMethod] public void Runs_after_setup() => Assert.IsTrue(Shared.Ready); }
            """);
        _repo.Commit("base");

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            // The clone is looked at afterwards: kept although nothing was missed.
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "5", "--out", _output, "--keep-clones"]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        var mutant = Assert.Single(run.GetProperty("mutants").EnumerateArray());
        Assert.Equal("Calc.Setup.Prepare", mutant.GetProperty("target").GetString());
        Assert.Equal(["Calc.Tests: Calc.Tests.EarlyTests"], mutant.GetProperty("killed").EnumerateArray().Select(static c => c.GetString()));
        Assert.Empty(mutant.GetProperty("missed").EnumerateArray());

        var source = File.ReadAllBytes(Path.Combine(_output, "repo", "src", "Calc", "Setup.cs"));
        Assert.True(source.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), "the byte order mark was dropped");
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
