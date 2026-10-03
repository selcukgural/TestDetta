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

    /// <summary>Test classes selected from coverage, per test project, with the evidence of their first selection.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _selected = new(Paths.Comparer);

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

            // A map whose recording had failures it cannot name may lack any class's code; unused.
            if (request.CoverageMaps.TryGetValue(test.Project.Path, out var map) && map.FailedClasses is not null)
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
    /// A setup scope (<see cref="CoverageMap.SetUpScopePrefix"/>) is used by every class in its namespace.
    /// </summary>
    /// <param name="index">Indexed source code.</param>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="fixture">Fixture name from the map, such as <c>App.Tests.ApiHost`1[App.Tests.BlogProfile]</c>.</param>
    private static List<string> ClassesUsing(SourceIndex index, string project, string fixture)
    {
        var classes = new List<string>();
        if (fixture.StartsWith(CoverageMap.SetUpScopePrefix, StringComparison.Ordinal))
        {
            // Setup for a namespace serves every class in it and in the namespaces below it.
            var scope = fixture[CoverageMap.SetUpScopePrefix.Length..];
            foreach (var type in index.Types)
            {
                if (type.CanContainTests && Paths.Comparer.Equals(type.ProjectPath, project)
                    && (scope.Length == 0 || type.RuntimeTypeName.StartsWith(scope + ".", StringComparison.Ordinal)))
                {
                    classes.Add(type.RuntimeTypeName);
                }
            }

            return classes;
        }

        var names = FixtureNames(fixture);
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

    /// <summary>
    /// True when a test of <paramref name="type"/> failed while its map was recorded. A failing test
    /// stops early, so the map may lack code the class reaches; the class runs whenever its project is
    /// affected.
    /// </summary>
    /// <param name="type">Indexed test class.</param>
    public bool FailedWhileRecording(IndexedType type) => FailedWhileRecording(type.ProjectPath, type.RuntimeTypeName);

    /// <summary>See <see cref="FailedWhileRecording(IndexedType)"/>, by project and runtime class name.</summary>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name.</param>
    public bool FailedWhileRecording(string project, string testClass) =>
        _maps.TryGetValue(project, out var map) && map.Map.FailedClasses!.Contains(testClass);

    /// <summary>Test classes the map of <paramref name="project"/> recorded running tests; empty without a map.</summary>
    /// <param name="project">Normalized test project path.</param>
    public IEnumerable<string> RecordedClasses(string project) =>
        _maps.TryGetValue(project, out var map) ? map.Map.Classes.Keys : [];

    /// <summary>True when a map of the type's project recorded tests of <paramref name="type"/>.</summary>
    /// <param name="type">Indexed type of a test project.</param>
    public bool Recorded(IndexedType type) =>
        _maps.TryGetValue(type.ProjectPath, out var map) && map.Map.Classes.ContainsKey(type.RuntimeTypeName);

    /// <summary>True when coverage selected <paramref name="type"/>, a test class.</summary>
    /// <param name="type">Indexed test class.</param>
    public bool Selects(IndexedType type) =>
        _selected.TryGetValue(type.ProjectPath, out var classes) && classes.ContainsKey(type.RuntimeTypeName);

    /// <summary>Why coverage selected <paramref name="type"/>: the change it ran and the map's commit; null when it did not.</summary>
    /// <param name="type">Indexed test class.</param>
    public string? Evidence(IndexedType type) => Evidence(type.ProjectPath, type.RuntimeTypeName);

    /// <summary>See <see cref="Evidence(IndexedType)"/>, by project and runtime class name.</summary>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name.</param>
    public string? Evidence(string project, string testClass) =>
        _selected.TryGetValue(project, out var classes) ? classes.GetValueOrDefault(testClass) : null;

    /// <summary>The commit the map of <paramref name="testProject"/> was recorded at; null without a map.</summary>
    /// <param name="testProject">Normalized test project path.</param>
    public string? MapCommit(string testProject) => _maps.TryGetValue(testProject, out var map) ? map.Map.Commit : null;

    /// <summary>
    /// Selects the test classes that ran <paramref name="change"/> in every affected test project with
    /// a map downstream of <paramref name="owner"/>. Returns false when no such project exists, one of
    /// their maps cannot answer, or the change may be run-once code; the caller then traces the change
    /// by name as well, and the classes the maps know stay selected.
    /// Projects without a map are decided by a separate name-only spread.
    /// </summary>
    /// <param name="change">A changed type or member.</param>
    /// <param name="owner">Project that owns the changed file.</param>
    /// <param name="reached">True for a member the name spread reached rather than a change itself; only the evidence differs.</param>
    public bool TryResolve(SymbolChange change, ProjectNode owner, bool reached = false)
    {
        if (_maps.Count == 0)
        {
            return false;
        }

        // A static constructor runs once per process, for whichever class came first, so the map
        // cannot name its users; every class that ran a member of the type used what it set up, often
        // through extension methods that never name the type (MimeKit's HtmlAttributeIdExtensions).
        // The change is still traced by name for classes that only read its static fields.
        if (change.MemberName?.StartsWith(".cctor", StringComparison.Ordinal) == true)
        {
            SelectAll(Answers(change with { MemberName = null }, owner).Answers, $"ran a member of {change.TypeName}, whose static constructor changed");
            return false;
        }

        // An instance serves more classes than the one that built it: a singleton a DI container builds
        // once, between tests or before them, and hands to every class (docs/design/between-tests-code.md).
        // Every class that ran a member of the type used an instance some constructor built.
        // When a map cannot say who ran the type (a member also ran before the first test), the
        // classes it names are not all of them, and the change is traced by name as well.
        var typeUsersKnown = true;
        if (change.MemberName?.StartsWith(".ctor", StringComparison.Ordinal) == true)
        {
            var typeWide = Answers(change with { MemberName = null }, owner);
            SelectAll(typeWide.Answers, $"ran a member of {change.TypeName}, whose constructor changed");
            typeUsersKnown = typeWide.Known;
        }

        if (!IsAnswerable(change, reached))
        {
            return false;
        }

        var (answers, resolved, _) = Answers(change, owner);
        var member = change.MemberName is null ? change.TypeName : $"{change.TypeName}.{change.MemberName}";
        SelectAll(answers, reached ? $"ran {member}, which names the changed code" : $"ran {member}, which changed");
        return resolved && typeUsersKnown && answers.Count > 0;
    }

    /// <summary>
    /// Answers for a type that nothing names, so a framework may create it through reflection, when
    /// the name spread reached it through code only: neither its declaration (which reflection reads)
    /// nor its metadata changed, so it behaves differently only where its code runs. Selects the
    /// classes that ran any of its members in every affected test project with a map downstream of
    /// <paramref name="owners"/>; a type no test ran took part in no test. Returns false, leaving the
    /// project-level fallback, when a map cannot say who ran it (not instrumented, run before the
    /// first test) or it may be run-once code: run inside a static constructor or cache fill, or by a
    /// single bucket per process, as a framework that builds it once and caches the result would
    /// (docs/design/reflection-types-with-coverage.md).
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="owners">Projects that compile the type.</param>
    public bool TryResolveType(string typeName, IReadOnlyCollection<string> owners)
    {
        var answers = new List<(string Project, IReadOnlySet<string> Classes)>();
        foreach (var project in _graph.WithDependents(owners))
        {
            if (!_affectedTests.Contains(project) || !_maps.TryGetValue(project, out var map))
            {
                continue;
            }

            if (map.ClassesRunningType(typeName) is not { } classes
                || map.TypeRanOutsideTests(typeName)
                || map.TypeRanDuringInitialization(typeName)
                || map.TypeHasExternalCallback(typeName)
                || (classes.Count > 0 && (map.BucketsRunningType(typeName) == 1 || map.TypeRanInOneBucketPerProcess(typeName))))
            {
                return false;
            }

            answers.Add((project, classes));
        }

        if (answers.Count == 0)
        {
            return false;
        }

        SelectAll(answers, $"ran a member of {typeName}, which nothing names and the change reaches through its code");
        return true;
    }

    /// <summary>
    /// What the maps of the affected test projects downstream of <paramref name="owner"/> say about
    /// <paramref name="change"/>: the classes each one names; Resolved is false when one of them cannot
    /// answer or the change may be run-once code, Known only when one cannot answer.
    /// </summary>
    /// <param name="change">A changed type or member.</param>
    /// <param name="owner">Project that owns the changed file.</param>
    private (List<(string Project, IReadOnlySet<string> Classes)> Answers, bool Resolved, bool Known) Answers(SymbolChange change, ProjectNode owner)
    {
        var answers = new List<(string Project, IReadOnlySet<string> Classes)>();
        var resolved = true;
        var known = true;
        foreach (var project in _graph.WithDependents([owner.Path]))
        {
            if (!_affectedTests.Contains(project) || !_maps.TryGetValue(project, out var map))
            {
                continue;
            }

            var (classes, complete) = Lookup(map, change);
            if (classes is null)
            {
                resolved = false;
                known = false;
                continue;
            }

            // The classes the map names ran it all the same; the rest are traced by name.
            if (!complete)
            {
                resolved = false;
                known = false;
            }

            // Run-once code is traced by name, but the classes the map saw running it depend on it
            // all the same: name tracing can miss them (through generated code or reflection).
            resolved &= !MayBeRunOnce(map, change);
            answers.Add((project, classes));
        }

        return (answers, resolved, known);
    }

    /// <summary>Adds the classes of every answer to the selection of its project; a class keeps the evidence of its first selection.</summary>
    /// <param name="answers">Classes per test project.</param>
    /// <param name="evidence">What the classes ran, without the map; the map's commit is added.</param>
    private void SelectAll(List<(string Project, IReadOnlySet<string> Classes)> answers, string evidence)
    {
        foreach (var (project, classes) in answers)
        {
            if (!_selected.TryGetValue(project, out var selected))
            {
                selected = new Dictionary<string, string>(StringComparer.Ordinal);
                _selected[project] = selected;
            }

            var withMap = $"{evidence} (map {Short(_maps[project].Map.Commit)})";
            foreach (var testClass in classes)
            {
                selected.TryAdd(testClass, withMap);
            }
        }
    }

    /// <summary>The first 12 characters of a commit.</summary>
    /// <param name="commit">Commit hash.</param>
    internal static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;

    /// <summary>
    /// Changes coverage cannot describe: additions (no test ran them yet), static constructors
    /// (they run once per process, for whichever test comes first), and changes to a type a framework
    /// may discover through reflection: the framework reads its declaration (attributes, members)
    /// without running any of it. A member of such a type that the name spread only reached is
    /// answerable: its declaration is unchanged, so what changed is only what it does when it runs
    /// (docs/design/reflection-types-with-coverage.md).
    /// </summary>
    /// <param name="change">A changed type or member.</param>
    /// <param name="reached">True for a member the name spread reached rather than a change itself.</param>
    private bool IsAnswerable(SymbolChange change, bool reached)
    {
        if (change.Kind == SymbolChangeKind.Added || change.MemberName?.StartsWith(".cctor", StringComparison.Ordinal) == true)
        {
            return false;
        }

        return reached || !MayBeFoundByReflection(change.TypeName);
    }

    /// <summary>True for a type that nothing else names and a framework could discover (it has a base list or attributes).</summary>
    /// <param name="typeName">Qualified type name.</param>
    private bool MayBeFoundByReflection(string typeName) =>
        _index.FindType(typeName) is { MayBeFoundByReflection: true }
        && !_index.IsReferencedOutside(DeclarationWalker.SimpleTypeName(typeName), typeName);

    /// <summary>
    /// True for code that may run once per test process and cache its result, which the map credits
    /// only to the first class that triggered it; the change is then traced by name instead. That is
    /// any member that ran inside a static constructor or a cache fill, and a constructor or static member that ran
    /// in only one test class or fixture per recording process (a lazy catalogue, a singleton).
    /// </summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">A changed member.</param>
    private bool MayBeRunOnce(CoverageIndex map, SymbolChange change)
    {
        // A whole type answered from coverage is only as good as its members: one built by a static
        // initializer was credited to whichever class came first.
        if (change.MemberName is null)
        {
            return map.TypeRanDuringInitialization(change.TypeName) || map.TypeHasExternalCallback(change.TypeName);
        }

        var isConstructor = change.MemberName.StartsWith(".ctor", StringComparison.Ordinal);
        var name = isConstructor ? ".ctor" : DeclarationWalker.ReferenceNames(change.MemberName) is [var single] ? single : null;
        if (name is null)
        {
            return false;
        }

        // A third-party library that calls the member back may have kept what it produced for later
        // classes, which the map does not credit (Castle's proxy generation hook in NSubstitute).
        if (map.RanDuringInitialization(change.TypeName, name) || map.IsExternalCallback(change.TypeName, name))
        {
            return true;
        }

        // A framework that discovers a type may call it once and cache the result for every later class,
        // so any member of such a type counts, not only constructors and static members.
        var isStatic = _index.FindMember(change.TypeName, change.MemberName) is { IsStatic: true };
        // Nothing ran it at all means no test runs it; run-once code is still recorded for its first user.
        return (isConstructor || isStatic || MayBeFoundByReflection(change.TypeName))
               && (map.BucketsRunning(change.TypeName, name) == 1 || map.RanInOneBucketPerProcess(change.TypeName, name));
    }

    /// <summary>
    /// Test classes that ran the changed member (all overloads share a name in the map), or any
    /// member of a changed type, and whether they are all of them. Null classes when the map does not
    /// know the member: fields and constants (constants are inlined at compile time), abstract
    /// members, members of other assemblies, or a member that also ran outside any test. A type
    /// whose members ran outside any test has known users, but not all of them (incomplete).
    /// </summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">A changed type or member.</param>
    private static (IReadOnlySet<string>? Classes, bool Complete) Lookup(CoverageIndex map, SymbolChange change)
    {
        if (change.MemberName is null)
        {
            return (map.ClassesRunningType(change.TypeName), !map.TypeRanOutsideTests(change.TypeName));
        }

        if (change.MemberName.StartsWith(".ctor", StringComparison.Ordinal))
        {
            return (map.ClassesRunning(change.TypeName, ".ctor"), true);
        }

        var names = DeclarationWalker.ReferenceNames(change.MemberName);
        if (names.Count != 1)
        {
            // Operators, indexers and finalizers have no source name to match; field lists are not methods.
            return (null, false);
        }

        return (map.ClassesRunning(change.TypeName, names[0]), true);
    }
}
