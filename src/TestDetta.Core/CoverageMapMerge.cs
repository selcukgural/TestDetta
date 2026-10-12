namespace TestDetta.Core;

/// <summary>What an incremental recording step re-recorded, and what the commits it covers changed.</summary>
/// <param name="RecordedClasses">
/// Test classes (runtime names) the step asked to record: the selection for the commits since the previous map. Each of
/// them takes what the partial recording says about it, also when it says nothing (the class is gone).
/// </param>
/// <param name="AssemblyClasses">
/// Every class in the test assembly at the new commit, from its PDB; a carried-over class outside it no longer exists
/// and is dropped. Null (or empty) when the PDB could not be read: then every carried-over class is kept.
/// </param>
/// <param name="NewlyUnobserved">Members the commits made unobserved (<see cref="CoverageMapMerge.UnobservedBy"/>).</param>
public sealed record MapMergeScope(
    IReadOnlySet<string> RecordedClasses,
    IReadOnlySet<string>? AssemblyClasses,
    IReadOnlySet<CoveredMethod> NewlyUnobserved);

/// <summary>
/// Builds the map at a new commit from the previous map and a recording of only the classes the commits in between can
/// affect (docs/design/incremental-recording.md). Classes the step recorded take their new entries; the others keep
/// theirs, re-indexed to the new method list. Whatever both maps say about code outside single classes (ambient code,
/// run-once code) is joined, so the merged map can only select more than either part would.
/// </summary>
public static class CoverageMapMerge
{
    /// <summary>Merges <paramref name="partial"/>, recorded at the new commit, into <paramref name="previous"/>.</summary>
    /// <param name="previous">The map the step started from, at an ancestor of the new commit.</param>
    /// <param name="partial">The recording of <see cref="MapMergeScope.RecordedClasses"/> at the new commit.</param>
    /// <param name="scope">What was recorded and what the commits changed.</param>
    /// <returns>The map at the new commit, with its <see cref="CoverageMap.Lineage"/> one step longer.</returns>
    public static CoverageMap Merge(CoverageMap previous, CoverageMap partial, MapMergeScope scope)
    {
        var ids = new Dictionary<CoveredMethod, int>(partial.Methods.Count);
        for (var i = 0; i < partial.Methods.Count; i++)
        {
            ids.TryAdd(partial.Methods[i], i);
        }

        var fresh = new HashSet<string>(scope.RecordedClasses, StringComparer.Ordinal);
        foreach (var testClass in ReportedClasses(partial))
        {
            fresh.Add(testClass);
        }

        var carried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testClass in ReportedClasses(previous))
        {
            if (fresh.Contains(testClass) || scope.AssemblyClasses is { Count: > 0 } exist && !exist.Contains(testClass))
            {
                continue;
            }

            carried.Add(testClass);
        }

        var remap = new Remap(previous.Methods, ids);
        var classes = Buckets(partial.Classes, previous.Classes, carried, remap);
        var fixtures = Buckets(partial.Fixtures, previous.Fixtures, FixturesToCarry(previous.Fixtures.Keys, partial), remap);
        var hits = new CoverageHits(classes, fixtures, Union(partial.Ambient, previous.Ambient, remap));

        CoverageHits? built = null;
        if (partial.BuiltValues is { } partBuilt && previous.BuiltValues is { } previousBuilt)
        {
            built = new CoverageHits(
                Buckets(partBuilt.Classes, previousBuilt.Classes, carried, remap),
                Buckets(partBuilt.Fixtures, previousBuilt.Fixtures, FixturesToCarry(previousBuilt.Fixtures.Keys, partial), remap),
                Union(partBuilt.Ambient, previousBuilt.Ambient, remap));
        }

        var initialization = Union(partial.Initialization, previous.Initialization, remap);
        var failed = Failed(previous, partial, carried, remap);
        var unobserved = new SortedSet<int>(remap.All(previous.Unobserved ?? []));
        foreach (var method in scope.NewlyUnobserved)
        {
            if (ids.TryGetValue(method, out var id))
            {
                unobserved.Add(id);
            }
        }

        return partial with
        {
            Hits = hits,
            Timings = Timings(previous, partial, carried),
            FailedClasses = failed,
            Initialization = initialization,
            LinkedInitialization = Linked(previous, partial, initialization, remap),
            // A carried class may have been the one test that also ran platform code; what is outside the tests is in
            // the ambient list either way, so the list (informational) is kept only for the new recording.
            TestPlatformCode = partial.TestPlatformCode,
            BuiltValues = built,
            FileReads = Reads(previous.FileReads, partial.FileReads, carried, partial),
            OutputUses = Reads(previous.OutputUses, partial.OutputUses, carried, partial),
            // Carried classes keep the reads the previous recording saw: what it could not see stays unseen.
            FileReadsUnseen = partial.FileReadsUnseen is { } unseen && previous.FileReadsUnseen is { } previousUnseen
                ? [.. unseen.Union(previousUnseen, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]
                : null,
            // A carried class's loads were seen by the previous recording: what either could not see stays unseen.
            OutputLoadsUnseen = partial.OutputLoadsUnseen is { } loads && previous.OutputLoadsUnseen is { } previousLoads
                ? [.. loads.Union(previousLoads, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]
                : null,
            SkippedClasses = partial.SkippedClasses is { } skipped && previous.SkippedClasses is { } previousSkipped
                ? new HashSet<string>([.. skipped, .. previousSkipped.Where(carried.Contains)], StringComparer.Ordinal)
                : null,
            ClassLocations = Locations(previous.ClassLocations, partial.ClassLocations, carried),
            LeakedReads = partial.LeakedReads is { } leaked && previous.LeakedReads is { } previousLeaked
                ? Join(leaked, previousLeaked, carried)
                : null,
            Unobserved = [.. unobserved],
            Lineage = previous.Lineage is { } lineage
                ? lineage with { Steps = lineage.Steps + 1 }
                : new MapLineage(previous.Commit, previous.Header.RecordedAt, 1),
        };
    }

    /// <summary>
    /// The members a range of commits makes unobserved in an incremental map: added members, every member of a type whose
    /// declaration was added or changed, and members whose metadata changed. A class carried over from before the range
    /// may reach them through reflection or dispatch that the selection cannot see, so the map cannot name their users
    /// until a full recording.
    /// </summary>
    /// <param name="changes">Code changes of the range.</param>
    /// <param name="methods">Every instrumented method at the range's end.</param>
    public static IReadOnlySet<CoveredMethod> UnobservedBy(IEnumerable<SymbolChange> changes, IReadOnlyList<CoveredMethod> methods)
    {
        var members = new HashSet<CoveredMethod>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.Kind == SymbolChangeKind.Removed)
            {
                continue;
            }

            if (change.MemberName is null)
            {
                types.Add(change.TypeName);
                continue;
            }

            if (change.Kind == SymbolChangeKind.Added || change.MetadataChanged)
            {
                foreach (var name in DeclarationWalker.CompiledNames(change.MemberName))
                {
                    members.Add(new CoveredMethod(change.TypeName, name));
                }
            }
        }

        var unobserved = new HashSet<CoveredMethod>();
        foreach (var method in methods)
        {
            if (members.Contains(method) || InType(method.TypeName, types))
            {
                unobserved.Add(method);
            }
        }

        return unobserved;
    }

    /// <summary>True when <paramref name="typeName"/> is one of <paramref name="types"/> or nested in one of them.</summary>
    /// <param name="typeName">Qualified type name, nested types joined with '.'.</param>
    /// <param name="types">Changed types.</param>
    private static bool InType(string typeName, HashSet<string> types)
    {
        var name = typeName;
        while (true)
        {
            if (types.Contains(name))
            {
                return true;
            }

            var dot = name.LastIndexOf('.');
            if (dot < 0)
            {
                return false;
            }

            name = name[..dot];
        }
    }

    /// <summary>Every class the runner reported for <paramref name="map"/>: ran, failed or skipped.</summary>
    /// <param name="map">A map.</param>
    private static IEnumerable<string> ReportedClasses(CoverageMap map) =>
        map.Classes.Keys.Concat(map.FailedClasses ?? Enumerable.Empty<string>()).Concat(map.SkippedClasses ?? Enumerable.Empty<string>());

    /// <summary>
    /// Fixtures of the previous map that keep their entries: those the partial recording did not run. A fixture it ran
    /// takes the new entry, which describes the fixture's code at the new commit.
    /// </summary>
    /// <param name="fixtures">Fixture names of the previous map.</param>
    /// <param name="partial">The partial recording.</param>
    private static HashSet<string> FixturesToCarry(IEnumerable<string> fixtures, CoverageMap partial)
    {
        var carry = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in fixtures)
        {
            if (!partial.Fixtures.ContainsKey(fixture) && partial.BuiltValues?.Fixtures.ContainsKey(fixture) != true)
            {
                carry.Add(fixture);
            }
        }

        return carry;
    }

    /// <summary>The partial recording's buckets, plus the carried-over buckets of the previous map re-indexed.</summary>
    /// <param name="partial">Buckets recorded at the new commit.</param>
    /// <param name="previous">Buckets of the previous map.</param>
    /// <param name="carry">Bucket names to take from <paramref name="previous"/>.</param>
    /// <param name="remap">Method id translation.</param>
    private static Dictionary<string, IReadOnlyList<int>> Buckets(
        IReadOnlyDictionary<string, IReadOnlyList<int>> partial,
        IReadOnlyDictionary<string, IReadOnlyList<int>> previous,
        HashSet<string> carry,
        Remap remap)
    {
        var buckets = new Dictionary<string, IReadOnlyList<int>>(partial, StringComparer.Ordinal);
        foreach (var (name, ids) in previous)
        {
            if (carry.Contains(name))
            {
                buckets[name] = [.. remap.All(ids).Order()];
            }
        }

        return buckets;
    }

    /// <summary>The union of a partial list and a re-indexed previous list, sorted.</summary>
    /// <param name="partial">Ids at the new commit.</param>
    /// <param name="previous">Ids of the previous map.</param>
    /// <param name="remap">Method id translation.</param>
    private static List<int> Union(IReadOnlyList<int> partial, IReadOnlyList<int> previous, Remap remap) =>
        [.. new SortedSet<int>([.. partial, .. remap.All(previous)])];

    /// <summary>
    /// Initialization that counts as linked in the merged map: linked in every part whose initialization holds it. A
    /// method one part saw as run-once code stays run-once code. Null when neither part knew linked initialization.
    /// </summary>
    /// <param name="previous">The previous map.</param>
    /// <param name="partial">The partial recording.</param>
    /// <param name="initialization">The merged initialization.</param>
    /// <param name="remap">Method id translation.</param>
    private static List<int>? Linked(CoverageMap previous, CoverageMap partial, List<int> initialization, Remap remap)
    {
        if (previous.LinkedInitialization is null && partial.LinkedInitialization is null)
        {
            return null;
        }

        var previousInitialization = remap.All(previous.Initialization).ToHashSet();
        var previousLinked = remap.All(previous.LinkedInitialization ?? []).ToHashSet();
        var partialInitialization = partial.Initialization.ToHashSet();
        var partialLinked = (partial.LinkedInitialization ?? []).ToHashSet();
        var linked = new List<int>();
        foreach (var id in initialization)
        {
            if ((!previousInitialization.Contains(id) || previousLinked.Contains(id)) && (!partialInitialization.Contains(id) || partialLinked.Contains(id)))
            {
                linked.Add(id);
            }
        }

        return linked;
    }

    /// <summary>
    /// Classes that failed while recording: the partial recording's, the carried-over failures, and every carried-over
    /// class whose entry names a method the new commit no longer has (its entry cannot be trusted). Null when either part
    /// could not say which classes failed.
    /// </summary>
    /// <param name="previous">The previous map.</param>
    /// <param name="partial">The partial recording.</param>
    /// <param name="carried">Carried-over classes.</param>
    /// <param name="remap">Method id translation.</param>
    private static HashSet<string>? Failed(CoverageMap previous, CoverageMap partial, HashSet<string> carried, Remap remap)
    {
        if (partial.FailedClasses is not { } partialFailed || previous.FailedClasses is not { } previousFailed)
        {
            return null;
        }

        var failed = new HashSet<string>(partialFailed, StringComparer.Ordinal);
        foreach (var testClass in carried)
        {
            if (previousFailed.Contains(testClass)
                || previous.Classes.TryGetValue(testClass, out var ids) && !remap.Keeps(ids)
                || previous.BuiltValues?.Classes.TryGetValue(testClass, out var built) == true && !remap.Keeps(built))
            {
                failed.Add(testClass);
            }
        }

        return failed;
    }

    /// <summary>Class and fixture times of both parts; null when either has none, since their scales would not compare.</summary>
    /// <param name="previous">The previous map.</param>
    /// <param name="partial">The partial recording.</param>
    /// <param name="carried">Carried-over classes.</param>
    private static CoverageTimings? Timings(CoverageMap previous, CoverageMap partial, HashSet<string> carried)
    {
        if (partial.Timings is not { } partTimes || previous.Timings is not { } previousTimes)
        {
            return null;
        }

        var classes = new Dictionary<string, double>(partTimes.ClassSeconds, StringComparer.Ordinal);
        foreach (var (testClass, seconds) in previousTimes.ClassSeconds)
        {
            if (carried.Contains(testClass))
            {
                classes[testClass] = seconds;
            }
        }

        var fixtures = new Dictionary<string, double>(partTimes.FixtureSeconds, StringComparer.Ordinal);
        foreach (var (fixture, seconds) in previousTimes.FixtureSeconds)
        {
            fixtures.TryAdd(fixture, seconds);
        }

        return new CoverageTimings(classes, fixtures, partTimes.StartupSeconds);
    }

    /// <summary>
    /// File reads (or output uses) of both parts: per class and fixture like the hits, ambient and initialization reads
    /// joined. Null when either part did not record them, since the merged map could then not answer for every class.
    /// </summary>
    /// <param name="previous">The previous map's reads.</param>
    /// <param name="part">The partial recording's reads.</param>
    /// <param name="carried">Carried-over classes.</param>
    /// <param name="partial">The partial recording, for the fixtures it ran.</param>
    private static FileReads? Reads(FileReads? previous, FileReads? part, HashSet<string> carried, CoverageMap partial)
    {
        if (previous is null || part is null)
        {
            return null;
        }

        return new FileReads(
            Join(part.Classes, previous.Classes, carried),
            Join(part.Fixtures, previous.Fixtures, FixturesToCarry(previous.Fixtures.Keys, partial)),
            [.. new SortedSet<string>([.. part.Ambient, .. previous.Ambient], StringComparer.Ordinal)],
            [.. new SortedSet<string>([.. part.Initialization, .. previous.Initialization], StringComparer.Ordinal)]);
    }

    /// <summary>The partial entries plus the carried-over entries of the previous map.</summary>
    /// <param name="part">Entries recorded at the new commit.</param>
    /// <param name="previous">Entries of the previous map.</param>
    /// <param name="carry">Names to take from <paramref name="previous"/>.</param>
    private static Dictionary<string, IReadOnlyList<string>> Join(
        IReadOnlyDictionary<string, IReadOnlyList<string>> part,
        IReadOnlyDictionary<string, IReadOnlyList<string>> previous,
        HashSet<string> carry)
    {
        var joined = new Dictionary<string, IReadOnlyList<string>>(part, StringComparer.Ordinal);
        foreach (var (name, values) in previous)
        {
            if (carry.Contains(name))
            {
                joined[name] = values;
            }
        }

        return joined;
    }

    /// <summary>Where each reported class is written: the partial recording's classes, plus the carried-over classes.</summary>
    /// <param name="previous">Locations of the previous map.</param>
    /// <param name="part">Locations of the partial recording.</param>
    /// <param name="carried">Carried-over classes.</param>
    private static Dictionary<string, IReadOnlyList<ClassLocation>>? Locations(
        IReadOnlyDictionary<string, IReadOnlyList<ClassLocation>>? previous,
        IReadOnlyDictionary<string, IReadOnlyList<ClassLocation>>? part,
        HashSet<string> carried)
    {
        if (previous is null && part is null)
        {
            return null;
        }

        var locations = new Dictionary<string, IReadOnlyList<ClassLocation>>(part ?? new Dictionary<string, IReadOnlyList<ClassLocation>>(), StringComparer.Ordinal);
        foreach (var (testClass, places) in previous ?? new Dictionary<string, IReadOnlyList<ClassLocation>>())
        {
            if (carried.Contains(testClass))
            {
                locations.TryAdd(testClass, places);
            }
        }

        return locations;
    }

    /// <summary>Translates method ids of the previous map to the new commit's method list, by type and member name.</summary>
    /// <param name="previousMethods">The previous map's methods.</param>
    /// <param name="ids">Id of every method at the new commit.</param>
    private sealed class Remap(IReadOnlyList<CoveredMethod> previousMethods, Dictionary<CoveredMethod, int> ids)
    {
        /// <summary>The new ids of <paramref name="previousIds"/>; methods the new commit no longer has are left out.</summary>
        /// <param name="previousIds">Ids in the previous map.</param>
        public IEnumerable<int> All(IEnumerable<int> previousIds)
        {
            foreach (var id in previousIds)
            {
                if (ids.TryGetValue(previousMethods[id], out var newId))
                {
                    yield return newId;
                }
            }
        }

        /// <summary>True when the new commit still has every method of <paramref name="previousIds"/>.</summary>
        /// <param name="previousIds">Ids in the previous map.</param>
        public bool Keeps(IReadOnlyList<int> previousIds)
        {
            foreach (var id in previousIds)
            {
                if (!ids.ContainsKey(previousMethods[id]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
