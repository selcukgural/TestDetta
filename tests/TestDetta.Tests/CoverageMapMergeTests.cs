using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>Merging a partial recording into the previous map (docs/design/incremental-recording.md).</summary>
public sealed class CoverageMapMergeTests
{
    private static readonly DateTimeOffset Then = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CoverageMap Map(string commit, CoveredMethod[] methods, Dictionary<string, IReadOnlyList<int>> classes) =>
        new(new CoverageMapHeader("tests/T.Tests.csproj", commit, Then), methods,
            new CoverageHits(classes, new Dictionary<string, IReadOnlyList<int>>(), []))
        {
            SkippedClasses = new HashSet<string>(StringComparer.Ordinal),
        };

    private static readonly CoveredMethod Add = new("Lib.Calc", "Add");
    private static readonly CoveredMethod Mul = new("Lib.Calc", "Mul");
    private static readonly CoveredMethod Div = new("Lib.Calc", "Div");
    private static readonly CoveredMethod Plugin = new("Lib.NewPlugin", "Run");

    private static MapMergeScope Scope(params string[] recorded) =>
        new(recorded.ToHashSet(StringComparer.Ordinal), null, new HashSet<CoveredMethod>());

    private static IEnumerable<CoveredMethod> Ran(CoverageMap map, string testClass) => map.Classes[testClass].Select(id => map.Methods[id]);

    [Fact]
    public void A_carried_class_keeps_its_entry_under_the_new_method_ids()
    {
        var previous = Map("a", [Add, Mul], new() { ["T.AddTests"] = [0], ["T.MulTests"] = [1] });
        var partial = Map("h", [Plugin, Mul, Add], new() { ["T.MulTests"] = [1, 0] });

        var merged = CoverageMapMerge.Merge(previous, partial, Scope("T.MulTests"));

        Assert.Equal("h", merged.Commit);
        Assert.Equal([Add], Ran(merged, "T.AddTests"));
        Assert.Equal([Mul, Plugin], Ran(merged, "T.MulTests").OrderBy(static m => m.TypeName, StringComparer.Ordinal));
    }

    [Fact]
    public void A_recorded_class_the_partial_run_did_not_report_is_gone()
    {
        var previous = Map("a", [Add, Mul], new() { ["T.AddTests"] = [0], ["T.MulTests"] = [1] });
        var partial = Map("h", [Add], []);

        var merged = CoverageMapMerge.Merge(previous, partial, Scope("T.MulTests"));

        Assert.Equal(["T.AddTests"], merged.Classes.Keys);
    }

    [Fact]
    public void A_carried_class_missing_from_the_test_assembly_is_dropped()
    {
        var previous = Map("a", [Add, Mul], new() { ["T.AddTests"] = [0], ["T.OldTests"] = [1] });
        var partial = Map("h", [Add, Mul], []);

        var merged = CoverageMapMerge.Merge(previous, partial, Scope() with { AssemblyClasses = new HashSet<string>(["T.AddTests"], StringComparer.Ordinal) });

        Assert.Equal(["T.AddTests"], merged.Classes.Keys);
    }

    [Fact]
    public void A_carried_class_whose_entry_names_a_removed_method_counts_as_failed()
    {
        var previous = Map("a", [Add, Div], new() { ["T.AddTests"] = [0], ["T.DivTests"] = [1] });
        var partial = Map("h", [Add], []);

        var merged = CoverageMapMerge.Merge(previous, partial, Scope());

        Assert.Contains("T.DivTests", merged.FailedClasses!);
        Assert.DoesNotContain("T.AddTests", merged.FailedClasses!);
    }

    [Fact]
    public void Ambient_and_run_once_code_of_both_parts_is_kept()
    {
        var previous = Map("a", [Add, Mul], []) with { Hits = new CoverageHits(new Dictionary<string, IReadOnlyList<int>>(), new Dictionary<string, IReadOnlyList<int>>(), [0]), Initialization = [1], LinkedInitialization = [1] };
        var partial = Map("h", [Mul, Add], []) with { Initialization = [0], LinkedInitialization = [] };

        var merged = CoverageMapMerge.Merge(previous, partial, Scope());

        Assert.Equal([Add], merged.Ambient.Select(id => merged.Methods[id]));
        Assert.Equal([Mul], merged.Initialization.Select(id => merged.Methods[id]));
        // Run-once code in the partial run stays run-once although the previous map had it linked.
        Assert.Empty(merged.LinkedInitialization!);
    }

    [Fact]
    public void A_fixture_the_partial_run_set_up_takes_its_new_entry_and_the_others_are_kept()
    {
        var fixtures = new Dictionary<string, IReadOnlyList<int>> { ["T.DbFixture"] = [0], ["T.WebFixture"] = [1] };
        var previous = Map("a", [Add, Mul], []) with { Hits = new CoverageHits(new Dictionary<string, IReadOnlyList<int>>(), fixtures, []) };
        var partial = Map("h", [Add, Mul, Div], []) with
        {
            Hits = new CoverageHits(new Dictionary<string, IReadOnlyList<int>>(), new Dictionary<string, IReadOnlyList<int>> { ["T.DbFixture"] = [2] }, []),
        };

        var merged = CoverageMapMerge.Merge(previous, partial, Scope());

        Assert.Equal([Div], merged.Fixtures["T.DbFixture"].Select(id => merged.Methods[id]));
        Assert.Equal([Mul], merged.Fixtures["T.WebFixture"].Select(id => merged.Methods[id]));
    }

    [Fact]
    public void Unobserved_members_pile_up_until_a_full_recording()
    {
        var previous = Map("a", [Add, Mul], new() { ["T.AddTests"] = [0] }) with { Unobserved = [1] };
        var partial = Map("h", [Plugin, Mul, Add], new() { ["T.PluginTests"] = [0] });

        var merged = CoverageMapMerge.Merge(previous, partial, Scope("T.PluginTests") with { NewlyUnobserved = new HashSet<CoveredMethod> { Plugin } });

        // The plugin stays unobserved although a recorded class ran it: a carried-over class may reach it too.
        Assert.Equal([Plugin, Mul], merged.Unobserved!.Select(id => merged.Methods[id]));
    }

    [Fact]
    public void Each_step_lengthens_the_lineage_from_the_last_full_recording()
    {
        var full = Map("a", [Add], new() { ["T.AddTests"] = [0] });
        var first = CoverageMapMerge.Merge(full, Map("b", [Add], []), Scope());
        var second = CoverageMapMerge.Merge(first, Map("c", [Add], []), Scope());

        Assert.Equal(new MapLineage("a", Then, 1), first.Lineage);
        Assert.Equal(new MapLineage("a", Then, 2), second.Lineage);
    }

    [Fact]
    public void A_part_that_could_not_tell_failures_or_reads_leaves_the_merged_map_unable_to_tell()
    {
        var previous = Map("a", [Add], new() { ["T.AddTests"] = [0] }) with
        {
            FileReads = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
        };
        var partial = Map("h", [Add], []) with { FailedClasses = null };

        var merged = CoverageMapMerge.Merge(previous, partial, Scope());

        Assert.Null(merged.FailedClasses);
        Assert.Null(merged.FileReads);
    }

    [Fact]
    public void Added_members_changed_types_and_changed_metadata_become_unobserved_but_a_changed_body_does_not()
    {
        CoveredMethod[] methods = [Add, Mul, Div, Plugin, new("Lib.NewPlugin.Options", "get_Level"), new("Lib.Old", "Run")];
        SymbolChange[] changes =
        [
            new("Lib.Calc", "Add(int,int)", SymbolChangeKind.Modified),
            new("Lib.Calc", "Mul(int,int)", SymbolChangeKind.Added),
            new("Lib.Calc", "Div(int,int)", SymbolChangeKind.Modified) { MetadataChanged = true },
            new("Lib.NewPlugin", null, SymbolChangeKind.Added),
            new("Lib.Old", null, SymbolChangeKind.Removed),
        ];

        var unobserved = CoverageMapMerge.UnobservedBy(changes, methods);

        Assert.Equal([Mul, Div, Plugin, new("Lib.NewPlugin.Options", "get_Level")], methods.Where(unobserved.Contains));
    }

    [Fact]
    public void A_full_recording_reports_a_class_that_ran_code_its_carried_entry_lacked()
    {
        var incremental = Map("b", [Add, Mul, Plugin], new() { ["T.ScanTests"] = [0], ["T.MulTests"] = [1] }) with
        {
            Unobserved = [2],
            Lineage = new MapLineage("a", Then, 3),
        };
        var full = Map("c", [Plugin, Mul, Add], new() { ["T.ScanTests"] = [0, 2, 1], ["T.MulTests"] = [1] });

        var divergence = Assert.Single(MapDivergence.Find(incremental, full));

        // The plugin was unobserved, so only Mul counts as missing.
        Assert.Equal("T.ScanTests", divergence.TestClass);
        Assert.Equal(["Lib.Calc.Mul"], divergence.Missing);
    }
}
