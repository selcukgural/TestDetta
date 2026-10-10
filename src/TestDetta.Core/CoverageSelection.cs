using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>
/// Answers "which test classes ran this changed symbol?" from coverage maps, for the changes a map
/// can answer reliably. Everything else is left to name propagation.
/// </summary>
internal sealed partial class CoverageSelection
{
    private readonly ILogger _logger;

    private readonly ProjectGraph _graph;
    private readonly SourceIndex _index;

    /// <summary>Repository root, which map class locations are relative to.</summary>
    private readonly string _repoRoot;

    /// <summary>Runtime identifier of the machine selecting, compared with each map's <see cref="CoverageMap.Platform"/>.</summary>
    private readonly string _platform;

    /// <summary>Matches of unreported candidates with orphans, per test project, computed on first use.</summary>
    private readonly Dictionary<string, RecordedClassMatches> _matches = new(Paths.Comparer);

    /// <summary>Coverage index per test project that has a usable map.</summary>
    private readonly Dictionary<string, CoverageIndex> _maps = new(Paths.Comparer);

    /// <summary>Affected test projects, by path.</summary>
    private readonly HashSet<string> _affectedTests = new(Paths.Comparer);

    /// <summary>
    /// Projects that run completely whatever coverage says (a changed build file, a generator they use).
    /// Their maps have no say in whether coverage can answer: a map that cannot know code which ran in
    /// the compiler sent changes into the name trace for every other project (Mapperly's integration
    /// tests, which use the library as a generator).
    /// </summary>
    private readonly IReadOnlySet<string> _runsCompletely;

    /// <summary>Projects each test project compiles against (itself included), computed on first use.</summary>
    private readonly Dictionary<string, HashSet<string>> _compiledAgainst = new(Paths.Comparer);

    /// <summary>Test classes selected from coverage, per test project, with the evidence of their first selection.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _selected = new(Paths.Comparer);

    /// <summary>Prepares the maps of the affected test projects.</summary>
    /// <param name="request">Selection input, including the maps.</param>
    /// <param name="affectedTests">Test projects affected at project level.</param>
    /// <param name="index">Indexed source code, for the reflection check.</param>
    /// <param name="runsCompletely">Projects that run completely whatever coverage says.</param>
    /// <param name="logger">Receives why coverage could not answer for a change.</param>
    public CoverageSelection(TestSelectionRequest request, List<AffectedProject> affectedTests, SourceIndex index, IReadOnlySet<string> runsCompletely, ILogger logger)
    {
        _logger = logger;
        _runsCompletely = runsCompletely;
        _graph = request.Graph;
        _index = index;
        _repoRoot = request.RepoRoot;
        _platform = request.Platform;
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
        var aliases = Matches(project).Aliases;
        foreach (var testClass in selected)
        {
            // A class the map recorded under another name has its time under that name.
            seconds += timings.ClassSeconds.GetValueOrDefault(aliases.GetValueOrDefault(testClass) ?? testClass);
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
    /// True when a test of the class failed while its map was recorded. A failing test stops early, so
    /// the map may lack code the class reaches; the class runs whenever its project is affected.
    /// </summary>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name as the map recorded it.</param>
    public bool FailedWhileRecording(string project, string testClass) =>
        _maps.TryGetValue(project, out var map) && map.Map.FailedClasses!.Contains(testClass);

    /// <summary>
    /// Test classes the test runner reported while the map of <paramref name="project"/> was recorded:
    /// those with coverage, those that failed (whose coverage may be missing altogether, as for a
    /// framework the tracker cannot follow) and those whose tests were all skipped. Empty without a map.
    /// </summary>
    /// <param name="project">Normalized test project path.</param>
    public IEnumerable<string> ReportedClasses(string project) =>
        _maps.TryGetValue(project, out var map)
            ? map.Map.Classes.Keys.Union(map.Map.FailedClasses ?? Enumerable.Empty<string>()).Union(map.Map.SkippedClasses ?? Enumerable.Empty<string>())
            : [];

    /// <summary>
    /// True when the runner reported <paramref name="type"/> as a test class while its project's map was
    /// recorded (see <see cref="ReportedClasses"/>), whatever attributes the index recognised.
    /// </summary>
    /// <param name="type">Indexed type of a test project.</param>
    public bool Reported(IndexedType type) =>
        _maps.TryGetValue(type.ProjectPath, out var map)
        && (map.Map.Classes.ContainsKey(type.RuntimeTypeName)
            || map.Map.FailedClasses?.Contains(type.RuntimeTypeName) == true
            || map.Map.SkippedClasses?.Contains(type.RuntimeTypeName) == true);

    /// <summary>True when coverage selected <paramref name="type"/>, a test class.</summary>
    /// <param name="type">Indexed test class.</param>
    public bool Selects(IndexedType type) =>
        _selected.TryGetValue(type.ProjectPath, out var classes) && classes.ContainsKey(type.RuntimeTypeName);

    /// <summary>Why coverage selected the class: the change it ran and the map's commit; null when it did not.</summary>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name as the map recorded it.</param>
    public string? Evidence(string project, string testClass) =>
        _selected.TryGetValue(project, out var classes) ? classes.GetValueOrDefault(testClass) : null;

    /// <summary>
    /// The name the map of <paramref name="type"/>'s project recorded the class under: its own name,
    /// or the orphan written at its place when the map never reported the class
    /// (<see cref="RecordedClassMatcher"/>). Coverage, failure and skip are looked up by this name.
    /// </summary>
    /// <param name="type">Indexed test class.</param>
    public string RecordedName(IndexedType type) =>
        Matches(type.ProjectPath).Aliases.GetValueOrDefault(type.RuntimeTypeName) ?? type.RuntimeTypeName;

    /// <summary>
    /// The orphans that keep <paramref name="type"/> from being matched one to one, when the map and
    /// the build disagree about the classes of its file; null when they do not.
    /// </summary>
    /// <param name="type">Indexed test class.</param>
    public IReadOnlyList<string>? Mismatch(IndexedType type) =>
        Matches(type.ProjectPath).Mismatches.GetValueOrDefault(type.RuntimeTypeName);

    /// <summary>
    /// True for a class of the map that the build has under another name. It is decided as that class,
    /// so it is not a candidate of its own: a filter on its name would match nothing.
    /// </summary>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name from the map.</param>
    public bool RecordedUnderAnotherName(string project, string testClass) => Matches(project).PairedOrphans.Contains(testClass);

    /// <summary>True when every test of the class was skipped while the map was recorded.</summary>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name as the map recorded it.</param>
    public bool SkippedWhileRecording(string project, string testClass) =>
        _maps.TryGetValue(project, out var map) && map.Map.SkippedClasses?.Contains(testClass) == true;

    /// <summary>The platform the map of <paramref name="project"/> was recorded on; "an unknown platform" when it does not say.</summary>
    /// <param name="project">Normalized test project path.</param>
    public string MapPlatform(string project) =>
        _maps.TryGetValue(project, out var map) && map.Map.Platform is { } platform ? platform : "an unknown platform";

    /// <summary>The platform selecting, where the selected tests are expected to run.</summary>
    public string Platform => _platform;

    /// <summary>
    /// True when the map of <paramref name="project"/> was recorded on another platform than this one,
    /// or does not say: a test skipped there may run here.
    /// </summary>
    /// <param name="project">Normalized test project path.</param>
    public bool RecordedElsewhere(string project) =>
        _maps.TryGetValue(project, out var map) && !string.Equals(map.Map.Platform, _platform, StringComparison.Ordinal);

    /// <summary>
    /// Matches for <paramref name="project"/>, computed once. Only a map that knows its classes can say
    /// that a class is outside them; the warning for each mismatch is logged here, once per run.
    /// </summary>
    /// <param name="project">Normalized test project path.</param>
    private RecordedClassMatches Matches(string project)
    {
        if (_matches.TryGetValue(project, out var matches))
        {
            return matches;
        }

        matches = RecordedClassMatches.None;
        if (_maps.TryGetValue(project, out var map) && map.Map.KnowsClasses)
        {
            var types = _index.Types.Where(t => t.InTestProject && Paths.Comparer.Equals(t.ProjectPath, project)).ToList();
            matches = RecordedClassMatcher.Match(map.Map, types, _repoRoot);
            foreach (var (candidate, orphan) in matches.Aliases)
            {
                LogRecordedAs(candidate, orphan);
            }

            foreach (var (candidate, orphans) in matches.Mismatches)
            {
                LogMismatch(candidate, Path.GetFileName(project), string.Join(", ", orphans));
            }
        }

        _matches[project] = matches;
        return matches;
    }

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
            return Unanswered(change, "a static constructor runs once per process");
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
            return Unanswered(change, change.Kind == SymbolChangeKind.Added ? "added" : "its type may be found through reflection");
        }

        var answerSet = Answers(change, owner);
        var (answers, unresolved) = answerSet;
        var member = change.MemberName is null ? change.TypeName : $"{change.TypeName}.{change.MemberName}";
        SelectAll(answers, reached ? $"ran {member}, which names the changed code" : $"ran {member}, which changed");
        // Changed metadata is read through reflection without running the code: the classes that ran
        // it stay selected, and the change is traced through its type as well.
        var reason = unresolved
                     ?? (!typeUsersKnown ? "a member of its type ran outside any test"
                         : change.MetadataChanged ? "its metadata changed"
                         : answers.Count == 0 && !answerSet.CoveredByFullRuns ? "no affected test project with a map depends on it"
                         : null);
        return reason is null || Unanswered(change, reason);
    }

    /// <summary>
    /// Selects the test classes that ran a lambda or local function written in the reached member
    /// <paramref name="change"/>, in every affected test project with a map downstream of
    /// <paramref name="owner"/>: the name that reached it appears only in its endpoint handlers, which run
    /// for the requests that reach them, while its own body (the route mapping) runs whenever a host
    /// starts (docs/design/endpoint-handlers.md). Returns false, selecting nothing, when a map cannot
    /// say: recorded before nested methods were kept, without nested methods for the member, or with one
    /// that may be run-once code; the caller then asks about the whole member.
    /// </summary>
    /// <param name="change">A member the name spread reached.</param>
    /// <param name="owner">Project that compiles it.</param>
    public bool TryResolveNested(SymbolChange change, ProjectNode owner)
    {
        if (_maps.Count == 0 || change.MemberName is null)
        {
            return false;
        }

        var answers = new List<(string Project, IReadOnlySet<string> Classes)>();
        var runsCompletely = false;
        foreach (var project in _graph.WithDependents([owner.Path]))
        {
            if (!_affectedTests.Contains(project) || !_maps.TryGetValue(project, out var map))
            {
                continue;
            }

            if (_runsCompletely.Contains(project))
            {
                runsCompletely = true;
                continue;
            }

            if (!Instrumented(project, map, owner.Path))
            {
                return false;
            }

            HashSet<string>? classes = null;
            foreach (var name in DeclarationWalker.CompiledNames(change.MemberName))
            {
                if (!map.Knows(change.TypeName, name))
                {
                    continue;
                }

                if (map.ClassesRunningNested(change.TypeName, name) is not { } ran)
                {
                    return false;
                }

                (classes ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(ran);
            }

            if (classes is null)
            {
                return false;
            }

            answers.Add((project, classes));
        }

        if (answers.Count == 0)
        {
            return runsCompletely;
        }

        SelectAll(answers, $"ran an endpoint handler of {change.TypeName}.{change.MemberName}, which names the changed code");
        return true;
    }

    /// <summary>Logs why coverage cannot answer for <paramref name="change"/>; always false, for the caller to return.</summary>
    /// <param name="change">The change or reached member.</param>
    /// <param name="reason">Why.</param>
    private bool Unanswered(SymbolChange change, string reason)
    {
        LogUnanswered(change.TypeName, change.MemberName ?? "(type)", reason);
        return false;
    }

    /// <summary>
    /// Answers for a type that nothing names, so a framework may create it through reflection, when
    /// the name spread reached it through code only: neither its declaration (which reflection reads)
    /// nor its metadata changed, so it behaves differently only where its code runs. Selects the
    /// classes that ran any of its members in every affected test project with a map downstream of
    /// <paramref name="owners"/>; a type no test ran took part in no test. Returns false, leaving the
    /// project-level fallback, when a map cannot say who ran it (not instrumented, run before the
    /// first test) or it may be run-once code: run inside a static constructor or cache fill, or called
    /// back by a third-party library (docs/design/reflection-types-with-coverage.md).
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="owners">Projects that compile the type.</param>
    /// <param name="unanswered">Why the maps cannot answer, for the fallback's reason; null when they did.</param>
    public bool TryResolveType(string typeName, IReadOnlyCollection<string> owners, out string? unanswered)
    {
        unanswered = null;
        var answers = new List<(string Project, IReadOnlySet<string> Classes)>();
        var runsCompletely = false;
        foreach (var project in _graph.WithDependents(owners))
        {
            if (!_affectedTests.Contains(project) || !_maps.TryGetValue(project, out var map))
            {
                continue;
            }

            if (_runsCompletely.Contains(project))
            {
                runsCompletely = true;
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(project);
            unanswered = owners.Any(o => !Instrumented(project, map, o)) ? $"{name}'s map did not instrument it"
                : map.ClassesRunningType(typeName) is null ? $"{name}'s map does not know it"
                : map.TypeRanOutsideTests(typeName) ? $"a member of it ran outside any test of {name}"
                : map.TypeRanDuringInitialization(typeName) ? $"a member of it ran in a static constructor or cache fill in {name}"
                : map.TypeHasExternalCallback(typeName) ? $"a third-party library calls it back in {name}"
                : null;
            if (unanswered is not null || map.ClassesRunningType(typeName) is not { } classes)
            {
                return false;
            }

            answers.Add((project, classes));
        }

        // Every project with a map below the type runs completely: nothing is left to answer for.
        if (answers.Count == 0)
        {
            unanswered = runsCompletely ? null : "no affected test project with a map is below it";
            return runsCompletely;
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
    private AnswerSet Answers(SymbolChange change, ProjectNode owner)
    {
        var answers = new List<(string Project, IReadOnlySet<string> Classes)>();
        string? unresolved = null;
        var known = true;
        var runsCompletely = false;
        foreach (var project in _graph.WithDependents([owner.Path]))
        {
            if (!_affectedTests.Contains(project) || !_maps.TryGetValue(project, out var map))
            {
                continue;
            }

            if (_runsCompletely.Contains(project))
            {
                runsCompletely = true;
                continue;
            }

            // The test project compiles the owner's assembly, but its recording ran that assembly uninstrumented: the
            // names the map knows belong to another assembly (a linked file compiled into two), so it cannot answer.
            if (!Instrumented(project, map, owner.Path))
            {
                unresolved ??= $"a map does not know it ({Path.GetFileName(project)} was recorded without instrumenting {Path.GetFileName(owner.Path)})";
                known = false;
                continue;
            }

            var (classes, complete) = Lookup(map, change);
            if (classes is null)
            {
                // Two different gaps, worth telling apart when a selection is questioned: code the map
                // never instrumented (fields, constants, abstract members, other assemblies) and code
                // that also ran before or between tests.
                unresolved ??= Unobserved(map, change) ? "the incremental map has not seen its users since it changed"
                    : RanOutsideTests(map, change) ? "it ran outside any test"
                    : "a map does not know it";
                known = false;
                continue;
            }

            // The classes the map names ran it all the same; the rest are traced by name.
            if (!complete)
            {
                unresolved ??= change.MemberName is null ? "a member of its type ran outside any test"
                    : Unobserved(map, change) ? "the incremental map has not seen its users since it changed"
                    : "it ran outside any test";
                known = false;
            }

            // Run-once code is traced by name, but the classes the map saw running it depend on it
            // all the same: name tracing can miss them (through generated code or reflection).
            unresolved ??= RunOnceReason(map, change);

            // An inline function's body is compiled into its callers, which the map credits instead.
            if (change.Inlined)
            {
                unresolved ??= "it is inlined into its callers";
            }

            answers.Add((project, classes));
        }

        return new AnswerSet(answers, unresolved, known) { CoveredByFullRuns = runsCompletely };
    }

    /// <summary>What the maps say about one change.</summary>
    /// <param name="Answers">Classes per affected test project with a map.</param>
    /// <param name="Unresolved">Why a map cannot answer completely, or null when they all can.</param>
    /// <param name="Known">False when a map does not know who ran it at all (as opposed to run-once code).</param>
    private sealed record AnswerSet(List<(string Project, IReadOnlySet<string> Classes)> Answers, string? Unresolved, bool Known)
    {
        /// <summary>True when a project with a map below the change was left out because it runs completely anyway.</summary>
        public bool CoveredByFullRuns { get; init; }

        /// <summary>Deconstructs into the answers and the reason they are incomplete.</summary>
        /// <param name="answers">Classes per project.</param>
        /// <param name="unresolved">Why incomplete, or null.</param>
        public void Deconstruct(out List<(string Project, IReadOnlySet<string> Classes)> answers, out string? unresolved)
        {
            answers = Answers;
            unresolved = Unresolved;
        }
    }

    /// <summary>
    /// False when the map of <paramref name="testProject"/> lists the projects its recording instrumented
    /// (<see cref="CoverageMap.InstrumentedProjects"/>) and <paramref name="owner"/> is one the test project compiles
    /// against but not among them; true otherwise, also for maps without the list, which answer as before. A project
    /// the test project reaches only through its output was never instrumented and is decided by its output uses.
    /// </summary>
    /// <param name="testProject">Normalized test project path.</param>
    /// <param name="map">Its map.</param>
    /// <param name="owner">Normalized path of the project that compiles the changed code.</param>
    private bool Instrumented(string testProject, CoverageIndex map, string owner)
    {
        if (map.Map.InstrumentedProjects is not { } instrumented || !CompiledAgainst(testProject).Contains(owner))
        {
            return true;
        }

        var relative = Path.GetRelativePath(_repoRoot, owner).Replace('\\', '/');
        return instrumented.Contains(relative, Paths.Comparer);
    }

    /// <summary>The projects <paramref name="testProject"/> compiles against: its references, except those it does not compile against, transitively.</summary>
    /// <param name="testProject">Normalized test project path.</param>
    private HashSet<string> CompiledAgainst(string testProject)
    {
        if (_compiledAgainst.TryGetValue(testProject, out var closure))
        {
            return closure;
        }

        closure = new HashSet<string>(Paths.Comparer) { testProject };
        var queue = new Queue<string>([testProject]);
        while (queue.TryDequeue(out var path))
        {
            if (_graph.Find(path) is not { } project)
            {
                continue;
            }

            foreach (var reference in project.References)
            {
                if (!project.OpaqueReferences.Contains(reference) && closure.Add(reference))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        _compiledAgainst[testProject] = closure;
        return closure;
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
    /// Why the change may be code that runs once per test process and caches its result, which the map
    /// credits only to the first class that triggered it (the change is then traced by name as well);
    /// null when it is not. That is any member that ran inside a static constructor or a cache fill or
    /// is called back by a third-party library. A lazy catalogue or singleton stored into a field is
    /// answered through the values it built, credited to every class that read the field.
    /// </summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">A changed member.</param>
    private static string? RunOnceReason(CoverageIndex map, SymbolChange change)
    {
        // A whole type answered from coverage is only as good as its members: one built by a static
        // initializer was credited to whichever class came first.
        if (change.MemberName is null)
        {
            return map.TypeRanDuringInitialization(change.TypeName) ? "a member of the type ran in a static constructor or cache fill"
                : map.TypeHasExternalCallback(change.TypeName) ? "a member of the type is a third-party callback"
                : null;
        }

        foreach (var name in DeclarationWalker.CompiledNames(change.MemberName))
        {
            if (map.RanDuringInitialization(change.TypeName, name))
            {
                return "it ran in a static constructor or cache fill";
            }

            // A third-party library that calls the member back may have kept what it produced for later
            // classes, which the map does not credit (Castle's proxy generation hook in NSubstitute).
            if (map.IsExternalCallback(change.TypeName, name))
            {
                return "a third-party library calls it back";
            }
        }

        return null;
    }

    /// <summary>
    /// Test classes that ran the changed member (all overloads share a name in the map), or any
    /// member of a changed type, and whether they are all of them. Null classes when the map does not
    /// know the member: fields and constants (constants are inlined at compile time), abstract
    /// members, members of other assemblies. A member, or a type with a member, that also ran outside
    /// any test has known users, but not all of them (incomplete).
    /// </summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">A changed type or member.</param>
    private static (IReadOnlySet<string>? Classes, bool Complete) Lookup(CoverageIndex map, SymbolChange change)
    {
        if (change.MemberName is null)
        {
            return (map.ClassesRunningType(change.TypeName), !map.TypeRanOutsideTests(change.TypeName));
        }

        // Operators are recorded under their metadata names (op_Addition); the checked form of one may
        // not exist, which is fine, but a name the map knows and cannot answer for makes the whole unknown.
        HashSet<string>? classes = null;
        var complete = true;
        foreach (var name in DeclarationWalker.CompiledNames(change.MemberName))
        {
            if (!map.Knows(change.TypeName, name))
            {
                continue;
            }

            // A member that also ran outside any test has users the map cannot name, so the change is traced by name
            // as well; the classes the map saw running it are users all the same. A framework that finds the member by
            // reflection (FsCheck's default arbitraries) leaves the name trace nothing to follow, and only they remain.
            if (map.ClassesRunning(change.TypeName, name) is not { } ran)
            {
                complete = false;
                ran = map.ClassesSeenRunning(change.TypeName, name);
            }

            classes ??= new HashSet<string>(StringComparer.Ordinal);
            classes.UnionWith(ran);
        }

        return (classes, classes is not null && complete);
    }

    /// <summary>True when the change is to a member, or a type with a member, that an incremental map holds as unobserved.</summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">The change coverage could not answer for.</param>
    private static bool Unobserved(CoverageIndex map, SymbolChange change) =>
        change.MemberName is null
            ? map.TypeUnobserved(change.TypeName)
            : DeclarationWalker.CompiledNames(change.MemberName).Any(name => map.IsUnobserved(change.TypeName, name));

    /// <summary>True when an overload of the member, or for a type any member of it, ran outside any test.</summary>
    /// <param name="map">Coverage of one test project.</param>
    /// <param name="change">A changed type or member.</param>
    private static bool RanOutsideTests(CoverageIndex map, SymbolChange change) =>
        change.MemberName is null
            ? map.TypeRanOutsideTests(change.TypeName)
            : DeclarationWalker.CompiledNames(change.MemberName).Any(name => map.Knows(change.TypeName, name) && map.ClassesRunning(change.TypeName, name) is null);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TestClass} is not in its coverage map by name; the map recorded it as {RecordedAs}, at the same place")]
    private partial void LogRecordedAs(string testClass, string recordedAs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{TestClass} is not in the coverage map of {Project}, which recorded classes at its place or in its file that the build no longer has ({Orphans}); it is selected. Record the map again")]
    private partial void LogMismatch(string testClass, string project, string orphans);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: coverage cannot answer alone: {Reason}")]
    private partial void LogUnanswered(string typeName, string memberName, string reason);
}
