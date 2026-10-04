namespace Sift.Core;

/// <summary>
/// How the candidate test classes of one project that a coverage map never reported relate to the
/// map's orphans, the classes it reported that the build no longer has.
/// </summary>
/// <param name="Aliases">Candidate to the one orphan written at its place: the map recorded the class under that name.</param>
/// <param name="Mismatches">
/// Candidate to the orphans that make its pairing ambiguous: several orphans at its place, an orphan
/// also at another candidate's place, or orphans in its file at no candidate's place.
/// </param>
/// <param name="PairedOrphans">Orphans placed inside a candidate; their names are not in the build.</param>
internal sealed record RecordedClassMatches(
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Mismatches,
    IReadOnlySet<string> PairedOrphans)
{
    /// <summary>No candidate outside the map, or nothing to match it with.</summary>
    public static RecordedClassMatches None { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Matches test classes a coverage map never reported with the map's orphans by place: the class
/// whose declaration contains the line the map recorded for an orphan is that orphan under another
/// name, as when a build regenerates committed test sources with culture-dependent names (Markdig).
/// docs/design/unrecorded-classes.md, rule 2.
/// </summary>
internal static class RecordedClassMatcher
{
    /// <summary>
    /// Pairs the candidates of one test project outside <paramref name="map"/>'s reported classes with
    /// its orphans. A candidate with no orphan in its files is in neither result: nothing shows that the
    /// map should have known it (an <c>[Explicit]</c>-only class, one the repository's filter excludes).
    /// </summary>
    /// <param name="map">A map that knows its classes (<see cref="CoverageMap.KnowsClasses"/>).</param>
    /// <param name="types">Indexed types of the map's test project.</param>
    /// <param name="repoRoot">Repository root, which the map's locations are relative to.</param>
    public static RecordedClassMatches Match(CoverageMap map, IReadOnlyList<IndexedType> types, string repoRoot)
    {
        if (map.ClassLocations is not { } locations)
        {
            return RecordedClassMatches.None;
        }

        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var inBuild = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            inBuild.Add(type.RuntimeTypeName);
            if (type.CanContainTests && !map.Reported(type.RuntimeTypeName))
            {
                unknown.Add(type.RuntimeTypeName);
            }
        }

        if (unknown.Count == 0)
        {
            return RecordedClassMatches.None;
        }

        var pairsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var candidatesOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var unpairedByFile = new Dictionary<string, HashSet<string>>(Paths.Comparer);
        foreach (var (orphan, places) in locations)
        {
            if (inBuild.Contains(orphan) || !map.Reported(orphan))
            {
                continue;
            }

            foreach (var place in places)
            {
                var file = Paths.Normalize(Path.Combine(repoRoot, place.File));
                var owner = Innermost(types, file, place.Line);
                if (owner is not null && unknown.Contains(owner))
                {
                    Add(pairsOf, owner, orphan);
                    Add(candidatesOf, orphan, owner);
                    continue;
                }

                if (!unpairedByFile.TryGetValue(file, out var unpaired))
                {
                    unpaired = new HashSet<string>(StringComparer.Ordinal);
                    unpairedByFile[file] = unpaired;
                }

                unpaired.Add(orphan);
            }
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var mismatches = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            var candidate = type.RuntimeTypeName;
            if (!unknown.Contains(candidate))
            {
                continue;
            }

            if (pairsOf.TryGetValue(candidate, out var orphans))
            {
                if (orphans.Count == 1 && candidatesOf[orphans.First()].Count == 1)
                {
                    aliases[candidate] = orphans.First();
                    continue;
                }

                mismatches[candidate] = [.. orphans.Order(StringComparer.Ordinal)];
                continue;
            }

            var inItsFiles = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var span in type.Spans)
            {
                if (unpairedByFile.TryGetValue(span.File, out var unpaired))
                {
                    inItsFiles.UnionWith(unpaired);
                }
            }

            if (inItsFiles.Count > 0)
            {
                mismatches[candidate] = [.. inItsFiles];
            }
        }

        return new RecordedClassMatches(aliases, mismatches, new HashSet<string>(candidatesOf.Keys, StringComparer.Ordinal));
    }

    /// <summary>
    /// Runtime name of the innermost type whose declaration contains <paramref name="line"/> of
    /// <paramref name="file"/>: for a line inside a nested class, the nested class. Null when no
    /// declaration contains it.
    /// </summary>
    /// <param name="types">Indexed types of the project.</param>
    /// <param name="file">Normalized file path.</param>
    /// <param name="line">1-based line.</param>
    private static string? Innermost(IReadOnlyList<IndexedType> types, string file, int line)
    {
        string? innermost = null;
        var size = int.MaxValue;
        foreach (var type in types)
        {
            foreach (var span in type.Spans)
            {
                if (span.Contains(file, line) && span.EndLine - span.StartLine < size)
                {
                    innermost = type.RuntimeTypeName;
                    size = span.EndLine - span.StartLine;
                }
            }
        }

        return innermost;
    }

    /// <summary>Adds <paramref name="value"/> to the set under <paramref name="key"/>.</summary>
    /// <param name="sets">Sets by key.</param>
    /// <param name="key">Key.</param>
    /// <param name="value">Value to add.</param>
    private static void Add(Dictionary<string, HashSet<string>> sets, string key, string value)
    {
        if (!sets.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            sets[key] = set;
        }

        set.Add(value);
    }
}
