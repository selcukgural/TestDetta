namespace TestDetta.Core;

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

    /// <summary>
    /// What a test project with an older map does when the changes since the map include one no member
    /// describes (a build file, configuration, a global using) in a project it depends on.
    /// </summary>
    public DriftFallback DriftFallback { get; init; } = DriftFallback.RunAll;

    /// <summary>
    /// Reads what source generators write now, to compare with the maps when a change reaches a
    /// generator in the repository; null keeps every project using a changed generator running completely.
    /// </summary>
    public IGeneratedCodeReader? GeneratedCode { get; init; }

    /// <summary>How maps are used; anything but <see cref="MapUse.Bridge"/> exists for evaluation only.</summary>
    internal MapUse Maps { get; init; } = MapUse.Bridge;

    /// <summary>
    /// Stops the analysis between its steps (after the graph, after reading the changes, before selecting), so a
    /// caller that gave up waiting for it (the evaluation's analysis timeout) can wait for it to end before it uses
    /// the repository again. A step that is running finishes first; none can.
    /// </summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// Files whose working-tree version is this text instead of what is on disk, by path relative to the repository
    /// root with '/' separators; each counts as changed. <c>td audit</c> gives the analysis a change this way without
    /// writing it into the team's sources (docs/design/audit.md); empty for every other caller.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkingTreeOverlay { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// How a test project whose older map cannot be bridged is decided: the changes since the map include
/// one that no member describes, such as configuration that wires a new DI registration, which the map
/// cannot know and the name trace cannot see.
/// </summary>
public enum DriftFallback
{
    /// <summary>Run the whole test project (the default: such a change may have created any call).</summary>
    RunAll,

    /// <summary>Decide by name, as without a map: faster, but blind to calls made through configuration or reflection.</summary>
    ByName,
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
