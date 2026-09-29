namespace Sift.Core;

/// <summary>
/// Answers "which test classes ran this changed symbol?" from coverage maps, for the changes a map
/// can answer reliably. Everything else is left to name propagation.
/// </summary>
internal sealed class CoverageSelection
{
    private readonly ProjectGraph _graph;
    private readonly SourceIndex _index;

    /// <summary>Coverage index per test project that has a usable map.</summary>
    private readonly Dictionary<string, CoverageIndex> _maps = new(Paths.Comparer);

    /// <summary>Affected test projects, by path.</summary>
    private readonly HashSet<string> _affectedTests = new(Paths.Comparer);

    /// <summary>Test classes selected from coverage, per test project.</summary>
    private readonly Dictionary<string, HashSet<string>> _selected = new(Paths.Comparer);

    /// <summary>Prepares the maps of the affected test projects.</summary>
    /// <param name="request">Selection input, including the maps.</param>
    /// <param name="affectedTests">Test projects affected at project level.</param>
    /// <param name="index">Indexed source code, for the reflection check.</param>
    public CoverageSelection(TestSelectionRequest request, List<AffectedProject> affectedTests, SourceIndex index)
    {
        _graph = request.Graph;
        _index = index;
        foreach (var test in affectedTests)
        {
            _affectedTests.Add(test.Project.Path);
            if (request.CoverageMaps.TryGetValue(test.Project.Path, out var map))
            {
                var project = test.Project.Path;
                _maps[project] = new CoverageIndex(map, fixture => ClassesUsing(index, project, fixture));
            }
        }
    }

    /// <summary>
    /// Test classes of <paramref name="project"/> whose code names the fixture type and each of its
    /// generic arguments, typically in a constructor parameter or base list:
    /// <c>ApiHost`1[BlogProfile]</c> is used by classes that name both <c>ApiHost</c> and <c>BlogProfile</c>.
    /// </summary>
    /// <param name="index">Indexed source code.</param>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="fixture">Fixture name from the map, such as <c>App.Tests.ApiHost`1[App.Tests.BlogProfile]</c>.</param>
    private static List<string> ClassesUsing(SourceIndex index, string project, string fixture)
    {
        var names = FixtureNames(fixture);
        var classes = new List<string>();
        foreach (var type in index.Types)
        {
            if (!type.CanContainTests || !Paths.Comparer.Equals(type.ProjectPath, project))
            {
                continue;
            }

            if (names.TrueForAll(name => index.TypeMentions(type.TypeName, name)))
            {
                classes.Add(type.RuntimeTypeName);
            }
        }

        return classes;
    }

    /// <summary>Simple names of a fixture type and its generic arguments: <c>A.Host`1[A.Profile]</c> gives <c>Host</c>, <c>Profile</c>.</summary>
    /// <param name="fixture">Fixture name from the map.</param>
    private static List<string> FixtureNames(string fixture)
    {
        var names = new List<string>();
        foreach (var part in fixture.Split(['[', ']', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            names.Add(DeclarationWalker.SimpleTypeName(part.Replace('+', '.')));
        }

        return names;
    }

    /// <summary>
    /// Recorded time of <paramref name="selection"/>'s project and of what it selected: the selected
    /// classes plus the setup of every fixture one of them uses. Null without a map with timings.
    /// </summary>
    /// <param name="selection">Decision for an affected test project.</param>
    public TimeEstimate? Estimate(TestProjectSelection selection)
    {
        var project = selection.Project.Path;
        if (!_maps.TryGetValue(project, out var map) || map.Map.Timings is not { } timings)
        {
            return null;
        }

        // Start-up is paid by any run that runs a test, so it only disappears when the project is skipped.
        var full = timings.StartupSeconds + timings.ClassSeconds.Values.Sum() + timings.FixtureSeconds.Values.Sum();
        var selected = selection.Mode switch
        {
            TestRunMode.All => full,
            TestRunMode.Skipped => 0,
            _ => timings.StartupSeconds + SelectedSeconds(timings, project, selection.TestClasses),
        };

        return new TimeEstimate(full, selected);
    }

    /// <summary>Seconds of the given classes and of the fixtures they use.</summary>
    /// <param name="timings">Recorded timings.</param>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="classes">Selected test classes (runtime names).</param>
    private double SelectedSeconds(CoverageTimings timings, string project, IReadOnlyList<string> classes)
    {
        var selected = new HashSet<string>(classes, StringComparer.Ordinal);
        var seconds = 0d;
        foreach (var testClass in selected)
        {
            seconds += timings.ClassSeconds.GetValueOrDefault(testClass);
        }

        foreach (var (fixture, fixtureSeconds) in timings.FixtureSeconds)
        {
            if (ClassesUsing(_index, project, fixture).Exists(selected.Contains))
            {
                seconds += fixtureSeconds;
            }
        }

        return seconds;
    }

    /// <summary>True when at least one affected test project has a coverage map in use.</summary>
    public bool HasAnyMap => _maps.Count > 0;

    /// <summary>True when <paramref name="testProject"/> has a coverage map in use.</summary>
    /// <param name="testProject">Normalized test project path.</param>
    public bool HasMap(string testProject) => _maps.ContainsKey(testProject);

    /// <summary>True when coverage selected <paramref name="type"/>, a test class.</summary>
    /// <param name="type">Indexed test class.</param>
    public bool Selects(IndexedType type) =>
        _selected.TryGetValue(type.ProjectPath, out var classes) && classes.Contains(type.RuntimeTypeName);

    /// <summary>
    /// Selects the test classes that ran <paramref name="change"/> in every affected test project with
    /// a map downstream of <paramref name="owner"/>. Returns false, selecting nothing, when no such
    /// project exists or one of their maps cannot answer; the caller then traces the change by name.
    /// Projects without a map are decided by a separate name-only spread.
    /// </summary>
    /// <param name="change">A changed type or member.</param>
    /// <param name="owner">Project that owns the changed file.</param>
    public bool TryResolve(SymbolChange change, ProjectNode owner)
    {
        if (_maps.Count == 0 || !IsAnswerable(change))
        {
            return false;
        }

        var answers = new List<(string Project, IReadOnlySet<string> Classes)>();
        foreach (var project in _graph.WithDependents([owner.Path]))
        {
            if (!_affectedTests.Contains(project) || !_maps.TryGetValue(project, out var map))
            {
                continue;
            }

            if (Lookup(map, change) is not { } classes || MayBeRunOnce(map, change))
            {
                return false;
            }

            answers.Add((project, classes));
        }

        if (answers.Count == 0)
        {
            return false;
        }

        foreach (var (project, classes) in answers)
        {
            if (!_selected.TryGetValue(project, out var selected))
            {
                selected = new HashSet<string>(StringComparer.Ordinal);
                _selected[project] = selected;
            }

            selected.UnionWith(classes);
        }

        return true;
    }

    /// <summary>
    /// Changes coverage cannot describe: additions (no test ran them yet), static constructors
    /// (they run once per process, for whichever test comes first), and types a framework may
    /// discover through reflection, whose effect often comes from a cached result.
    /// </summary>
    /// <param name="change">A changed type or member.</param>
    private bool IsAnswerable(SymbolChange change)
    {
        if (change.Kind == SymbolChangeKind.Added || change.MemberName?.StartsWith(".cctor", StringComparison.Ordinal) == true)
        {
            return false;
        }

        var type = _index.FindType(change.TypeName);
        return type is null
               || !type.MayBeFoundByReflection
               || _index.IsReferencedOutside(DeclarationWalker.SimpleTypeName(change.TypeName), change.TypeName);
    }

    /// <summary>
    /// True for a constructor or static member that exactly one test class or fixture ran. Such code
    /// often runs once per test process and caches its result (a static catalogue, a singleton), so
    /// the map credits only the first class that triggered it; the change is traced by name instead.
    /// </summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">A changed member.</param>
    private bool MayBeRunOnce(CoverageIndex map, SymbolChange change)
    {
        if (change.MemberName is null)
        {
            return false;
        }

        var isConstructor = change.MemberName.StartsWith(".ctor", StringComparison.Ordinal);
        var name = isConstructor ? ".ctor" : DeclarationWalker.ReferenceNames(change.MemberName) is [var single] ? single : null;
        if (name is null)
        {
            return false;
        }

        var isStatic = _index.FindMember(change.TypeName, change.MemberName) is { IsStatic: true };
        // Nothing ran it at all means no test runs it; run-once code is still recorded for its first user.
        return (isConstructor || isStatic) && map.BucketsRunning(change.TypeName, name) == 1;
    }

    /// <summary>
    /// Test classes that ran the changed member (all overloads share a name in the map), or any
    /// member of a changed type. Null when the map does not know the member: fields and constants
    /// (constants are inlined at compile time), abstract members, members of other assemblies.
    /// </summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">A changed type or member.</param>
    private static IReadOnlySet<string>? Lookup(CoverageIndex map, SymbolChange change)
    {
        if (change.MemberName is null)
        {
            return map.ClassesRunningType(change.TypeName);
        }

        if (change.MemberName.StartsWith(".ctor", StringComparison.Ordinal))
        {
            return map.ClassesRunning(change.TypeName, ".ctor");
        }

        var names = DeclarationWalker.ReferenceNames(change.MemberName);
        if (names.Count != 1)
        {
            // Operators, indexers and finalizers have no source name to match; field lists are not methods.
            return null;
        }

        return map.ClassesRunning(change.TypeName, names[0]);
    }
}
