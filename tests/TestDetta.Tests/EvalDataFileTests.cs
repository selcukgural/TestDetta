using System.Text.Json;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// The evaluation empties data files outside every project and checks that the tests reading them are
/// selected (docs/design/runtime-file-reads.md). Slow: builds, records and runs a small repository.
/// </summary>
[Trait("Category", "Slow")]
[Trait("Category", "Eval")]
public sealed class EvalDataFileTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "testdetta-eval-data-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_emptied_data_file_fails_its_readers_and_TestDetta_selects_them()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\nTestResults/\n");
        _repo.Write("src/Rates/Rates.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Rates/RateTable.cs", """
            namespace Rates;
            public static class RateTable
            {
                public static string Load(string path) => File.ReadAllText(path).Trim();
                public static int Add(int a, int b) => a + b;
            }
            """);
        _repo.Write("testdata/rates.txt", "42\n");
        _repo.Write("tests/Rates.Tests/Rates.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MSTest" Version="4.4.1" />
                <ProjectReference Include="../../src/Rates/Rates.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Rates.Tests/Tests.cs", """
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            namespace Rates.Tests;

            [TestClass]
            public class ReaderTests
            {
                [TestMethod]
                public void Reads()
                {
                    var root = AppContext.BaseDirectory;
                    while (!Directory.Exists(Path.Combine(root, "testdata"))) root = Path.GetDirectoryName(root)!;
                    Assert.AreEqual("42", RateTable.Load(Path.Combine(root, "testdata", "rates.txt")));
                }
            }

            [TestClass] public class AddTests { [TestMethod] public void Adds() => Assert.AreEqual(3, RateTable.Add(1, 2)); }
            """);
        _repo.Commit("base");

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "1", "--seed", "1", "--data-files", "1", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        var file = Assert.Single(run.GetProperty("mutants").EnumerateArray(), static m => m.GetProperty("kind").GetString() == "empty-file");
        Assert.Equal("(file).testdata/rates.txt", file.GetProperty("target").GetString());
        Assert.Equal(["Rates.Tests: Rates.Tests.ReaderTests"], file.GetProperty("killed").EnumerateArray().Select(static c => c.GetString()));
        Assert.Empty(file.GetProperty("missed").EnumerateArray());
        Assert.True(file.GetProperty("selectedShare").GetDouble() < 1, "only the reader runs, not the whole project");

        // The file is put back once the evaluation ends.
        Assert.Equal("42\n", File.ReadAllText(Path.Combine(_repo.Root, "testdata", "rates.txt")));
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
