using System.Text.RegularExpressions;
using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Lab;

/// <summary>A map read from disk, with the file name it is shown by.</summary>
/// <param name="Name">The map's file name without the extension (its test project).</param>
/// <param name="Map">The map.</param>
internal sealed record NamedMap(string Name, CoverageMap Map);

/// <summary>One way some buckets of a map depend on what a pattern names.</summary>
/// <param name="Map">The map's name.</param>
/// <param name="Relation">How they depend on it: ran, read a value built by, loaded, loaded by name, scanned, read.</param>
/// <param name="Where">Which buckets: test classes, fixtures, outside every test, while initializing.</param>
/// <param name="Names">The classes or fixtures, sorted; empty for the buckets that have no name.</param>
internal sealed record WhoRow(string Map, string Relation, string Where, IReadOnlyList<string> Names);

/// <summary>
/// Answers "who used this?" from coverage maps: the buckets that ran a member, read a value it built, loaded or scanned
/// an output, or read a file. Before the lab this took a Python script over the map's JSON each time, and the bucket that
/// mattered (a value read, a scan) was easy to leave out; here every relation the map records is asked.
/// </summary>
internal static class MapExplorer
{
    /// <summary>Where the buckets of one hit or use list sit.</summary>
    private const string InClasses = "test classes";

    /// <summary>See <see cref="InClasses"/>.</summary>
    private const string InFixtures = "fixtures";

    /// <summary>See <see cref="InClasses"/>.</summary>
    private const string Outside = "outside every test";

    /// <summary>See <see cref="InClasses"/>.</summary>
    private const string Initializing = "while initializing";

    /// <summary>
    /// The map at <paramref name="path"/>, or every map of this format in a directory (one per test project, as
    /// <c>.testdetta/coverage</c> or an evaluation's recording holds them); maps of another format are left out.
    /// </summary>
    /// <param name="path">A map file or a directory of maps.</param>
    /// <exception cref="IOException">Nothing readable is there.</exception>
    public static List<NamedMap> Load(string path)
    {
        string[] files = Directory.Exists(path)
            ? [.. Directory.GetFiles(path, "*.json").Where(static f => CoverageMapStore.FormatOf(f) == CoverageMapStore.Version).Order(StringComparer.Ordinal)]
            : [path];
        var maps = new List<NamedMap>(files.Length);
        foreach (var file in files)
        {
            if (CoverageMapStore.Read(file) is { } map)
            {
                maps.Add(new NamedMap(Path.GetFileNameWithoutExtension(file), map));
            }
        }

        return maps.Count > 0 ? maps : throw new IOException($"no coverage map of format {CoverageMapStore.Version} at {path}");
    }

    /// <summary>Every relation of every map to what <paramref name="pattern"/> names, in map order.</summary>
    /// <param name="maps">The maps.</param>
    /// <param name="pattern">Type.Member, Type, an output name or a file path; <c>*</c> stands for any text.</param>
    public static List<WhoRow> Who(IReadOnlyList<NamedMap> maps, string pattern)
    {
        var exact = Glob(pattern, RegexOptions.None);
        var loose = Glob(pattern, RegexOptions.IgnoreCase);
        var rows = new List<WhoRow>();
        foreach (var (name, map) in maps)
        {
            var ids = new HashSet<int>();
            for (var i = 0; i < map.Methods.Count; i++)
            {
                var method = map.Methods[i];
                if (exact.IsMatch(method.TypeName) || exact.IsMatch($"{method.TypeName}.{method.MemberName}"))
                {
                    ids.Add(i);
                }
            }

            if (ids.Count > 0)
            {
                AddHits(rows, name, "ran", map.Hits, ids);
                AddList(rows, name, "ran", Initializing, map.Initialization, ids);
                if (map.BuiltValues is { } built)
                {
                    AddHits(rows, name, "read a value built by", built, ids);
                }
            }

            if (map.OutputUses is { } outputs)
            {
                AddUses(rows, name, outputs, use => Output(use) is var (_, output) && loose.IsMatch(output), static use => Output(use).Relation);
            }

            if (map.FileReads is { } reads)
            {
                AddUses(rows, name, reads, path => loose.IsMatch(path), static _ => "read");
            }
        }

        return rows;
    }

    /// <summary>Writes the rows as a table, or says that nothing in the maps names the pattern.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="writer">Where the table goes.</param>
    /// <param name="style">Console styling.</param>
    public static void Write(IReadOnlyList<WhoRow> rows, TextWriter writer, ConsoleStyle style)
    {
        if (rows.Count == 0)
        {
            writer.WriteLine(style.Paint(Role.Muted, "Nothing in the maps ran, built, loaded, scanned or read what the pattern names."));
            return;
        }

        var table = new TextTable(style, new TextColumn("Map"), new TextColumn("Relation"), new TextColumn("Where"), new TextColumn("Count", AlignRight: true), new TextColumn("Names", Wrap: true));
        foreach (var row in rows)
        {
            table.Add(
                new Cell(row.Map),
                new Cell(row.Relation, Role.Heading),
                new Cell(row.Where),
                new Cell(row.Names.Count == 0 ? "-" : row.Names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Role.Number),
                new Cell(string.Join(", ", row.Names.Select(Short)), Role.Test));
        }

        table.Write(writer);
    }

    /// <summary>The relation an output use records and the output's name: a plain name is a load by path or a start.</summary>
    /// <param name="use">An entry of <see cref="CoverageMap.OutputUses"/>.</param>
    private static (string Relation, string Output) Output(string use) =>
        use.StartsWith(CoverageMap.OutputScanPrefix, StringComparison.Ordinal) ? ("scanned", use[CoverageMap.OutputScanPrefix.Length..])
        : use.StartsWith(CoverageMap.OutputNameLoadPrefix, StringComparison.Ordinal) ? ("loaded by name", use[CoverageMap.OutputNameLoadPrefix.Length..])
        : ("loaded or started", use);

    /// <summary>Adds the classes and fixtures among <paramref name="hits"/> that ran one of <paramref name="ids"/>, and the ambient list.</summary>
    /// <param name="rows">Rows so far; updated in place.</param>
    /// <param name="map">The map's name.</param>
    /// <param name="relation">The relation the hits stand for.</param>
    /// <param name="hits">Hits per bucket.</param>
    /// <param name="ids">Method ids the pattern names.</param>
    private static void AddHits(List<WhoRow> rows, string map, string relation, CoverageHits hits, HashSet<int> ids)
    {
        AddBuckets(rows, map, relation, InClasses, hits.Classes, ids);
        AddBuckets(rows, map, relation, InFixtures, hits.Fixtures, ids);
        AddList(rows, map, relation, Outside, hits.Ambient, ids);
    }

    /// <summary>Adds one row naming the buckets whose list holds one of <paramref name="ids"/>, when there is one.</summary>
    /// <param name="rows">Rows so far; updated in place.</param>
    /// <param name="map">The map's name.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="where">Which kind of bucket.</param>
    /// <param name="buckets">Lists per bucket.</param>
    /// <param name="ids">Method ids the pattern names.</param>
    private static void AddBuckets(List<WhoRow> rows, string map, string relation, string where, IReadOnlyDictionary<string, IReadOnlyList<int>> buckets, HashSet<int> ids)
    {
        var names = buckets.Where(b => b.Value.Any(ids.Contains)).Select(static b => b.Key).Order(StringComparer.Ordinal).ToList();
        if (names.Count > 0)
        {
            rows.Add(new WhoRow(map, relation, where, names));
        }
    }

    /// <summary>Adds a row without names when a list of the whole process holds one of <paramref name="ids"/>.</summary>
    /// <param name="rows">Rows so far; updated in place.</param>
    /// <param name="map">The map's name.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="where">Outside every test, or while initializing.</param>
    /// <param name="list">The list.</param>
    /// <param name="ids">Method ids the pattern names.</param>
    private static void AddList(List<WhoRow> rows, string map, string relation, string where, IReadOnlyList<int> list, HashSet<int> ids)
    {
        if (list.Any(ids.Contains))
        {
            rows.Add(new WhoRow(map, relation, where, []));
        }
    }

    /// <summary>Adds a row per relation and kind of bucket for the uses (files read, outputs used) that match.</summary>
    /// <param name="rows">Rows so far; updated in place.</param>
    /// <param name="map">The map's name.</param>
    /// <param name="uses">The uses per bucket.</param>
    /// <param name="matches">True for a use the pattern names.</param>
    /// <param name="relation">The relation a use stands for.</param>
    private static void AddUses(List<WhoRow> rows, string map, FileReads uses, Func<string, bool> matches, Func<string, string> relation)
    {
        foreach (var (where, buckets) in (ReadOnlySpan<(string, IReadOnlyDictionary<string, IReadOnlyList<string>>)>)[(InClasses, uses.Classes), (InFixtures, uses.Fixtures)])
        {
            foreach (var group in buckets.SelectMany(b => b.Value.Where(matches).Select(u => (Relation: relation(u), Bucket: b.Key))).GroupBy(static u => u.Relation).OrderBy(static g => g.Key, StringComparer.Ordinal))
            {
                rows.Add(new WhoRow(map, group.Key, where, [.. group.Select(static u => u.Bucket).Distinct().Order(StringComparer.Ordinal)]));
            }
        }

        foreach (var (where, list) in (ReadOnlySpan<(string, IReadOnlyList<string>)>)[(Outside, uses.Ambient), (Initializing, uses.Initialization)])
        {
            foreach (var found in list.Where(matches).Select(relation).Distinct().Order(StringComparer.Ordinal))
            {
                rows.Add(new WhoRow(map, found, where, []));
            }
        }
    }

    /// <summary>A pattern with <c>*</c> for any text, matched whole.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="options">Case handling.</param>
    private static Regex Glob(string pattern, RegexOptions options) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", options | RegexOptions.CultureInvariant);

    /// <summary>A class or fixture name without its namespace, which the map column already places.</summary>
    /// <param name="name">Runtime name.</param>
    private static string Short(string name) => name[(name.LastIndexOf('.') + 1)..];
}
