namespace Sift.Core;

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

/// <summary>Which methods each test class and fixture of one test project ran, recorded by <c>sift record</c>.</summary>
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
    /// Methods that some recording process ran in two or more test classes or fixtures. A method
    /// that ran in a single bucket of every process may be run-once code (a lazy cache): with
    /// several processes it shows up once per process. Null for maps recorded before this was kept.
    /// </summary>
    public IReadOnlyList<int>? SharedInProcess { get; init; }
}
