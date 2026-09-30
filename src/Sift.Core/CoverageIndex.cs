namespace Sift.Core;

/// <summary>
/// Lookup view of one <see cref="CoverageMap"/>: which test classes ran a given member or any
/// member of a given type.
/// </summary>
internal sealed class CoverageIndex
{
    /// <summary>Test classes per (type, member) key.</summary>
    private readonly Dictionary<CoveredMethod, HashSet<string>> _classesByMember = [];

    /// <summary>Test classes per type, over all its members and nested types.</summary>
    private readonly Dictionary<string, HashSet<string>> _classesByType = new(StringComparer.Ordinal);

    /// <summary>Members that ran before the first test (discovery, theory data): their callers are unknown.</summary>
    private readonly HashSet<CoveredMethod> _ambient = [];

    /// <summary>Number of buckets (test classes and fixtures, before fixtures are expanded) that ran each member.</summary>
    private readonly Dictionary<CoveredMethod, int> _bucketCount = [];

    /// <summary>Members with an overload that ran inside a static constructor or a cache fill.</summary>
    private readonly HashSet<CoveredMethod> _initialization = [];

    /// <summary>Members that some process ran in two or more buckets; null when the map does not say.</summary>
    private readonly HashSet<CoveredMethod>? _sharedInProcess;

    /// <summary>Builds the lookups; members that no test ran are still known, with no classes.</summary>
    /// <param name="map">Recorded map.</param>
    /// <param name="classesUsingFixture">
    /// Test classes (runtime names) that use a fixture type. A fixture nobody is known to use makes
    /// its methods unanswerable, like code that ran outside every test.
    /// </param>
    public CoverageIndex(CoverageMap map, Func<string, IReadOnlyCollection<string>> classesUsingFixture)
    {
        Map = map;
        foreach (var method in map.Methods)
        {
            _classesByMember.TryAdd(method, []);
            foreach (var type in EnclosingTypes(method.TypeName))
            {
                _classesByType.TryAdd(type, []);
            }
        }

        foreach (var (testClass, ids) in map.Classes)
        {
            foreach (var id in ids)
            {
                var method = map.Methods[id];
                _bucketCount[method] = _bucketCount.GetValueOrDefault(method) + 1;
                _classesByMember[method].Add(testClass);
                foreach (var type in EnclosingTypes(method.TypeName))
                {
                    _classesByType[type].Add(testClass);
                }
            }
        }

        foreach (var (fixture, ids) in map.Fixtures)
        {
            var users = classesUsingFixture(fixture);
            foreach (var id in ids)
            {
                var method = map.Methods[id];
                _bucketCount[method] = _bucketCount.GetValueOrDefault(method) + 1;
                if (users.Count == 0)
                {
                    _ambient.Add(method);
                    continue;
                }

                _classesByMember[method].UnionWith(users);
                foreach (var type in EnclosingTypes(method.TypeName))
                {
                    _classesByType[type].UnionWith(users);
                }
            }
        }

        foreach (var id in map.Ambient)
        {
            _ambient.Add(map.Methods[id]);
        }

        foreach (var id in map.Initialization)
        {
            _initialization.Add(map.Methods[id]);
        }

        if (map.SharedInProcess is { } shared)
        {
            _sharedInProcess = [.. shared.Select(id => map.Methods[id])];
        }
    }

    /// <summary>True when an overload of the member ran inside a static constructor or a cache fill.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name, <c>.ctor</c> for constructors.</param>
    public bool RanDuringInitialization(string typeName, string member) =>
        _initialization.Contains(new CoveredMethod(typeName, member));

    /// <summary>
    /// True when some bucket ran the member but no recording process ran it in two buckets: with
    /// one process that is a single test class; with several, one per process, the pattern of code
    /// that runs once and is cached. False when the map does not record this.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name, <c>.ctor</c> for constructors.</param>
    public bool RanInOneBucketPerProcess(string typeName, string member)
    {
        var method = new CoveredMethod(typeName, member);
        return _sharedInProcess is not null && _bucketCount.GetValueOrDefault(method) > 0 && !_sharedInProcess.Contains(method);
    }

    /// <summary>The map this index reads.</summary>
    public CoverageMap Map { get; }

    /// <summary>
    /// Test classes that ran <paramref name="member"/> of <paramref name="typeName"/>, or null when
    /// the map cannot answer: the member was not instrumented, or it also ran outside any test.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name as in <see cref="CoveredMethod.MemberName"/>.</param>
    public IReadOnlySet<string>? ClassesRunning(string typeName, string member)
    {
        var key = new CoveredMethod(typeName, member);
        if (_ambient.Contains(key))
        {
            return null;
        }

        return _classesByMember.GetValueOrDefault(key);
    }

    /// <summary>
    /// How many test classes and fixtures ran the member, counting a fixture once however many classes
    /// use it. Code that caches its result runs once per test process, for whichever bucket comes first,
    /// so a count of one is weak evidence for such code.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name.</param>
    public int BucketsRunning(string typeName, string member) =>
        _bucketCount.GetValueOrDefault(new CoveredMethod(typeName, member));

    /// <summary>Test classes that ran any member of <paramref name="typeName"/> or its nested types; null when none was instrumented.</summary>
    /// <param name="typeName">Qualified type name.</param>
    public IReadOnlySet<string>? ClassesRunningType(string typeName) => _classesByType.GetValueOrDefault(typeName);

    /// <summary><paramref name="typeName"/> and every type it is nested in: <c>A.B.C</c> gives <c>A.B.C</c>, <c>A.B</c>, <c>A</c>.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <remarks>Namespace prefixes are included too; they never match a real type name, so they only cost a few entries.</remarks>
    private static IEnumerable<string> EnclosingTypes(string typeName)
    {
        var name = typeName;
        while (true)
        {
            yield return name;
            var dot = name.LastIndexOf('.');
            if (dot < 0)
            {
                yield break;
            }

            name = name[..dot];
        }
    }
}
