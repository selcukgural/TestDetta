using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// Run-once code whose result is stored into a field is answered through the values it built, not
/// traced by name (docs/design/run-once-cascades.md, C). Code whose result leaves another way stays
/// run-once.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LinkedInitializationTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    /// <summary>How RateService keeps the rate Build computes.</summary>
    /// <param name="cache">One of the cache shapes below.</param>
    private static string RateService(string cache) => cache switch
    {
        // A static cache filled with GetOrAdd: linked to the field.
        "getoradd" => """
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, decimal> Cache = new();
            public decimal For(string country) => Cache.GetOrAdd(country, Build);
            """,
        // A Lazy<T> in a field: linked to the field.
        "lazy" => """
            private static readonly System.Lazy<decimal> Turkey = new(() => new RateService().Build("tr"));
            public decimal For(string country) => Turkey.Value;
            """,
        // Linked in one fill and unlinked in another: one unseen path keeps it run-once.
        "mixed" => """
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, decimal> Cache = new();
            private static System.Collections.Concurrent.ConcurrentDictionary<string, decimal> Shared() => Cache;
            public decimal For(string country)
            {
                Shared().GetOrAdd(country + "!", Build);
                return Cache.GetOrAdd(country, Build);
            }
            """,
        // A hand-rolled cache: looked up with TryGetValue, filled through the indexer (NodaTime's
        // DateTimeZoneCache). The value built on a miss is linked to the dictionary's field.
        "pool" => """
            private static readonly System.Collections.Generic.Dictionary<string, Holder> Cache = [];
            public sealed class Holder(decimal value) { public decimal Value { get; } = value; }
            public decimal For(string country)
            {
                if (!Cache.TryGetValue(country, out var holder))
                {
                    holder = new Holder(Build(country));
                    Cache[country] = holder;
                }

                return holder.Value;
            }
            """,
        // A list in a field added to outside any fill: the add stores the value for the list's readers.
        "list" => """
            private static readonly System.Collections.Generic.List<decimal> Seen = [];
            public decimal For(string country)
            {
                if (Seen.Count == 0)
                {
                    Seen.Add(Build(country));
                }

                return Seen[0];
            }
            """,
        // A second cache built from the first: its users depend on what built the first.
        "derived" => """
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, decimal> Cache = new();
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Labels = new();
            public decimal For(string country) => Cache.GetOrAdd(country, Build);
            public string Label(string country) => Labels.GetOrAdd(country, static c => "rate " + new RateService().For(c).ToString(System.Globalization.CultureInfo.InvariantCulture));
            """,
        // The cache comes back from a method, not a field load: the probes cannot follow it.
        _ => """
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, decimal> Cache = new();
            private static System.Collections.Concurrent.ConcurrentDictionary<string, decimal> Shared() => Cache;
            public decimal For(string country) => Shared().GetOrAdd(country, Build);
            """,
    };

    [Theory]
    [InlineData("getoradd", true)]
    [InlineData("lazy", true)]
    [InlineData("pool", true)]
    [InlineData("unlinked", false)]
    [InlineData("mixed", false)]
    public void A_cache_kept_in_a_field_is_answered_through_its_readers_and_starts_no_name_trace(string cache, bool linked)
    {
        var map = Record(cache);
        var rate = map.Methods.ToList().IndexOf(new CoveredMethod("App.Table", "Rate"));
        Assert.Contains(rate, map.Initialization);
        Assert.True(linked == map.LinkedInitialization!.Contains(rate),
            $"init=[{string.Join(",", map.Initialization.Select(i => map.Methods[i]))}] linked=[{string.Join(",", map.LinkedInitialization!.Select(i => map.Methods[i]))}]");

        _repo.Replace("src/App/Rates.cs", "0.2m : 0.1m", "0.25m : 0.1m");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        // Linked: the classes that ran the fill or read its field. Unlinked: the name trace as before,
        // which also reaches ReportTests through the shared name.
        Assert.Equal(
            linked ? ["App.Tests.AlphaTests", "App.Tests.BetaTests"] : ["App.Tests.AlphaTests", "App.Tests.BetaTests", "App.Tests.ReportTests"],
            selection.TestClasses);
    }

    /// <summary>Writes the repository with <paramref name="cache"/>, records its map, stores it and returns it.</summary>
    /// <param name="cache">Cache shape, see <see cref="RateService"/>.</param>
    private CoverageMap Record(string cache)
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Rates.cs", $$"""
            namespace App;
            public sealed class RateService
            {
                {{RateService(cache)}}
                public decimal Build(string country) => Table.Rate(country);
            }
            public static class Table { public static decimal Rate(string country) => country == "tr" ? 0.2m : 0.1m; }
            // Shares the name Build with RateService: only a name trace reaches it.
            public sealed class Report { public string Build() => "report"; }
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            // Alpha fills the cache; Beta only reads what Alpha's run built.
            public class AlphaTests { [Fact] public void Turkey() => Assert.Equal(0.2m, new App.RateService().For("tr")); }
            public class BetaTests { [Fact] public void Turkey_again() => Assert.Equal(0.2m, new App.RateService().For("tr")); }
            public class ReportTests { [Fact] public void Builds() => Assert.Equal("report", new App.Report().Build()); }
            public class PlainTests { [Fact] public void Holds() => Assert.True(true); }
            """ + (cache == "derived" ? """

            // Aaa fills both caches, the rates first; Gamma only reads the labels, built from the rates.
            public class AaaTests
            {
                [Fact]
                public void Rate_then_label()
                {
                    // The rate is built and cached first; the label is built later from the cached rate.
                    Assert.Equal(0.2m, new App.RateService().For("tr"));
                    Assert.Equal("rate 0.2", new App.RateService().Label("tr"));
                }
            }
            public class GammaTests { [Fact] public void Label() => Assert.Equal("rate 0.2", new App.RateService().Label("tr")); }
            """ : ""));

        if (cache == "derived")
        {
            // Classes run one at a time by name, so Aaa always fills before Gamma reads.
            _repo.Write("tests/App.Tests/Order.cs", """
                [assembly: Xunit.TestCollectionOrderer("App.Tests.ByName", "App.Tests")]
                [assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
                namespace App.Tests;
                public sealed class ByName : Xunit.ITestCollectionOrderer
                {
                    public System.Collections.Generic.IEnumerable<Xunit.Abstractions.ITestCollection> OrderTestCollections(System.Collections.Generic.IEnumerable<Xunit.Abstractions.ITestCollection> collections) =>
                        System.Linq.Enumerable.OrderBy(collections, static c => c.DisplayName, System.StringComparer.Ordinal);
                }
                """);
        }

        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        return map;
    }

    [Fact]
    public void A_value_built_from_another_built_value_depends_on_what_built_that_one()
    {
        Record("derived");

        _repo.Replace("src/App/Rates.cs", "0.2m : 0.1m", "0.25m : 0.1m");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        // Gamma never ran Build nor read the rates' cache: only the labels, which were built from them.
        Assert.Contains("App.Tests.GammaTests", selection.TestClasses);
        Assert.Contains("App.Tests.AaaTests", selection.TestClasses);
    }

    [Fact]
    public void A_value_added_to_a_collection_in_a_field_is_credited_to_the_collections_readers()
    {
        var map = Record("list");

        // Alpha built the value; Beta only read the list, and depends on what built it all the same.
        var built = map.BuiltValues!.Classes["App.Tests.BetaTests"].Select(i => map.Methods[i]).ToList();
        Assert.Contains(new CoveredMethod("App.Table", "Rate"), built);
        Assert.Contains(new CoveredMethod("App.RateService", "Build"), built);
    }

    public void Dispose() => _repo.Dispose();
}
