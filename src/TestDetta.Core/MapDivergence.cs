namespace TestDetta.Core;

/// <summary>A test class whose full recording ran members its carried-over entry in an incremental map lacked.</summary>
/// <param name="TestClass">Runtime class name.</param>
/// <param name="Missing">The members, as <c>Type.Member</c>, sorted.</param>
public sealed record ClassDivergence(string TestClass, IReadOnlyList<string> Missing);

/// <summary>
/// Compares an incremental map with the full recording that replaces it (docs/design/incremental-recording.md). A class
/// whose fresh entry names a member the incremental entry lacked, although the incremental map knew the member and did
/// not hold it as unobserved, means the map was missing something: nondeterminism in the test, or a gap in the selection
/// that carried the class over. Either is worth seeing, so every full recording after incremental steps reports them.
/// </summary>
public static class MapDivergence
{
    /// <summary>The classes of <paramref name="full"/> that ran members <paramref name="incremental"/> did not credit them with.</summary>
    /// <param name="incremental">The map built by incremental steps.</param>
    /// <param name="full">A full recording of the same test project.</param>
    /// <returns>Diverging classes ordered by name; empty when the incremental map held everything.</returns>
    public static IReadOnlyList<ClassDivergence> Find(CoverageMap incremental, CoverageMap full)
    {
        var known = incremental.Methods.ToHashSet();
        // Unobserved members were never claimed. Run-once code is credited to whichever class needs it first, which
        // changes with what runs before it: not a gap either.
        var ignored = new HashSet<CoveredMethod>();
        foreach (var id in incremental.Unobserved ?? [])
        {
            ignored.Add(incremental.Methods[id]);
        }

        foreach (var id in incremental.Initialization)
        {
            ignored.Add(incremental.Methods[id]);
        }

        foreach (var id in full.Initialization)
        {
            ignored.Add(full.Methods[id]);
        }

        var divergences = new List<ClassDivergence>();
        foreach (var (testClass, ids) in full.Classes.OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            // A class that failed while recording, or a map that could not say which failed, claimed nothing to compare.
            if (!incremental.Classes.TryGetValue(testClass, out var before) || incremental.FailedClasses is not { } failed || failed.Contains(testClass))
            {
                continue;
            }

            var had = Members(incremental, before, incremental.BuiltValues?.Classes.GetValueOrDefault(testClass));
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var method in Members(full, ids, full.BuiltValues?.Classes.GetValueOrDefault(testClass)))
            {
                if (known.Contains(method) && !ignored.Contains(method) && !had.Contains(method))
                {
                    missing.Add($"{method.TypeName}.{method.MemberName}");
                }
            }

            if (missing.Count > 0)
            {
                divergences.Add(new ClassDivergence(testClass, [.. missing]));
            }
        }

        return divergences;
    }

    /// <summary>The members a class ran and the members whose built values it read.</summary>
    /// <param name="map">The map the ids index.</param>
    /// <param name="ran">Ids the class ran.</param>
    /// <param name="built">Ids whose values it read, or null.</param>
    private static HashSet<CoveredMethod> Members(CoverageMap map, IReadOnlyList<int> ran, IReadOnlyList<int>? built)
    {
        var members = new HashSet<CoveredMethod>();
        foreach (var id in ran)
        {
            members.Add(map.Methods[id]);
        }

        foreach (var id in built ?? [])
        {
            members.Add(map.Methods[id]);
        }

        return members;
    }
}
