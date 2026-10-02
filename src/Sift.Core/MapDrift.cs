namespace Sift.Core;

/// <summary>
/// What changed between the commit some coverage maps were recorded at and the merge base. The maps
/// still say which test classes ran a member at <see cref="FromCommit"/>; the selection adds the
/// classes that can reach the change through code the drift changed (docs/design/map-drift-bridge.md).
/// </summary>
/// <param name="FromCommit">Commit the maps were recorded at, an ancestor of the merge base.</param>
/// <param name="Commits">First-parent commits between <paramref name="FromCommit"/> and the merge base.</param>
/// <param name="ChangedFiles">Paths changed in between, relative to the repository root.</param>
/// <param name="Impact">Project-level impact and code changes of <paramref name="ChangedFiles"/>.</param>
public sealed record MapDrift(string FromCommit, int Commits, IReadOnlyList<string> ChangedFiles, ImpactResult Impact);

/// <summary>Settings of one analysis.</summary>
public sealed record AnalysisOptions
{
    /// <summary>Default of <see cref="MaxMapAge"/>: covers the newest nightly map of every corpus repository at p90.</summary>
    public const int DefaultMaxMapAge = 50;

    /// <summary>
    /// How many first-parent commits a coverage map may be behind the merge base and still be used
    /// through the drift bridge; 0 uses only maps recorded exactly at the merge base.
    /// </summary>
    public int MaxMapAge { get; init; } = DefaultMaxMapAge;

    /// <summary>How maps are used; anything but <see cref="MapUse.Bridge"/> exists for evaluation only.</summary>
    internal MapUse Maps { get; init; } = MapUse.Bridge;
}

/// <summary>How an analysis uses coverage maps.</summary>
internal enum MapUse
{
    /// <summary>Maps at the merge base as they are; older ones within the age limit through the drift bridge.</summary>
    Bridge,

    /// <summary>No map at all: every test project is decided by name (what an older map got before the bridge).</summary>
    None,

    /// <summary>
    /// Every map as if it had been recorded at the merge base, ignoring the drift. Unsafe; it measures
    /// how many misses the drift causes without a bridge.
    /// </summary>
    AsIfCurrent,
}
