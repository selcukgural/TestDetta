using TestDetta.Cli;
using TestDetta.Core;
using TestDetta.Lab;

namespace TestDetta.Tests;

/// <summary>The lab's answers: who used something according to a map, and how two evaluation runs differ.</summary>
public sealed class LabTests
{
    private static NamedMap Map() => new("Host.Tests", new CoverageMap(
        new CoverageMapHeader("tests/Host.Tests/Host.Tests.csproj", "abc", DateTimeOffset.UtcNow),
        [new CoveredMethod("Plugin.Greeter", "Name"), new CoveredMethod("Lib.Calc", "Tax")],
        new CoverageHits(
            new Dictionary<string, IReadOnlyList<int>> { ["Host.Tests.ALoaderTests"] = [0], ["Host.Tests.CalcTests"] = [1] },
            new Dictionary<string, IReadOnlyList<int>> { ["Host.Tests.HostFixture"] = [0] },
            [1]),
        null)
    {
        BuiltValues = new CoverageHits(new Dictionary<string, IReadOnlyList<int>> { ["Host.Tests.ReaderTests"] = [0] }, new Dictionary<string, IReadOnlyList<int>>(), []),
        OutputUses = new FileReads(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Host.Tests.ALoaderTests"] = ["Plugin"],
                ["Host.Tests.BShapeTests"] = [CoverageMap.OutputScanPrefix + "Plugin"],
            },
            new Dictionary<string, IReadOnlyList<string>>(),
            [CoverageMap.OutputNameLoadPrefix + "Plugin"],
            []),
    });

    [Fact]
    public void Who_lists_every_relation_the_map_records()
    {
        var rows = MapExplorer.Who([Map()], "Plugin*");

        Assert.Contains(rows, static r => r is { Relation: "ran", Where: "test classes" } && r.Names.SequenceEqual(["Host.Tests.ALoaderTests"]));
        Assert.Contains(rows, static r => r is { Relation: "ran", Where: "fixtures" } && r.Names.SequenceEqual(["Host.Tests.HostFixture"]));
        Assert.Contains(rows, static r => r is { Relation: "read a value built by", Where: "test classes" } && r.Names.SequenceEqual(["Host.Tests.ReaderTests"]));
        Assert.Contains(rows, static r => r is { Relation: "loaded or started", Where: "test classes" } && r.Names.SequenceEqual(["Host.Tests.ALoaderTests"]));
        Assert.Contains(rows, static r => r is { Relation: "scanned", Where: "test classes" } && r.Names.SequenceEqual(["Host.Tests.BShapeTests"]));
        Assert.Contains(rows, static r => r is { Relation: "loaded by name", Where: "outside every test" });
        Assert.DoesNotContain(rows, static r => r.Names.Contains("Host.Tests.CalcTests"));
    }

    [Fact]
    public void Who_matches_a_member_and_says_when_nothing_matches()
    {
        var rows = MapExplorer.Who([Map()], "Lib.Calc.Tax");
        var output = new StringWriter();

        Assert.Equal(["ran"], rows.Select(static r => r.Relation).Distinct());
        Assert.Contains(rows, static r => r is { Where: "outside every test" });
        MapExplorer.Write(MapExplorer.Who([Map()], "Nothing.Here"), output, ConsoleStyle.Plain);
        Assert.Contains("Nothing in the maps", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ab_compares_the_common_mutants_and_counts_the_rest()
    {
        RunMutant[] first = [new("A.M", "throw", 0.2, Killed: true, Missed: false), new("B.M", "throw", 0.5, false, false), new("C.M", "throw", 0.1, true, true)];
        RunMutant[] second = [new("A.M", "throw", 0.3, true, false), new("B.M", "throw", 0.5, false, false), new("D.M", "throw", 0.9, true, false)];

        var comparison = RunComparison.Compare("repo", first, second);

        Assert.Equal(2, comparison.Common);
        Assert.Equal(1, comparison.OnlyFirst);
        Assert.Equal(1, comparison.OnlySecond);
        Assert.Equal(0.35, comparison.FirstMean, 6);
        Assert.Equal(0.40, comparison.SecondMean, 6);
        Assert.Equal([new ShareChange("A.M", "throw", 0.2, 0.3)], comparison.Changes);
        Assert.Empty(comparison.FirstMissed);
        Assert.Equal(1, comparison.Failing);
    }
}
