namespace Sift.Core;

/// <summary>
/// Evaluates project files with MSBuild, so the graph knows what the XML reading of
/// <see cref="ProjectGraphLoader"/> can only guess: references and items built by property
/// functions, conditions and <c>Remove</c>. See docs/design/msbuild-evaluation.md.
/// </summary>
public interface IProjectEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="projects"/>. A project that could not be evaluated is missing from the
    /// result; null when nothing could be evaluated (no SDK, the evaluation process failed).
    /// </summary>
    /// <param name="repoRoot">Normalized repository root.</param>
    /// <param name="projects">Normalized paths of the project files to evaluate.</param>
    IReadOnlyDictionary<string, EvaluatedProject>? Evaluate(string repoRoot, IReadOnlyCollection<string> projects);
}

/// <summary>What MSBuild says about one project, united over its target frameworks.</summary>
/// <param name="IsTestProject">True when the <c>IsTestProject</c> property is <c>true</c> in any target framework.</param>
/// <param name="AssemblyName">The <c>AssemblyName</c> property, or null when empty.</param>
/// <param name="References">Normalized full paths of its <c>ProjectReference</c> items.</param>
/// <param name="Items">
/// Normalized full paths of the files in its build items (<c>Compile</c>, <c>EmbeddedResource</c>,
/// <c>Content</c>, <c>None</c>, <c>AdditionalFiles</c>), inside its directory or not.
/// </param>
/// <param name="Packages">Names of its <c>PackageReference</c> items.</param>
public sealed record EvaluatedProject(
    bool IsTestProject,
    string? AssemblyName,
    IReadOnlyList<string> References,
    IReadOnlySet<string> Items,
    IReadOnlySet<string> Packages)
{
    /// <summary>
    /// The preprocessor symbols the compiler defines, one set per target framework: <c>DefineConstants</c>
    /// with the SDK's implicit framework symbols (<c>NET8_0_OR_GREATER</c>). Empty when unknown.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> SymbolSets { get; init; } = [];

    /// <summary>The part of <see cref="References"/> it does not compile against; see <see cref="ProjectNode.OpaqueReferences"/>.</summary>
    public IReadOnlySet<string> OpaqueReferences { get; init; } = new HashSet<string>(Paths.Comparer);
}
