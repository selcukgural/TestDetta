namespace TestDetta.Core;

/// <summary>
/// Tells what source generators write into a test project's build now. When a change reaches a
/// generator in the repository, the selection compares this with the generated code its coverage map
/// recorded, so only generated code that really changed counts (docs/design/generator-output-changes.md).
/// The analysis itself never builds; the command line and the evaluation supply a reader that does.
/// </summary>
public interface IGeneratedCodeReader
{
    /// <summary>
    /// The generated files of <paramref name="testProject"/> and of the repository's projects in its
    /// output, as the recorder reads them, from a build of the current sources: built for this, or one
    /// that is already up to date.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="testProject">Test project whose output is read.</param>
    /// <param name="framework">Target framework its map was recorded for, or null for its only one.</param>
    GeneratedCodeRead Read(ProjectGraph graph, string repoRoot, ProjectNode testProject, string? framework);
}

/// <summary>What <see cref="IGeneratedCodeReader.Read"/> found: the generated files, or why there are none.</summary>
/// <param name="Sources">The generated files; null when they could not be read.</param>
/// <param name="Unavailable">Why they could not be read, when the build itself was fine (no current build, no PDB); null otherwise.</param>
/// <param name="BuildErrors">The compiler's errors when the build failed; null otherwise.</param>
public sealed record GeneratedCodeRead(IReadOnlyList<GeneratedSource>? Sources, string? Unavailable, string? BuildErrors)
{
    /// <summary>The generated files of a build.</summary>
    /// <param name="sources">Files read from the PDBs.</param>
    public static GeneratedCodeRead Read(IReadOnlyList<GeneratedSource> sources) => new(sources, null, null);

    /// <summary>No generated files to compare, for a reason that is not a broken build.</summary>
    /// <param name="reason">Why, phrased to follow "and": "no current build exists".</param>
    public static GeneratedCodeRead NotAvailable(string reason) => new(null, reason, null);

    /// <summary>The build failed: the sources do not compile, which <c>dotnet test</c> would report the same way.</summary>
    /// <param name="errors">The first compiler errors.</param>
    public static GeneratedCodeRead Failed(string errors) => new(null, null, errors);
}
