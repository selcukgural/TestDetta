namespace TestDetta.Core;

/// <summary>A method as the coverage map names it, in the same terms as <see cref="SymbolChange"/>.</summary>
/// <param name="TypeName">Qualified type name, nested types joined with '.', generic arity as <c>`n</c>.</param>
/// <param name="MemberName">
/// Simple member name: <c>Place</c> for a method (all overloads share it), <c>Total</c> for a
/// property, <c>.ctor</c>/<c>.cctor</c> for constructors, <c>op_Addition</c> for operators.
/// Lambdas, local functions, iterators and async state machines count as the member that contains them.
/// </param>
public sealed record CoveredMethod(string TypeName, string MemberName);

/// <summary>Where and when a coverage map was recorded.</summary>
/// <param name="TestProject">Test project path relative to the repository root.</param>
/// <param name="Commit">Commit the map was recorded at.</param>
/// <param name="RecordedAt">When the map was recorded.</param>
public sealed record CoverageMapHeader(string TestProject, string Commit, DateTimeOffset RecordedAt);

/// <summary>Which methods ran, as indexes into <see cref="CoverageMap.Methods"/>.</summary>
/// <param name="Classes">Test class (runtime name) to the methods its tests ran.</param>
/// <param name="Fixtures">
/// Fixture type (runtime name of the generic definition, such as <c>App.Tests.ApiHost`1</c>) to
/// the methods its setup ran. Selection credits these to every test class that uses the fixture.
/// </param>
/// <param name="Ambient">Methods that ran before any test or fixture, such as discovery and theory data.</param>
public sealed record CoverageHits(
    IReadOnlyDictionary<string, IReadOnlyList<int>> Classes,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Fixtures,
    IReadOnlyList<int> Ambient);

/// <summary>
/// Files the tests read and directories they listed while recording (docs/design/runtime-file-reads.md):
/// repository-relative paths with '/' separators, a directory ending in '/'. A read of a copy in a
/// build output stands for the repository file it was copied from.
/// </summary>
/// <param name="Classes">Test class (runtime name) to what its tests read.</param>
/// <param name="Fixtures">Fixture name to what its setup read; credited to every class that uses it.</param>
/// <param name="Ambient">Read outside every test and fixture: every test of the project may depend on it.</param>
/// <param name="Initialization">Read inside an initialization scope (a static constructor, a cache fill), once for every later user.</param>
public sealed record FileReads(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Classes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fixtures,
    IReadOnlyList<string> Ambient,
    IReadOnlyList<string> Initialization);

/// <summary>
/// Time spent in each test class and fixture while recording. Recording runs one test at a time,
/// instrumented, so the absolute numbers are higher than a normal run; the proportions are what
/// estimates rely on.
/// </summary>
/// <param name="ClassSeconds">Test class (runtime name) to seconds.</param>
/// <param name="FixtureSeconds">Fixture name to seconds spent setting it up.</param>
/// <param name="StartupSeconds">
/// Seconds a test process spent before its first test class or fixture (host start-up, discovery),
/// averaged over the recording's processes; every run that runs any test pays it.
/// </param>
public sealed record CoverageTimings(
    IReadOnlyDictionary<string, double> ClassSeconds,
    IReadOnlyDictionary<string, double> FixtureSeconds,
    double StartupSeconds = 0);

/// <summary>Which methods each test class and fixture of one test project ran, recorded by <c>td record</c>.</summary>
/// <param name="Header">Where and when it was recorded.</param>
/// <param name="Methods">
/// Every instrumented method, run or not; a method missing here is unknown, a method listed here
/// that nothing ran is known to be untested.
/// </param>
/// <param name="Hits">Methods run per test class and fixture.</param>
/// <param name="Timings">Time per test class and fixture; null for maps recorded before timings existed.</param>
public sealed record CoverageMap(
    CoverageMapHeader Header,
    IReadOnlyList<CoveredMethod> Methods,
    CoverageHits Hits,
    CoverageTimings? Timings = null)
{
    /// <summary>
    /// Prefix of a fixture name that stands for setup scoped to a namespace rather than a type: NUnit's
    /// <c>[SetUpFixture]</c> (the namespace follows the prefix) and MSTest's <c>[AssemblyInitialize]</c>
    /// (nothing follows: the whole assembly). Every test class in the scope depends on its code.
    /// </summary>
    public const string SetUpScopePrefix = "[setup-scope]";

    /// <summary>
    /// Before a fixture name that stands for a test data member several classes use (<c>[ClassData]</c> or <c>[MemberData]</c>
    /// of a shared type or an abstract base): its users are in <see cref="DataSourceUsers"/>, not found by name.
    /// </summary>
    public const string DataSourcePrefix = "[data-source]";

    /// <summary>Test project path relative to the repository root.</summary>
    public string TestProject => Header.TestProject;

    /// <summary>Commit the map was recorded at.</summary>
    public string Commit => Header.Commit;

    /// <summary>Test class to the methods its tests ran.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Classes => Hits.Classes;

    /// <summary>Fixture type to the methods its setup ran.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Fixtures => Hits.Fixtures;

    /// <summary>Methods that ran before any test or fixture.</summary>
    public IReadOnlyList<int> Ambient => Hits.Ambient;

    /// <summary>
    /// Test classes (runtime names) with a test that failed while recording. A failing test stops
    /// early, so its class may reach more code than the map shows; selection runs these classes
    /// whenever their project is affected. Null when tests failed but the recording could not tell
    /// which (no TRX report): then no class of the map can be trusted.
    /// </summary>
    public IReadOnlySet<string>? FailedClasses { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Methods (indexes into <see cref="Methods"/>) that ran while a value was initialized for reuse:
    /// inside a static constructor or a cache-filling call such as <c>ConcurrentDictionary.GetOrAdd</c>.
    /// Such code runs once per process, so only the first test class to need the value is credited
    /// with it; every user of the value depends on it.
    /// </summary>
    public IReadOnlyList<int> Initialization { get; init; } = [];

    /// <summary>
    /// The part of <see cref="Initialization"/> whose every run was in a linked scope: what it built was
    /// stored into a tracked field, so <see cref="BuiltValues"/> already credits every bucket that uses
    /// it, and it needs no run-once treatment (docs/design/run-once-cascades.md, C). Null for maps
    /// recorded before this was kept, which counts as none linked.
    /// </summary>
    public IReadOnlyList<int>? LinkedInitialization { get; init; }

    /// <summary>
    /// Methods each bucket depends on through a value it read rather than code it ran: the code that
    /// built a value stored into a field (a static initializer, <c>_x ??= new X()</c>), credited to
    /// every bucket that read the field, wherever the building ran. Kept apart from
    /// <see cref="Hits"/>, which keep where code really ran.
    /// Null for maps recorded before field reads were tracked.
    /// </summary>
    public CoverageHits? BuiltValues { get; init; }

    /// <summary>
    /// Methods (indexes into <see cref="Methods"/>) that implement an interface or override a method of
    /// a third-party library (neither the repository's nor the .NET shared framework's), such as a
    /// Castle DynamicProxy <c>IProxyGenerationHook</c>. Such a library calls them and may keep the
    /// result for every later caller (Castle builds a proxy type once per interface), so the classes
    /// that ran them are not all that depend on them. Null for maps recorded before this was kept.
    /// </summary>
    public IReadOnlyList<int>? ExternalCallbacks { get; init; }

    /// <summary>
    /// Methods (indexes into <see cref="Methods"/>) compiled from lambdas, anonymous methods and local
    /// functions, or their state machines. They carry the name of the member they are written in, whose
    /// own body may run elsewhere: an endpoint registration runs at every host's start, its handler lambda
    /// only for the requests that reach it. A member reached by a name written only inside its endpoint
    /// handlers is answered by these alone (docs/design/endpoint-handlers.md). Null for maps recorded
    /// before this was kept.
    /// </summary>
    public IReadOnlyList<int>? NestedMethods { get; init; }

    /// <summary>
    /// Files source generators wrote into the recorded assemblies, read from their PDBs. They are not
    /// in the source tree, so the source index adds them from here, as sources of their projects.
    /// </summary>
    public IReadOnlyList<GeneratedSource> Generated { get; init; } = [];

    /// <summary>What the tests read by a path; null for a map recorded before reads were (it can answer nothing about them).</summary>
    public FileReads? FileReads { get; init; }

    /// <summary>
    /// File names of the third-party assemblies in the test output that read files but whose reads the
    /// recording could not see (they could not be rewritten), sorted; empty when it saw the reads of every
    /// third-party assembly as well as the repository's (docs/design/non-code-files.md). Null for maps
    /// recorded before third-party reads were, which know only the reads of repository code. Only a map
    /// with an empty list can vouch that a non-code file was read by no test.
    /// </summary>
    public IReadOnlyList<string>? FileReadsUnseen { get; init; }

    /// <summary>
    /// True when <see cref="FileReads"/> holds every file read the recording could observe, the reads of
    /// third-party code included, so a file it does not name was read by no test that ran.
    /// </summary>
    public bool KnowsAllFileReads => FileReads is not null && FileReadsUnseen is [];

    /// <summary>
    /// Assemblies the test processes loaded without a wrapped call naming them, by simple and file name, sorted: by
    /// code the recording does not rewrite (a third-party plugin host), by the runtime (a dependency), or before the
    /// tracker started (docs/design/loaded-outputs.md). <see cref="OutputUses"/> cannot say who loaded these. Null for
    /// a map recorded before loads were tracked, whose <see cref="OutputUses"/> may miss any load.
    /// </summary>
    public IReadOnlyList<string>? OutputLoadsUnseen { get; init; }

    /// <summary>
    /// Before a type's full name in <see cref="OutputUses"/>: the bucket read the type's shape through reflection or a
    /// schema exporter, without running its code; the tracker's <c>TestDettaTracker.ShapePrefix</c>.
    /// </summary>
    public const string OutputShapePrefix = "shape:";

    /// <summary>
    /// Before a name in <see cref="OutputUses"/>: the bucket enumerated the loaded assemblies (<c>AppDomain.GetAssemblies</c>)
    /// while that assembly was loaded, rather than loading it; the tracker's <c>TestDettaTracker.ScanPrefix</c>. A map
    /// recorded before 2026-10-11 has scans as plain names.
    /// </summary>
    public const string OutputScanPrefix = "scan:";

    /// <summary>
    /// Before a name in <see cref="OutputUses"/>: the bucket loaded that assembly by its name rather than by a path; the
    /// tracker's <c>TestDettaTracker.NameLoadPrefix</c>. A map recorded before 2026-10-11 has such loads as plain names.
    /// </summary>
    public const string OutputNameLoadPrefix = "name:";

    /// <summary>
    /// True when <see cref="OutputUses"/> names every class that loaded <paramref name="names"/>: loads were tracked
    /// and none of the names was loaded unseen.
    /// </summary>
    /// <param name="names">An output's assembly and project names.</param>
    public bool KnowsLoadsOf(IReadOnlyCollection<string> names) =>
        OutputUses is not null
        && OutputLoadsUnseen is { } unseen
        && !unseen.Any(u => names.Any(n => string.Equals(u, n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// What the tests started as a process, loaded as an assembly, or read from a build output that git
    /// does not track, by name: the last path segment without <c>.dll</c>, <c>.exe</c> or a project
    /// extension, shaped like <see cref="FileReads"/>. A project referenced without compiling against
    /// it is used at run time by the classes whose names include its assembly or project name
    /// (docs/design/runtime-file-reads.md). Null for a map recorded before uses were (it can answer
    /// nothing about them).
    /// </summary>
    public FileReads? OutputUses { get; init; }

    /// <summary>
    /// What the build fed the compiler of each project in the test output (the test project included)
    /// from outside the files git tracks, by project path relative to the repository: compiled documents
    /// by file name with their PDB checksums, and embedded resources (<c>resource:</c> and name) with their
    /// SHA-256 (docs/design/runtime-file-reads.md). When the build of a project uses the output of one it
    /// references without compiling against it, an equal set in a current build shows the output reached
    /// it only as a file to run. Recorded for <see cref="Framework"/>; null for maps recorded before.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? BuildInputs { get; init; }

    /// <summary>
    /// <see cref="BuildInputs"/> of the frameworks other than <see cref="Framework"/>, by framework, for a
    /// multi-targeted test project recorded without <c>-f</c> (the frameworks of
    /// <see cref="GeneratedFrameworks"/>): another framework's build may compile other files, so each one that
    /// runs is compared with its own (docs/design/build-inputs-per-framework.md). Null when the map holds one
    /// framework's build.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? FrameworkBuildInputs { get; init; }

    /// <summary>
    /// Test classes (runtime names) the test reports listed although none of their tests executed:
    /// every test was skipped, inconclusive or ignored, so the map cannot say what the class runs.
    /// Together with <see cref="Classes"/> and <see cref="FailedClasses"/> this is every class the
    /// runner reported (see <see cref="KnowsClasses"/>). Null when a test process left no report or
    /// for maps recorded before this was kept: then the map cannot tell a class it never saw from one
    /// that ran nothing (docs/design/unrecorded-classes.md).
    /// </summary>
    public IReadOnlySet<string>? SkippedClasses { get; init; }

    /// <summary>
    /// Runtime identifier of the machine that recorded the map, such as <c>osx-arm64</c>. A class
    /// skipped there may run on another platform, where the map cannot answer for it. Null for maps
    /// recorded before this was kept.
    /// </summary>
    public string? Platform { get; init; }

    /// <summary>
    /// Target framework the test project was recorded for, such as <c>net8.0</c>, when it targets
    /// several; null when it targets one. Generated code can differ per framework, so what the
    /// generators write now is read from a build for this framework (docs/design/generator-output-changes.md).
    /// </summary>
    public string? Framework { get; init; }

    /// <summary>
    /// Every target framework of a test project that targets several, when <see cref="Generated"/> holds
    /// the generated code of each (tagged with <see cref="GeneratedSource.Framework"/>); null when it
    /// targets one, or when only some were built (recorded with <c>-f</c>). A changed generator is then
    /// compared framework by framework; without the list, such a project runs completely.
    /// </summary>
    public IReadOnlyList<string>? GeneratedFrameworks { get; init; }

    /// <summary>
    /// Where each reported test class (runtime name) is written: one entry per source file with the
    /// first line of its code there, read from the test assembly's PDB. When the build names a class
    /// differently from the recording (Markdig's culture-dependent generated spec classes), the class
    /// at the same place is the one the map recorded. Null when the PDB could not be read or for maps
    /// recorded before this was kept.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ClassLocation>>? ClassLocations { get; init; }

    /// <summary>
    /// The projects whose assemblies the recording instrumented in the test project's output (the test project
    /// included), as paths relative to the repository root with '/' separators, sorted. A project the test project
    /// compiles against that is not here ran uninstrumented: the map knows nothing of its code, even where a project it
    /// does know compiles the same file (a linked source file) into another assembly under the same names. Null for maps
    /// recorded before this was kept, which selection reads as before: a member the map names counts as known.
    /// </summary>
    public IReadOnlyList<string>? InstrumentedProjects { get; init; }

    /// <summary>
    /// Per test class, the tracked fields (<c>Type::Field</c>, sorted) it read while they held a value another test
    /// class's code had stored: state one class left for the next (docs/design/order-dependent-state.md). Run alone or
    /// in another order such a class finds the field unset and may run code the map never saw it run. Recorded to
    /// measure how common that is; selection does not read it. Null for maps recorded before it was kept.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? LeakedReads { get; init; }

    /// <summary>
    /// The test classes (runtime names) that use each shared data source bucket (<see cref="DataSourcePrefix"/>), as the
    /// recorder found them: the classes naming it in a data attribute, those deriving from an abstract one included. Its
    /// code, run while the framework discovers tests, serves them alone. Null for maps recorded before (their shared data
    /// counted as code outside every test).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? DataSourceUsers { get; init; }

    /// <summary>
    /// Fixture name to the test classes whose tests built that fixture type themselves (<c>new FixtureA()</c>): users of the
    /// fixture as much as the classes it is injected into, since a fixture type may share what any instance built (a
    /// server cached per fixture type, FastEndpoints). What such a test ran after building it is in the fixture's hits too.
    /// Null for a map recorded before 2026-10-12.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? FixtureBuilders { get; init; }

    /// <summary>
    /// Methods (indexes into <see cref="Methods"/>) whose users no recording at <see cref="Commit"/> could name: members that
    /// were added, belong to a type whose declaration changed, or whose metadata changed since the last full recording, in a
    /// map built by incremental steps (docs/design/incremental-recording.md). Carried-over classes were recorded before such
    /// a member looked as it does now, so they may reach it by reflection or dispatch without the map showing it; selection
    /// treats these like code that ran outside every test. Null for a full recording.
    /// </summary>
    public IReadOnlyList<int>? Unobserved { get; init; }

    /// <summary>How the map was made: null for a full recording, the incremental steps since one otherwise.</summary>
    public MapLineage? Lineage { get; init; }

    /// <summary>
    /// True when the map knows every class the runner reported, run or not, so a class outside them
    /// was not seen while recording rather than seen running nothing.
    /// </summary>
    public bool KnowsClasses => SkippedClasses is not null && FailedClasses is not null;

    /// <summary>
    /// True when the runner reported <paramref name="testClass"/> while the map was recorded: it ran,
    /// failed or was skipped. Meaningful only when <see cref="KnowsClasses"/> is true.
    /// </summary>
    /// <param name="testClass">Runtime class name.</param>
    public bool Reported(string testClass) =>
        Classes.ContainsKey(testClass) || FailedClasses?.Contains(testClass) == true || SkippedClasses?.Contains(testClass) == true;
}

/// <summary>
/// The incremental steps a map was built by since its last full recording (docs/design/incremental-recording.md). Each
/// step re-records the classes a range of commits can affect and carries the others over, so the count and the age bound
/// how long a wrong carried-over entry could survive; past the thresholds the next recording is a full one.
/// </summary>
/// <param name="FullCommit">Commit of the last full recording.</param>
/// <param name="FullRecordedAt">When the last full recording was made.</param>
/// <param name="Steps">Incremental steps since then, at least one.</param>
public sealed record MapLineage(string FullCommit, DateTimeOffset FullRecordedAt, int Steps);

/// <summary>Where a test class is written in one source file, as the test assembly's PDB says.</summary>
/// <param name="File">Path relative to the repository root, with '/' separators.</param>
/// <param name="Line">First line (1-based) of the class's code in that file.</param>
public sealed record ClassLocation(string File, int Line);
