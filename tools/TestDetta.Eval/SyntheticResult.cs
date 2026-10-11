namespace TestDetta.Eval;

/// <summary>One synthetic change: what TestDetta selected for it and which test classes its mutant failed.</summary>
/// <param name="Target">The changed member.</param>
/// <param name="Detected">True when TestDetta reported a change to the member's type or to the whole project.</param>
/// <param name="MutatedMethods">Compiled methods that threw; 0 when the member was not found in any test output.</param>
/// <param name="KilledClasses">Test classes that failed with the mutant but not without it (<c>Project: Class</c>).</param>
/// <param name="MissedClasses">Of those, the classes TestDetta did not select: a missed test.</param>
/// <param name="SelectedShare">Share of the recorded test classes that TestDetta selected, from 0 to 1.</param>
/// <param name="Inconclusive">
/// True when the runs could not decide (docs/evaluation.md, "Judging"): a doubt that could hide a miss (a part
/// TestDetta left out that does not complete without the mutant either, a class whose run alone ran none of its tests),
/// or a catch that could not be confirmed with nothing else to decide. The mutant leaves the miss rate's n and is
/// counted as a miss in the worst-case bound.
/// </param>
internal sealed record SyntheticMutant(
    SyntheticTarget Target,
    bool Detected,
    int MutatedMethods,
    IReadOnlyList<string> KilledClasses,
    IReadOnlyList<string> MissedClasses,
    double SelectedShare,
    bool Inconclusive)
{
    /// <summary>
    /// True when the mutant was caught and a part TestDetta ran crashed or hung with the mutant, again when run
    /// apart, and completed without it: TestDetta caught it without a class list to show.
    /// </summary>
    public bool CaughtByCrash { get; init; }

    /// <summary>Why the mutant is inconclusive; null otherwise.</summary>
    public string? Note { get; init; }

    /// <summary>Classes that failed with the mutant but were not counted, with the reason (<c>Project: Class: why</c>).</summary>
    public IReadOnlyList<string> NotCounted { get; init; } = [];

    /// <summary>
    /// True when TestDetta's analysis stopped on a build that fails with the mutant (a generator mutant its
    /// consumers do not compile with), as <c>td test</c> would: it counts for misses, judged like a full run,
    /// but not for the selected share, since nothing was selected.
    /// </summary>
    public bool StoppedOnBuild { get; init; }

    /// <summary>True when the test outcome came from the <see cref="OutcomeCache"/> and only the selection was computed now.</summary>
    public bool Reused { get; init; }

    /// <summary>True when the mutant failed at least one test class, so it can prove a miss or its absence.</summary>
    public bool Killing => !Inconclusive && (KilledClasses.Count > 0 || CaughtByCrash);

    /// <summary>
    /// What each other selector (drift modes with <see cref="RunSettings.MapAge"/>, dotnet-tia with
    /// <see cref="RunSettings.TiaCli"/>) selected and missed, against the same confirmed failures as the
    /// main result. Empty without them, and when the full run crashed (a crash proves nothing about
    /// which classes a selector left out).
    /// </summary>
    public IReadOnlyDictionary<OtherSelector, OtherOutcome> Others { get; init; } = new Dictionary<OtherSelector, OtherOutcome>();
}

/// <summary>
/// A selection the evaluation judges next to TestDetta's own on the same mutants: TestDetta with maps recorded
/// before the evaluated commit (the drift modes), or another tool.
/// </summary>
internal enum OtherSelector
{
    /// <summary>The older maps through the drift bridge (what TestDetta does).</summary>
    Bridge,

    /// <summary>No maps: every test project decided by name (what TestDetta did with an older map before the bridge).</summary>
    NoMap,

    /// <summary>The older maps as if recorded at the evaluated commit; shows the misses the drift causes unbridged.</summary>
    Naive,

    /// <summary>dotnet-tia's static selection (docs/design/tia-comparison.md).</summary>
    Tia,

    /// <summary>
    /// Every test project the change affects, run whole: project-level selection as dotnet-affected
    /// and similar tools do it, from TestDetta's own project graph and impact. Always measured.
    /// </summary>
    ProjectLevel,

    /// <summary>dotnet-affected itself (whole affected test projects), to confirm <see cref="ProjectLevel"/> with the tool teams use.</summary>
    DotnetAffected,

    /// <summary>
    /// Maps built by one incremental recording step from the older maps to the evaluated commit
    /// (docs/design/incremental-recording.md); judged against the exact maps of a full recording there.
    /// </summary>
    Incremental,
}

/// <summary>What the incremental recording step did for one test project, next to the full recording at the same commit.</summary>
/// <param name="Project">The test project's name.</param>
/// <param name="Kind">full, partial, carry or current.</param>
/// <param name="Reason">Why, as <c>td record --incremental</c> says it.</param>
/// <param name="Recorded">Classes the step recorded (all of them for a full recording, none for a carried map).</param>
/// <param name="Total">Classes the older map knew.</param>
/// <param name="Seconds">Wall time of the step's recording, build included; 0 when nothing was recorded.</param>
/// <param name="FullSeconds">Wall time of the full recording at the commit; null when it came from a cache that did not keep it.</param>
/// <param name="Divergent">Classes whose full entry ran code the incremental map did not credit them with (<see cref="Core.MapDivergence"/>).</param>
internal sealed record IncrementalStepResult(string Project, string Kind, string Reason, int Recorded, int Total, double Seconds, double? FullSeconds, IReadOnlyList<string> Divergent);

/// <summary>One other selector's result for a mutant.</summary>
/// <param name="Missed">Confirmed failing classes the mode did not select (<c>Project: Class</c>).</param>
/// <param name="SelectedShare">Share of the evaluated commit's recorded classes the mode selected.</param>
internal sealed record OtherOutcome(IReadOnlyList<string> Missed, double SelectedShare)
{
    /// <summary>Why the selector ran everything (dotnet-tia's full-run reason, or its failure); null otherwise.</summary>
    public string? FullRun { get; init; }

    /// <summary>The selector's own analysis time, when it reports one (dotnet-tia).</summary>
    public double? Seconds { get; init; }
}

/// <summary>A synthetic evaluation of one commit.</summary>
/// <param name="Sha">Commit the changes were made on.</param>
/// <param name="TestClasses">Test classes in the recorded maps.</param>
/// <param name="BaselineSeconds">Time of the full suite without mutants.</param>
/// <param name="Mutants">One result per sampled member, in sample order.</param>
/// <param name="Error">Why the commit could not be evaluated, or null.</param>
internal sealed record SyntheticRun(string Sha, int TestClasses, double BaselineSeconds, IReadOnlyList<SyntheticMutant> Mutants, string? Error)
{
    /// <summary>Test projects left out because they wrote no test report, relative to the repository root.</summary>
    public IReadOnlyList<string> Unreported { get; init; } = [];

    /// <summary>
    /// Seed of the sample, so the run can be repeated with <c>--seed</c>; null in a run made before seeds were reported.
    /// A replay keeps its source's seed, which picked its changes.
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>True when the seed was drawn for this run rather than given.</summary>
    public bool SeedDrawn { get; init; }

    /// <summary>The <c>synthetic.json</c> whose changes this run replayed (<c>--replay</c>); null for a sample.</summary>
    public string? ReplayOf { get; init; }

    /// <summary>True when the mutants were switched on in a schema rather than written in one at a time.</summary>
    public bool Schema { get; init; }

    /// <summary>Mutants that ran at once; 1 when one at a time.</summary>
    public int MutantParallelism { get; init; } = 1;

    /// <summary>The pinned toolchain the run used, such as <c>SDK 10.0.401 + 8.0.31 (osx-arm64)</c>; null for the <c>dotnet</c> on PATH.</summary>
    public string? Toolchain { get; init; }

    /// <summary>Wall-clock time of the evaluation so far, recording and building included.</summary>
    public double Seconds { get; init; }

    /// <summary>First-parent commits between the drift modes' map commit and <see cref="Sha"/>; 0 without drift modes.</summary>
    public int MapAge { get; init; }

    /// <summary>Commit the drift modes' maps were recorded at; null without drift modes.</summary>
    public string? MapCommit { get; init; }

    /// <summary>The judging rules the mutants were judged by (<see cref="MutantJudge.Rules"/>).</summary>
    public string Judging { get; init; } = MutantJudge.Rules;

    /// <summary>Recorded test classes per map, by test project name, for the incidents and the comparison with an earlier round.</summary>
    public IReadOnlyList<MapCount> Maps { get; init; } = [];

    /// <summary>With drift modes: the incremental recording step from the older maps, per test project; empty otherwise.</summary>
    public IReadOnlyList<IncrementalStepResult> IncrementalSteps { get; init; } = [];

    /// <summary>What went wrong while recording or evaluating that a reader of the numbers must know (docs/evaluation.md, "Incidents").</summary>
    public IReadOnlyList<string> Incidents { get; init; } = [];
}

/// <summary>What one coverage map of an evaluation holds.</summary>
/// <param name="Project">The test project's name.</param>
/// <param name="Classes">Recorded test classes (with coverage).</param>
/// <param name="Failed">Classes that failed while recording; null when the recording could not tell which.</param>
internal sealed record MapCount(string Project, int Classes, int? Failed);
