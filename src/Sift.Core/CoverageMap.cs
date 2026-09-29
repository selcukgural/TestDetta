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

/// <summary>Which methods each test class and fixture of one test project ran, recorded by <c>sift record</c>.</summary>
/// <param name="Header">Where and when it was recorded.</param>
/// <param name="Methods">
/// Every instrumented method, run or not; a method missing here is unknown, a method listed here
/// that nothing ran is known to be untested.
/// </param>
/// <param name="Hits">Methods run per test class and fixture.</param>
public sealed record CoverageMap(CoverageMapHeader Header, IReadOnlyList<CoveredMethod> Methods, CoverageHits Hits)
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
}
