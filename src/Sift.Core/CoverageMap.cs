namespace Sift.Core;

/// <summary>A method as the coverage map names it, in the same terms as <see cref="SymbolChange"/>.</summary>
/// <param name="TypeName">Qualified type name, nested types joined with '.', generic arity as <c>`n</c>.</param>
/// <param name="MemberName">
/// Simple member name: <c>Place</c> for a method (all overloads share it), <c>Total</c> for a
/// property, <c>.ctor</c>/<c>.cctor</c> for constructors, <c>op_Addition</c> for operators.
/// Lambdas, local functions, iterators and async state machines count as the member that contains them.
/// </param>
public sealed record CoveredMethod(string TypeName, string MemberName);

/// <summary>Which methods each test class of one test project ran, recorded by <c>sift record</c>.</summary>
/// <param name="TestProject">Test project path relative to the repository root.</param>
/// <param name="Commit">Commit the map was recorded at.</param>
/// <param name="RecordedAt">When the map was recorded.</param>
/// <param name="Methods">
/// Every instrumented method, run or not; a method missing here is unknown, a method listed here
/// that no class ran is known to be untested.
/// </param>
/// <param name="Classes">Test class (runtime name) to indexes into <paramref name="Methods"/>.</param>
/// <param name="Ambient">Indexes of methods that ran outside any test, such as assembly-level setup.</param>
public sealed record CoverageMap(
    string TestProject,
    string Commit,
    DateTimeOffset RecordedAt,
    IReadOnlyList<CoveredMethod> Methods,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Classes,
    IReadOnlyList<int> Ambient);
