namespace TestDetta.Core;

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

    /// <summary>Members with an overload that ran inside a static constructor or a cache fill.</summary>
    private readonly HashSet<CoveredMethod> _initialization = [];

    /// <summary>Types (and the types they are nested in) with a member in <see cref="_initialization"/>.</summary>
    private readonly HashSet<string> _initializationTypes = new(StringComparer.Ordinal);

    /// <summary>Types (and the types they are nested in) with a member in <see cref="_ambient"/>.</summary>
    private readonly HashSet<string> _ambientTypes = new(StringComparer.Ordinal);

    /// <summary>Members a third-party library calls back (<see cref="CoverageMap.ExternalCallbacks"/>).</summary>
    private readonly HashSet<CoveredMethod> _externalCallbacks = [];

    /// <summary>Types (and the types they are nested in) with a member in <see cref="_externalCallbacks"/>.</summary>
    private readonly HashSet<string> _externalCallbackTypes = new(StringComparer.Ordinal);

    /// <summary>Members an incremental map holds as unobserved (<see cref="CoverageMap.Unobserved"/>).</summary>
    private readonly HashSet<CoveredMethod> _unobserved = [];

    /// <summary>Types (and the types they are nested in) with a member in <see cref="_unobserved"/>.</summary>
    private readonly HashSet<string> _unobservedTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Ids of the methods compiled from lambdas and local functions (<see cref="CoverageMap.NestedMethods"/>);
    /// null for a map recorded before they were kept.
    /// </summary>
    private readonly HashSet<int>? _nestedIds;

    /// <summary>Test classes per member, counting only the member's nested methods; a member with none is absent.</summary>
    private readonly Dictionary<CoveredMethod, HashSet<string>> _nestedClassesByMember = [];

    /// <summary>Members with a nested method that ran outside any test, inside a static constructor or cache fill, or is a third-party callback.</summary>
    private readonly HashSet<CoveredMethod> _nestedUnanswerable = [];

    /// <summary>Builds the lookups; members that no test ran are still known, with no classes.</summary>
    /// <param name="map">Recorded map.</param>
    /// <param name="classesUsingFixture">
    /// Test classes (runtime names) that use a fixture type. A fixture nobody is known to use makes
    /// its methods unanswerable, like code that ran outside every test.
    /// </param>
    public CoverageIndex(CoverageMap map, Func<string, IReadOnlyCollection<string>> classesUsingFixture)
    {
        Map = map;
        _nestedIds = map.NestedMethods is { } nestedIds ? [.. nestedIds] : null;
        foreach (var method in map.Methods)
        {
            _classesByMember.TryAdd(method, []);
            foreach (var type in EnclosingTypes(method.TypeName))
            {
                _classesByType.TryAdd(type, []);
            }
        }

        foreach (var id in _nestedIds ?? [])
        {
            _nestedClassesByMember.TryAdd(map.Methods[id], []);
        }

        foreach (var (testClass, ids) in map.Classes)
        {
            foreach (var id in ids)
            {
                var method = map.Methods[id];
                AddNested(id, method, [testClass]);
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
                if (users.Count == 0)
                {
                    _ambient.Add(method);
                    MarkNestedUnanswerable(id, method);
                    continue;
                }

                AddNested(id, method, users);
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
            MarkNestedUnanswerable(id, map.Methods[id]);
        }

        // Code that built a value a bucket read counts for that bucket, without counting as a bucket
        // that ran it: the run-once rules below still see only where the code ran.
        if (map.BuiltValues is { } built)
        {
            foreach (var (testClass, ids) in built.Classes)
            {
                AddUsers(map, ids, [testClass]);
            }

            foreach (var (fixture, ids) in built.Fixtures)
            {
                var users = classesUsingFixture(fixture);
                if (users.Count == 0)
                {
                    _ambient.UnionWith(ids.Select(id => map.Methods[id]));
                    foreach (var id in ids)
                    {
                        MarkNestedUnanswerable(id, map.Methods[id]);
                    }

                    continue;
                }

                AddUsers(map, ids, users);
            }

            _ambient.UnionWith(built.Ambient.Select(id => map.Methods[id]));
            foreach (var id in built.Ambient)
            {
                MarkNestedUnanswerable(id, map.Methods[id]);
            }
        }

        // Initialization whose result was stored into a tracked field is answered through the values it
        // built (builtValues, merged above): it is not run-once evidence (docs/design/run-once-cascades.md, C).
        var linked = map.LinkedInitialization is { } linkedIds ? new HashSet<int>(linkedIds) : [];
        foreach (var id in map.Initialization)
        {
            if (linked.Contains(id))
            {
                continue;
            }

            _initialization.Add(map.Methods[id]);
            MarkNestedUnanswerable(id, map.Methods[id]);
            foreach (var type in EnclosingTypes(map.Methods[id].TypeName))
            {
                _initializationTypes.Add(type);
            }
        }

        // A member no recording at the map's commit could name the users of (an incremental map's unobserved members)
        // is answered like code that ran outside every test: by name (docs/design/incremental-recording.md).
        foreach (var id in map.Unobserved ?? [])
        {
            _ambient.Add(map.Methods[id]);
            _unobserved.Add(map.Methods[id]);
            _unobservedTypes.UnionWith(EnclosingTypes(map.Methods[id].TypeName));
            MarkNestedUnanswerable(id, map.Methods[id]);
        }

        foreach (var method in _ambient)
        {
            _ambientTypes.UnionWith(EnclosingTypes(method.TypeName));
        }

        foreach (var id in map.ExternalCallbacks ?? [])
        {
            MarkNestedUnanswerable(id, map.Methods[id]);
            _externalCallbacks.Add(map.Methods[id]);
            _externalCallbackTypes.UnionWith(EnclosingTypes(map.Methods[id].TypeName));
        }
    }

    /// <summary>
    /// True when an overload of the member is called back by a third-party library, which may keep
    /// what it produced for every later caller (see <see cref="CoverageMap.ExternalCallbacks"/>).
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name.</param>
    public bool IsExternalCallback(string typeName, string member) => _externalCallbacks.Contains(new CoveredMethod(typeName, member));

    /// <summary>True when a member of <paramref name="typeName"/> or a type nested in it is called back by a third-party library.</summary>
    /// <param name="typeName">Qualified type name.</param>
    public bool TypeHasExternalCallback(string typeName) => _externalCallbackTypes.Contains(typeName);

    /// <summary>Credits methods to test classes without counting buckets.</summary>
    /// <param name="map">Recorded map.</param>
    /// <param name="ids">Method ids.</param>
    /// <param name="classes">Test classes to credit.</param>
    private void AddUsers(CoverageMap map, IReadOnlyList<int> ids, IReadOnlyCollection<string> classes)
    {
        foreach (var id in ids)
        {
            var method = map.Methods[id];
            AddNested(id, method, classes);
            _classesByMember[method].UnionWith(classes);
            foreach (var type in EnclosingTypes(method.TypeName))
            {
                _classesByType[type].UnionWith(classes);
            }
        }
    }

    /// <summary>Credits a nested method's classes to its member's nested view; other methods are left out.</summary>
    /// <param name="id">Method id.</param>
    /// <param name="method">Its member.</param>
    /// <param name="classes">Test classes that ran it.</param>
    private void AddNested(int id, CoveredMethod method, IReadOnlyCollection<string> classes)
    {
        if (_nestedIds?.Contains(id) == true)
        {
            _nestedClassesByMember[method].UnionWith(classes);
        }
    }

    /// <summary>Marks the member of a nested method that ran where its users are unknown, or may be run-once code.</summary>
    /// <param name="id">Method id.</param>
    /// <param name="method">Its member.</param>
    private void MarkNestedUnanswerable(int id, CoveredMethod method)
    {
        if (_nestedIds?.Contains(id) == true)
        {
            _nestedUnanswerable.Add(method);
        }
    }

    /// <summary>
    /// Test classes that ran a lambda or local function written in <paramref name="member"/> of
    /// <paramref name="typeName"/>, whatever ran its own body; null when the map cannot say: it was
    /// recorded before nested methods were kept, the member has none, or one of them ran outside any
    /// test, inside a static constructor or cache fill, or is called back by a third-party library.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name as in <see cref="CoveredMethod.MemberName"/>.</param>
    public IReadOnlySet<string>? ClassesRunningNested(string typeName, string member)
    {
        var key = new CoveredMethod(typeName, member);
        if (_nestedIds is null || _nestedUnanswerable.Contains(key))
        {
            return null;
        }

        return _nestedClassesByMember.GetValueOrDefault(key);
    }

    /// <summary>True when some member of <paramref name="typeName"/> or a type nested in it ran inside a static constructor or a cache fill.</summary>
    /// <param name="typeName">Qualified type name.</param>
    public bool TypeRanDuringInitialization(string typeName) => _initializationTypes.Contains(typeName);

    /// <summary>True when an overload of the member ran inside a static constructor or a cache fill.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name, <c>.ctor</c> for constructors.</param>
    public bool RanDuringInitialization(string typeName, string member) =>
        _initialization.Contains(new CoveredMethod(typeName, member));

    /// <summary>True when the map lists <paramref name="member"/> of <paramref name="typeName"/> (run or not).</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name.</param>
    public bool Knows(string typeName, string member) => _classesByMember.ContainsKey(new CoveredMethod(typeName, member));

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
    /// Test classes that ran any member of <paramref name="typeName"/> or its nested types; null when
    /// none was instrumented. Not all of them when <see cref="TypeRanOutsideTests"/>.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    public IReadOnlySet<string>? ClassesRunningType(string typeName) => _classesByType.GetValueOrDefault(typeName);

    /// <summary>
    /// True when a member of <paramref name="typeName"/> or its nested types also ran outside any test
    /// (before the first test, or in a fixture no class is known to use): the classes that used what
    /// it did there are unknown.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    public bool TypeRanOutsideTests(string typeName) => _ambientTypes.Contains(typeName);

    /// <summary>True when an overload of the member is held as unobserved by an incremental map (<see cref="CoverageMap.Unobserved"/>).</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="member">Simple member name.</param>
    public bool IsUnobserved(string typeName, string member) => _unobserved.Contains(new CoveredMethod(typeName, member));

    /// <summary>True when a member of <paramref name="typeName"/> or a type nested in it is held as unobserved by an incremental map.</summary>
    /// <param name="typeName">Qualified type name.</param>
    public bool TypeUnobserved(string typeName) => _unobservedTypes.Contains(typeName);

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
