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
