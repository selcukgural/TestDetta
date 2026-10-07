using TestDetta.Mutants;

namespace TestDetta.Cli;

/// <summary>What one audited change came to (docs/design/audit.md, "Judging").</summary>
internal enum AuditOutcome
{
    /// <summary>Tests broke and the selection ran every class that broke.</summary>
    Caught,

    /// <summary>A class that broke, confirmed alone with and without the change, was left out: a miss.</summary>
    Missed,

    /// <summary>The change ran every test that can load it and none failed.</summary>
    DidNotBreak,

    /// <summary>No audited test project's build output holds the member, so no test can see the change; not counted.</summary>
    NotReached,

    /// <summary>A run did not complete in a way the rules can judge (a crash outside the selection, say); listed, not counted.</summary>
    Inconclusive,

    /// <summary>Cut by the budget or a cancellation while it ran; listed, not counted.</summary>
    Stopped,
}

/// <summary>A test class that failed with a change, and what the selection decided for it.</summary>
/// <param name="Project">Its test project, relative to the repository root.</param>
/// <param name="Name">Runtime class name.</param>
/// <param name="Selected">True when the selection ran it.</param>
internal sealed record AuditClass(string Project, string Name, bool Selected)
{
    /// <summary>The rule that decided the class (<c>Coverage</c>, <c>Name</c>, …), or why there was none.</summary>
    public string Decision { get; init; } = string.Empty;

    /// <summary>What the rule found, such as the changed member the class ran and the map's commit.</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>How the selection decided the class's project (<c>All</c>, <c>Filtered</c>, <c>Skipped</c>, <c>not-affected</c>).</summary>
    public string ProjectMode { get; init; } = string.Empty;

    /// <summary>Why the project was decided that way.</summary>
    public string ProjectReason { get; init; } = string.Empty;

    /// <summary>Commit of the project's coverage map; null when it has none in use.</summary>
    public string? MapCommit { get; init; }

    /// <summary>For a class not counted: what its runs alone showed.</summary>
    public string? Note { get; init; }
}

/// <summary>One audited change: a member broken by one mutant kind, and what the tests and the selection did.</summary>
/// <param name="Index">Position in the sample, from 1.</param>
/// <param name="Target">The member, its kind and its behaviour-neutral edit.</param>
/// <param name="Outcome">What it came to.</param>
internal sealed record AuditMutant(int Index, SyntheticTarget Target, AuditOutcome Outcome)
{
    /// <summary>The file of the edit, relative to the repository root.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>Compiled methods (or field stores) the mutant changed.</summary>
    public int MutatedMethods { get; init; }

    /// <summary>Audited test projects whose output holds the member, which all ran completely.</summary>
    public IReadOnlyList<string> Reached { get; init; } = [];

    /// <summary>Classes that broke, counted: each selected or a confirmed miss.</summary>
    public IReadOnlyList<AuditClass> Broken { get; init; } = [];

    /// <summary>Classes that failed but are not counted, with the reason (failed only next to others, fails alone anyway).</summary>
    public IReadOnlyList<AuditClass> NotCounted { get; init; } = [];

    /// <summary>What TestDetta saw change, such as <c>Shop.Calc.Tax(decimal) modified</c>.</summary>
    public IReadOnlyList<string> Seen { get; init; } = [];

    /// <summary>Share of the recorded test classes the selection ran.</summary>
    public double SelectedShare { get; init; }

    /// <summary>True when a test host crashed or hung with the change and everything the selection left out passed.</summary>
    public bool CaughtByCrash { get; init; }

    /// <summary>Why it is inconclusive, not reached or stopped; null otherwise.</summary>
    public string? Note { get; init; }

    /// <summary>Wall time of its runs and analysis.</summary>
    public double Seconds { get; init; }

    /// <summary>True for a change that broke tests: n of the bound.</summary>
    public bool Broke => Outcome is AuditOutcome.Caught or AuditOutcome.Missed;

    /// <summary>True for a change whose tests ran to a judgement (what <c>--max-mutants</c> counts).</summary>
    public bool Ran => Outcome is AuditOutcome.Caught or AuditOutcome.Missed or AuditOutcome.DidNotBreak or AuditOutcome.Inconclusive;

    /// <summary>The member as <c>--member</c> takes it.</summary>
    public string Member => $"{Target.Member.TypeName}.{Target.Member.MemberName}";
}

/// <summary>A test project of the repository and whether the audit ran it.</summary>
/// <param name="Path">Relative to the repository root.</param>
/// <param name="Audited">True when the changes ran it.</param>
/// <param name="Reason">Why it is not audited; null when it is.</param>
internal sealed record AuditProjectInfo(string Path, bool Audited, string? Reason)
{
    /// <summary>Wall time of its baseline run.</summary>
    public double BaselineSeconds { get; init; }

    /// <summary>Classes that failed in the baseline, which no change is judged by.</summary>
    public IReadOnlyList<string> BaselineFailed { get; init; } = [];

    /// <summary>Test classes in its baseline report.</summary>
    public int Classes { get; init; }

    /// <summary>Commit of its coverage map in use; null without one (the selection then decides it by name).</summary>
    public string? MapCommit { get; init; }
}

/// <summary>How the sample was made, for the report.</summary>
/// <param name="Seed">Seed of the order.</param>
/// <param name="Kinds">Mutant kinds, as given.</param>
/// <param name="Candidates">Members in the sample's order.</param>
/// <param name="CoveredCandidates">Of those, members a map says some test ran (reported, not used).</param>
/// <param name="GeneratorMembers">Members left out because a source generator or analyzer project compiles them.</param>
internal sealed record AuditSampleInfo(int Seed, string Kinds, int Candidates, int CoveredCandidates, int GeneratorMembers)
{
    /// <summary>The <c>--member</c> filter; empty for the whole sample.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];
}

/// <summary>Inputs that let a reader repeat the audit.</summary>
/// <param name="Version">TestDetta's informational version.</param>
/// <param name="BinarySha256">SHA-256 of the TestDetta assembly that ran.</param>
/// <param name="Head">The commit audited.</param>
/// <param name="Maps">Each test project's map: project, commit and file hash.</param>
/// <param name="Options">The options that decide the result.</param>
/// <param name="Command">The command that repeats it.</param>
internal sealed record AuditReproduction(
    string Version,
    string? BinarySha256,
    string? Head,
    IReadOnlyList<(string TestProject, string Commit, string Sha256)> Maps,
    IReadOnlyList<string> Options,
    string Command);

/// <summary>The result of <c>td audit</c>.</summary>
/// <param name="Root">The team's repository root.</param>
/// <param name="StopReason">Why it stopped: <c>target</c>, <c>max-mutants</c>, <c>budget</c>, <c>sample</c>, <c>cancelled</c> or <c>error</c>.</param>
/// <param name="Mutants">Every change taken from the sample, in sample order.</param>
/// <param name="Projects">Every test project, audited or not.</param>
/// <param name="Settings">The audit's options.</param>
/// <param name="Reproduction">How to repeat it.</param>
internal sealed record AuditResult(
    string Root,
    string StopReason,
    IReadOnlyList<AuditMutant> Mutants,
    IReadOnlyList<AuditProjectInfo> Projects,
    AuditSettings Settings,
    AuditReproduction Reproduction)
{
    /// <summary>Why the audit could not run or stopped early with an error; null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>How the sample was made; null when the audit stopped before sampling.</summary>
    public AuditSampleInfo? Sample { get; init; }

    /// <summary>The <c>dotnet test</c> arguments after <c>--</c>.</summary>
    public IReadOnlyList<string> TestArguments { get; init; } = [];

    /// <summary>Wall time of the whole audit.</summary>
    public double Seconds { get; init; }

    /// <summary>Wall time of the baseline runs.</summary>
    public double BaselineSeconds { get; init; }

    /// <summary>The work folder; deleted at the end.</summary>
    public string? WorkFolder { get; init; }

    /// <summary>A work folder that could not be deleted; the next audit removes it.</summary>
    public string? LeftBehind { get; init; }

    /// <summary>Changes tested at once.</summary>
    public int Parallelism { get; init; } = 1;

    /// <summary>Changes that ran tests to a judgement.</summary>
    public int Changes => Mutants.Count(static m => m.Ran);

    /// <summary>Changes that broke tests: n of the bound.</summary>
    public int Broke => Mutants.Count(static m => m.Broke);

    /// <summary>Changes with a confirmed miss.</summary>
    public int Missed => Mutants.Count(static m => m.Outcome == AuditOutcome.Missed);

    /// <summary>The changes with the given outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    public int Count(AuditOutcome outcome) => Mutants.Count(m => m.Outcome == outcome);

    /// <summary>The exact one-sided 95% upper bound of the miss rate (<see cref="MissRate.UpperBound"/>), n = <see cref="Broke"/>.</summary>
    public double UpperBound => MissRate.UpperBound(Missed, Broke);

    /// <summary>True when as many changes broke tests as the target asks.</summary>
    public bool TargetReached => Broke >= Settings.TargetBroken;
}
