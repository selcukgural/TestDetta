using System.Text.Json;
using Sift.Eval;

namespace Sift.Tests;

/// <summary>
/// The evaluation's drift modes (<c>--map-age</c>): with maps recorded a commit before a change that
/// added a call, the old maps used as if current miss the caller's tests, the drift bridge does not,
/// and the exact maps stay the reference. Slow: builds, records twice and runs.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EvalMapAgeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "sift-eval-mapage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Drift_modes_are_judged_on_the_same_failures()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Shop/Shop.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Shop/Calc.cs", """
            namespace Shop;
            public class Calc { public decimal Tax(decimal net) => net * 0.2m; }
            public class Discount { public decimal Apply(decimal price) => price - 1m; }
            """);
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/Tests.cs", """
            using Xunit;
            namespace Shop.Tests;
            public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2m, new Calc().Tax(10m)); }
            public class DiscountTests { [Fact] public void Discounts() => Assert.True(new Discount().Apply(10m) > 0m); }
            """);
        _repo.Commit("base");
        _repo.Replace("src/Shop/Calc.cs", "price - 1m", "price + new Calc().Tax(price) - 1m");
        _repo.Commit("discount after tax");

        var stdout = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(TextWriter.Null);
            exitCode = EvalApp.Run(["--repo", _repo.Root, "--branch", "main", "--synthetic", "10", "--map-age", "1", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.Equal(0, exitCode);
        Assert.Equal(1, run.GetProperty("mapAge").GetInt32());

        var tax = run.GetProperty("mutants").EnumerateArray().Single(m => m.GetProperty("target").GetString() == "Shop.Calc.Tax");
        Assert.Contains("Shop.Tests: Shop.Tests.DiscountTests", Strings(tax.GetProperty("killed")));
        Assert.Empty(Strings(tax.GetProperty("missed")));
        var drift = tax.GetProperty("drift");
        Assert.Empty(Strings(drift.GetProperty("bridge").GetProperty("missed")));
        Assert.Empty(Strings(drift.GetProperty("noMap").GetProperty("missed")));
        Assert.Equal(["Shop.Tests: Shop.Tests.DiscountTests"], Strings(drift.GetProperty("naive").GetProperty("missed")));
        Assert.Contains("## Map drift", File.ReadAllText(Path.Combine(_output, "synthetic.md")), StringComparison.Ordinal);
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(static e => e.GetString()!)];

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, recursive: true);
        }
    }
}
