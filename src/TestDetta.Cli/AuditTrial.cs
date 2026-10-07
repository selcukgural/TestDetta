using System.Diagnostics;
using TestDetta.Core;
using TestDetta.Mutants;

namespace TestDetta.Cli;

/// <summary>What every change of one audit shares.</summary>
/// <param name="Workspace">The work folder and its copies.</param>
/// <param name="Projects">The audited test projects.</param>
/// <param name="Assemblies">The repository's assemblies in the audited outputs, relative to the root of a working copy.</param>
/// <param name="UserArguments">The <c>dotnet test</c> arguments after <c>--</c>.</param>
/// <param name="Selector">Makes <c>td test</c>'s selection for a change.</param>
/// <param name="Maps">The coverage maps by normalized test project path, for the selected share and the map commits.</param>
internal sealed record AuditContext(
    AuditWorkspace Workspace,
    IReadOnlyList<AuditProject> Projects,
    IReadOnlyList<string> Assemblies,
    IReadOnlyList<string> UserArguments,
    AuditSelector Selector,
    IReadOnlyDictionary<string, CoverageMap> Maps)
{
    /// <summary>The audit's time budget; each run's limit is cut to what is left of it.</summary>
    public ShadowBudget Budget { get; init; } = new(null);

    /// <summary>Changes tested at once: the time limits grow with it, since the runs share the machine.</summary>
    public int Parallelism { get; init; } = 1;
}

/// <summary>A worker: its working copy, its folder for backups and the variables of its processes.</summary>
/// <param name="Copy">Root of its working copy.</param>
/// <param name="Backups">Where the originals of rewritten assemblies are kept while a change is in.</param>
/// <param name="Environment">Variables of its processes, such as its own temporary directory.</param>
internal sealed record AuditWorker(string Copy, string Backups, IReadOnlyDictionary<string, string> Environment);

/// <summary>Points tests can reach into: a change written in, before any test runs.</summary>
internal sealed class AuditHooks
{
    /// <summary>Called with a change's index right after its mutant is written into the working copy.</summary>
    public Action<int>? Injected { get; init; }
}

/// <summary>
/// Tests one change (docs/design/audit.md, "Judging"): writes the member's mutant into the worker's copy, runs every
/// audited test project that can load it completely, compares the classes that broke with <c>td test</c>'s selection
/// for that change, and confirms each class the selection left out alone with the change and alone without it before
/// it counts as a miss. The copy's assemblies are restored before it returns, whatever happens.
/// </summary>
/// <param name="context">What the changes share.</param>
/// <param name="hooks">Test hooks.</param>
/// <param name="cancel">Stops the change; its runs are killed.</param>
internal sealed class AuditTrial(AuditContext context, AuditHooks hooks, CancellationToken cancel)
{
    /// <summary>
    /// A change's run may take this many times the project's baseline run before it counts as hung, as in the corpus
    /// evaluation: a mutant can make a test wait forever, for a retry that never succeeds; tests lower it.
    /// </summary>
    internal static double TimeoutFactor { get; set; } = 5;

    /// <summary>The shortest time limit of a change's run, however fast the baseline was; tests lower it.</summary>
    internal static TimeSpan MinimumTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Runs projects of a working copy.</summary>
    private readonly AuditTestRun _runner = new(context.Workspace, cancel);

    /// <summary>Tests the change of <paramref name="target"/> in <paramref name="worker"/>'s copy.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="index">Position in the sample, from 1.</param>
    /// <param name="target">The member and its kind.</param>
    /// <exception cref="IOException">A rewritten assembly could not be written or restored: the copy can no longer be trusted.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A member Cecil cannot rewrite is a member no test can see broken; only file errors stop the audit.")]
    public AuditMutant Run(AuditWorker worker, int index, SyntheticTarget target)
    {
        var stopwatch = Stopwatch.StartNew();
        var start = new AuditMutant(index, target, AuditOutcome.NotReached)
        {
            File = Path.GetRelativePath(context.Workspace.Root, target.Edits[0].Path).Replace('\\', '/'),
        };
        if (cancel.IsCancellationRequested)
        {
            return start with { Outcome = AuditOutcome.Stopped, Note = "cancelled before it started" };
        }

        Mutation mutation;
        try
        {
            mutation = MutationInjector.Inject([.. context.Assemblies.Select(a => Path.Combine(worker.Copy, a))], target.Member, worker.Backups, target.Kind);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OutOfMemoryException))
        {
            return start with { Note = $"the mutant could not be written in ({ex.GetType().Name}: {ex.Message})", Seconds = stopwatch.Elapsed.TotalSeconds };
        }

        using (mutation)
        {
            hooks.Injected?.Invoke(index);
            var result = Judge(worker, start with { MutatedMethods = mutation.MutatedMethods }, mutation);
            return result with { Seconds = stopwatch.Elapsed.TotalSeconds };
        }
    }

    /// <summary>Runs the tests the change reaches and judges them against the selection; the mutant is in.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="mutant">The change so far.</param>
    /// <param name="mutation">The mutant, lifted for the runs without it.</param>
    private AuditMutant Judge(AuditWorker worker, AuditMutant mutant, Mutation mutation)
    {
        if (mutation.MutatedMethods == 0)
        {
            return mutant with { Note = "the mutant changed no compiled code: its kind does not apply to the member (no two-way branch, a by-reference return) or no audited test output holds it" };
        }

        // Only a test project whose output holds a rewritten copy can load the mutant.
        var rewritten = mutation.Backups.Keys.Select(static f => Paths.Normalize(Path.GetDirectoryName(f)!)).ToHashSet(Paths.Comparer);
        var reached = context.Projects.Where(p => p.Outputs.Any(o => rewritten.Contains(Paths.Normalize(Path.Combine(worker.Copy, o))))).ToList();
        if (reached.Count == 0)
        {
            return mutant with { Note = "only test projects the audit does not run hold it" };
        }

        mutant = mutant with { Reached = [.. reached.Select(static p => p.Relative)] };
        AnalysisResult analysis;
        try
        {
            analysis = context.Selector.Select(mutant.Target);
        }
        catch (OperationCanceledException)
        {
            return mutant with { Outcome = AuditOutcome.Stopped, Note = "cancelled" };
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return mutant with { Outcome = AuditOutcome.Inconclusive, Note = $"TestDetta's analysis of the change failed: {ex.Message}" };
        }

        mutant = mutant with { Seen = Seen(analysis), SelectedShare = Selections.SelectedShare(analysis.Tests, context.Maps) };
        var broken = new List<AuditClass>();
        var notCounted = new List<AuditClass>();
        var caughtByCrash = false;
        string? inconclusive = null;
        foreach (var project in reached)
        {
            var decision = analysis.Tests.FirstOrDefault(t => Paths.Comparer.Equals(t.Project.Path, project.Project.Path));
            var full = Run(worker, project, context.UserArguments, $"m{mutant.Index}-full");
            if (Stopped(full) is { } stopped)
            {
                return mutant with { Outcome = AuditOutcome.Stopped, Note = stopped };
            }

            IReadOnlyCollection<string> failed = full.Failed;
            if (!full.Complete)
            {
                // A crash or hang: only what the selection left out can hold a miss, so that part runs again with the change.
                if (decision?.Mode == TestRunMode.All)
                {
                    caughtByCrash = true;
                    continue;
                }

                if (Outside(project, decision) is not { } outsideArguments)
                {
                    inconclusive ??= $"{project.Relative}: {full.Problem}, and the classes the selection left out cannot be run apart under your own filter";
                    continue;
                }

                var outside = Run(worker, project, outsideArguments, $"m{mutant.Index}-outside");
                if (Stopped(outside) is { } outsideStopped)
                {
                    return mutant with { Outcome = AuditOutcome.Stopped, Note = outsideStopped };
                }

                if (!outside.Complete)
                {
                    inconclusive ??= $"{project.Relative}: {full.Problem}; the classes the selection left out: {outside.Problem}";
                    continue;
                }

                if (outside.Failed.All(project.BaselineFailed.Contains))
                {
                    caughtByCrash = true;
                    continue;
                }

                failed = outside.Failed;
            }

            var leftOut = new List<string>();
            foreach (var testClass in failed.Where(c => !project.BaselineFailed.Contains(c)).Order(StringComparer.Ordinal))
            {
                if (Selections.Runs(decision, testClass))
                {
                    broken.Add(Class(project, testClass, true, decision));
                }
                else
                {
                    leftOut.Add(testClass);
                }
            }

            if (leftOut.Count > 0 && Confirm(worker, mutant.Index, project, decision, leftOut, mutation) is var (missed, dropped, problem))
            {
                if (problem is not null)
                {
                    if (problem.StartsWith("stopped", StringComparison.Ordinal) || problem == "cancelled")
                    {
                        return mutant with { Outcome = AuditOutcome.Stopped, Note = problem };
                    }

                    inconclusive ??= $"{project.Relative}: {problem}";
                }

                broken.AddRange(missed);
                notCounted.AddRange(dropped);
            }
        }

        var outcome = broken.Any(static c => !c.Selected) ? AuditOutcome.Missed
            : inconclusive is not null ? AuditOutcome.Inconclusive
            : broken.Count > 0 || caughtByCrash ? AuditOutcome.Caught
            : AuditOutcome.DidNotBreak;
        return mutant with
        {
            Outcome = outcome,
            Broken = broken,
            NotCounted = notCounted,
            CaughtByCrash = caughtByCrash && outcome == AuditOutcome.Caught,
            Note = outcome == AuditOutcome.Inconclusive ? inconclusive : null,
        };
    }

    /// <summary>
    /// Runs the left-out classes alone with the change, then those that fail again alone without it (the original
    /// assemblies back for that run). A class that passes alone failed only next to other tests; one that fails without
    /// the change fails alone anyway; neither is counted. The rest are misses. A run that does not complete leaves the
    /// classes unjudged, with the problem.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <param name="index">The change's index.</param>
    /// <param name="project">The test project.</param>
    /// <param name="decision">The selection's decision for it.</param>
    /// <param name="leftOut">Classes that broke and were left out.</param>
    /// <param name="mutation">The mutant, lifted for the control run.</param>
    private (List<AuditClass> Missed, List<AuditClass> Dropped, string? Problem) Confirm(
        AuditWorker worker, int index, AuditProject project, TestProjectSelection? decision, List<string> leftOut, Mutation mutation)
    {
        if (TestFilter.WithinUserFilter(leftOut, project.Syntax, context.UserArguments) is not var (filter, others))
        {
            return ([], [], "your own filter cannot be joined with a class filter, so the classes left out could not be confirmed alone");
        }

        List<string> arguments = [.. filter, .. others];
        var alone = Run(worker, project, arguments, $"m{index}-alone");
        if (Stopped(alone) is { } stopped)
        {
            return ([], [], stopped);
        }

        if (!alone.Complete)
        {
            return ([], [], $"the classes left out, run alone with the change: {alone.Problem}");
        }

        var dropped = leftOut.Where(c => !alone.Failed.Contains(c))
            .Select(c => Class(project, c, false, decision) with { Note = "failed only next to other tests: passed alone with the change" })
            .ToList();
        var again = leftOut.FindAll(alone.Failed.Contains);
        if (again.Count == 0)
        {
            return ([], dropped, null);
        }

        AuditRunOutcome control;
        using (mutation.Lift())
        {
            control = Run(worker, project, [.. TestFilter.WithinUserFilter(again, project.Syntax, context.UserArguments)!.Value.Filter, .. others], $"m{index}-control");
        }

        if (Stopped(control) is { } controlStopped)
        {
            return ([], dropped, controlStopped);
        }

        var missed = new List<AuditClass>();
        foreach (var testClass in again)
        {
            if (control.Complete && control.Failed.Contains(testClass))
            {
                dropped.Add(Class(project, testClass, false, decision) with { Note = "fails alone without the change too" });
                continue;
            }

            // A control run that did not complete proves nothing; the failure with the change stands.
            missed.Add(Class(project, testClass, false, decision) with
            {
                Note = control.Complete ? "fails alone with the change and passes alone without it" : $"fails alone with the change; the run without it: {control.Problem}",
            });
        }

        return (missed, dropped, null);
    }

    /// <summary>
    /// The arguments that run what the selection left out of a project, or null when that cannot be run apart: a
    /// filter of the user's cannot be joined with an excluding class filter, so then nothing is run.
    /// </summary>
    /// <param name="project">The test project.</param>
    /// <param name="decision">The selection's decision for it, or null when it is not affected.</param>
    private IReadOnlyList<string>? Outside(AuditProject project, TestProjectSelection? decision)
    {
        if (decision is not { Mode: TestRunMode.Filtered, TestClasses.Count: > 0 })
        {
            return context.UserArguments;
        }

        var userFilters = TestRunArguments.BeforeRunSettings(context.UserArguments)
            .Any(static a => a.StartsWith("--filter", StringComparison.Ordinal) || a.StartsWith("--treenode-filter", StringComparison.Ordinal));
        return userFilters ? null : [.. TestFilter.Arguments(decision.TestClasses, exclude: true, project.Syntax), .. context.UserArguments];
    }

    /// <summary>Runs a project of the worker's copy with the change's limits: its baseline time times five, cut to the budget.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="project">The test project.</param>
    /// <param name="arguments">Arguments after the project.</param>
    /// <param name="label">Name of the run.</param>
    private AuditRunOutcome Run(AuditWorker worker, AuditProject project, IReadOnlyList<string> arguments, string label)
    {
        var own = TimeSpan.FromSeconds(Math.Max(MinimumTimeout.TotalSeconds, project.BaselineSeconds * TimeoutFactor * Math.Max(1, context.Parallelism)));
        var (limits, capped) = context.Budget.For(own, idle: null);
        var output = project.Outputs.Count > 0 ? Path.Combine(worker.Copy, project.Outputs[0]) : null;
        return _runner.Run(
            new AuditRunRequest(worker.Copy, project.Project, arguments, label) { NoBuild = true, Timeout = limits.Timeout, CappedByBudget = capped, Environment = worker.Environment },
            output);
    }

    /// <summary>Why a run stopped the change (the budget or a cancellation), or null when it did not.</summary>
    /// <param name="run">The run.</param>
    private static string? Stopped(AuditRunOutcome run) => run.Cancelled ? "cancelled" : run.OutOfBudget ? "stopped: the budget ran out" : null;

    /// <summary>A failed class with the selection's decision chain for it.</summary>
    /// <param name="project">Its test project.</param>
    /// <param name="testClass">Runtime class name.</param>
    /// <param name="selected">True when the selection ran it.</param>
    /// <param name="decision">The selection's decision for the project, or null when the change does not affect it.</param>
    private AuditClass Class(AuditProject project, string testClass, bool selected, TestProjectSelection? decision)
    {
        const string NotAffected = "the change does not affect this test project";
        var classDecision = decision?.Decisions.FirstOrDefault(d => string.Equals(d.TestClass, testClass, StringComparison.Ordinal));
        return new AuditClass(project.Relative, testClass, selected)
        {
            Decision = classDecision?.Kind.ToString() ?? decision?.Mode switch
            {
                null => "not-affected",
                TestRunMode.All => nameof(ClassDecisionKind.WholeProject),
                _ => "not-a-candidate",
            },
            Evidence = classDecision?.Evidence ?? decision?.Reason ?? NotAffected,
            ProjectMode = decision?.Mode.ToString() ?? "not-affected",
            ProjectReason = decision?.Reason ?? NotAffected,
            MapCommit = context.Maps.TryGetValue(project.Project.Path, out var map) ? map.Commit : null,
        };
    }

    /// <summary>What TestDetta saw change, one line per changed declaration.</summary>
    /// <param name="analysis">The analysis of the change.</param>
    private static List<string> Seen(AnalysisResult analysis)
    {
        var seen = new List<string>();
        foreach (var change in analysis.Impact.CodeChanges)
        {
            if (change.AffectsWholeProject)
            {
                seen.Add($"{change.Path}: a change to the whole project");
            }

            seen.AddRange(change.Symbols.Select(static s => $"{s.TypeName}{(s.MemberName is { } member ? "." + member : string.Empty)} {s.Kind.ToString().ToLowerInvariant()}"));
        }

        seen.AddRange(analysis.Impact.CosmeticFiles.Select(static f => $"{f}: whitespace or comments only"));
        return seen.Count == 0 ? ["no code change seen"] : seen;
    }
}
