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

    /// <summary>
    /// Variables every run of a change gets, as the team's own environment would give them (a runner set to another
    /// language, say); those <see cref="AuditProcess"/> sets itself still win.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Called with each run's name (<c>m3-full</c>, <c>m3-alone</c> …) before it starts.</summary>
    public Action<string>? Running { get; init; }

    /// <summary>
    /// Makes each run in place of <c>dotnet test</c>: the request, and true when the mutant is in (false for a run
    /// without the change). With it, the judging rules are tested without building or running anything.
    /// </summary>
    public Func<AuditRunRequest, bool, AuditRunOutcome>? Runs { get; init; }
}

/// <summary>
/// A change whose judging or restoring failed after it had a result: the result is kept (a confirmed miss stays a
/// miss), and the audit stops, since the copy can no longer be trusted.
/// </summary>
/// <param name="result">What the change came to before the failure.</param>
/// <param name="inner">The failure.</param>
internal sealed class AuditChangeFailedException(AuditMutant result, Exception inner) : Exception(inner.Message, inner)
{
    /// <summary>What the change came to before the failure.</summary>
    public AuditMutant Result { get; } = result;
}

/// <summary>
/// Tests one change (docs/design/audit.md, "Judging"): writes the member's mutant into the worker's copy, runs every
/// audited test project that can load it completely, and compares what broke with <c>td test</c>'s selection for that
/// change. Every doubt goes against TestDetta: a class the selection left out counts as a miss once it fails alone
/// with the change and not alone without it; a class it ran counts as caught only once it fails alone with the change
/// and passes alone without it, the same test from the other side; a run that does not complete with the change runs
/// again without it, and a part the selection left out that completes only without the change is a miss, while a part
/// it ran counts as caught only when it does not complete with the change a second time. The copy's assemblies are
/// restored before it returns, whatever happens.
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

    /// <summary>Runs projects of a working copy; made on first use, so tests that replace the runs need no work folder.</summary>
    private AuditTestRun Runner => field ??= new AuditTestRun(context.Workspace, cancel);

    /// <summary>Tests the change of <paramref name="target"/> in <paramref name="worker"/>'s copy.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="index">Position in the sample, from 1.</param>
    /// <param name="target">The member and its kind.</param>
    /// <exception cref="AuditChangeFailedException">Judging or restoring failed after the change had a result; the result is in it.</exception>
    /// <exception cref="IOException">A rewritten assembly could not be written: the copy can no longer be trusted.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A member Cecil cannot rewrite is a member no test can see broken; any failure while judging keeps the result so far.")]
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

        var mutant = start with { MutatedMethods = mutation.MutatedMethods };
        var verdict = new Verdict(cancel);
        AuditMutant result;
        try
        {
            hooks.Injected?.Invoke(index);
            result = Judge(worker, mutant, mutation, verdict);
        }
        catch (OperationCanceledException)
        {
            // Between two runs: a miss confirmed before it stays one.
            result = verdict.Stopped(verdict.Mutant ?? mutant, "cancelled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The result so far is kept (a confirmed miss stays a miss); the copy is restored as far as it goes.
            var kept = verdict.Stopped(verdict.Mutant ?? mutant, $"stopped by an error: {ex.Message}") with { Seconds = stopwatch.Elapsed.TotalSeconds };
            try
            {
                mutation.Dispose();
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                // The first failure is the one reported; the audit stops either way.
            }

            throw new AuditChangeFailedException(kept, ex);
        }

        result = result with { Seconds = stopwatch.Elapsed.TotalSeconds };
        try
        {
            mutation.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recorded before the restore: the change's result counts, and the audit stops.
            throw new AuditChangeFailedException(result, ex);
        }

        return result;
    }

    /// <summary>Runs the tests the change reaches and judges them against the selection; the mutant is in.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="mutant">The change so far.</param>
    /// <param name="mutation">The mutant, lifted for the runs without it.</param>
    /// <param name="verdict">Collects what the runs show.</param>
    private AuditMutant Judge(AuditWorker worker, AuditMutant mutant, Mutation mutation, Verdict verdict)
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
        AuditSelection selection;
        try
        {
            selection = context.Selector.Select(mutant.Target);
        }
        catch (OperationCanceledException)
        {
            return mutant with { Outcome = AuditOutcome.Stopped, Note = "cancelled" };
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return mutant with { Outcome = AuditOutcome.Inconclusive, Note = $"TestDetta's analysis of the change failed: {ex.Message}" };
        }

        var analysis = selection.Analysis;
        mutant = mutant with { Seen = Seen(analysis), SelectedShare = Selections.SelectedShare(analysis.Tests, context.Maps), EditPositions = selection.Positions };
        return JudgeRuns(worker, mutant, mutation, reached, analysis.Tests, verdict);
    }

    /// <summary>Judges the runs of a change whose selection is made (<see cref="JudgeRuns"/>), for tests that replace the runs.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="mutant">The change so far.</param>
    /// <param name="mutation">The mutant, lifted for the runs without it.</param>
    /// <param name="reached">The audited test projects that can load it.</param>
    /// <param name="decisions">The selection's decisions per affected test project.</param>
    internal AuditMutant Judge(AuditWorker worker, AuditMutant mutant, Mutation mutation, IReadOnlyList<AuditProject> reached, IReadOnlyList<TestProjectSelection> decisions) =>
        JudgeRuns(worker, mutant, mutation, reached, decisions, new Verdict(cancel));

    /// <summary>
    /// Runs each project the change reaches and judges what failed against the selection's decision for it; then, when
    /// no miss showed, confirms the failures of classes the selection ran.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <param name="mutant">The change so far, with what the analysis saw.</param>
    /// <param name="mutation">The mutant, lifted for the runs without it.</param>
    /// <param name="reached">The audited test projects that can load it.</param>
    /// <param name="decisions">The selection's decisions per affected test project.</param>
    /// <param name="verdict">Collects what the runs show.</param>
    private AuditMutant JudgeRuns(AuditWorker worker, AuditMutant mutant, Mutation mutation, IReadOnlyList<AuditProject> reached, IReadOnlyList<TestProjectSelection> decisions, Verdict verdict)
    {
        verdict.Mutant = mutant;
        var pending = new List<(AuditProject Project, TestProjectSelection? Decision, List<string> Classes)>();
        foreach (var project in reached)
        {
            var decision = decisions.FirstOrDefault(t => Paths.Comparer.Equals(t.Project.Path, project.Project.Path));
            if (JudgeProject(new Scope(worker, mutant.Index, project, decision, mutation, verdict), pending) is { } stop)
            {
                return verdict.Stopped(mutant, stop);
            }
        }

        // Classes the selection ran count only once confirmed; with a miss the change is missed anyway, and they are
        // listed unconfirmed.
        foreach (var (project, decision, classes) in pending)
        {
            var scope = new Scope(worker, mutant.Index, project, decision, mutation, verdict);
            if (verdict.Missed)
            {
                verdict.NotCounted.AddRange(classes.Select(c => Class(scope, c, true) with { Note = "not confirmed: the change is missed anyway" }));
                continue;
            }

            if (ConfirmSelected(scope, classes) is { } stop)
            {
                return verdict.Stopped(mutant, stop);
            }
        }

        return cancel.IsCancellationRequested ? verdict.Stopped(mutant, "cancelled") : verdict.Final(mutant);
    }

    /// <summary>One project of one change: what its runs need.</summary>
    /// <param name="Worker">The worker.</param>
    /// <param name="Index">The change's index.</param>
    /// <param name="Project">The test project.</param>
    /// <param name="Decision">The selection's decision for it; null when the change does not affect it.</param>
    /// <param name="Mutation">The mutant, lifted for the runs without it.</param>
    /// <param name="Verdict">Collects what the runs show.</param>
    private sealed record Scope(AuditWorker Worker, int Index, AuditProject Project, TestProjectSelection? Decision, Mutation Mutation, Verdict Verdict);

    /// <summary>
    /// Runs a project completely with the change and judges it. A complete run's new failures are split by the
    /// selection: those it left out are confirmed alone (<see cref="ConfirmLeftOut"/>), those it ran wait for
    /// <see cref="ConfirmSelected"/>. A run that does not complete is judged by parts (<see cref="Incomplete"/>).
    /// </summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="pending">Receives the classes the selection ran that failed, to confirm.</param>
    /// <returns>Why the change stopped (a cancellation), or null.</returns>
    private string? JudgeProject(Scope scope, List<(AuditProject, TestProjectSelection?, List<string>)> pending)
    {
        var full = Whole(scope.Project, Run(scope, context.UserArguments, "full"));
        if (Stopped(full) is { } stopped)
        {
            return stopped;
        }

        return full.Complete ? Failures(scope, full, pending) : Incomplete(scope, full, pending);
    }

    /// <summary>Splits a complete run's new failures by the selection and confirms the left-out ones.</summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="run">A complete run with the change.</param>
    /// <param name="pending">Receives the classes the selection ran that failed.</param>
    /// <returns>Why the change stopped, or null.</returns>
    private string? Failures(Scope scope, AuditRunOutcome run, List<(AuditProject, TestProjectSelection?, List<string>)> pending)
    {
        var selected = new List<string>();
        var leftOut = new List<string>();
        foreach (var testClass in New(scope.Project, run).Order(StringComparer.Ordinal))
        {
            (Selections.Runs(scope.Decision, testClass) ? selected : leftOut).Add(testClass);
        }

        if (leftOut.Count > 0 && ConfirmLeftOut(scope, leftOut) is { } stopped)
        {
            return stopped;
        }

        if (selected.Count > 0)
        {
            pending.Add((scope.Project, scope.Decision, selected));
        }

        return null;
    }

    /// <summary>
    /// A run of the whole project that did not complete with the change (a crash, a hang, an error of the run). Each
    /// part is run again on its own, and a part that does not complete with the change runs once more without it:
    /// <list type="bullet">
    /// <item>the selection ran nothing here: the whole run, which did not complete, runs without the change; completing
    /// then, the run is repeated with the change, and not completing again makes the change a miss (the part the
    /// selection left out breaks with it; see <see cref="LeftOutAgain"/>); not completing without it, inconclusive;</item>
    /// <item>it ran everything: the run is repeated with the change; not completing again, and completing without it,
    /// is a catch; a repeat that completes is judged by its failures (none: not counted);</item>
    /// <item>it ran some classes: first the classes it left out, as for nothing; then, when no miss showed there, the
    /// classes it ran, as for everything; when neither part fails on its own, the change is inconclusive.</item>
    /// </list>
    /// </summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="full">The incomplete run with the change.</param>
    /// <param name="pending">Receives the classes the selection ran that failed.</param>
    /// <returns>Why the change stopped, or null.</returns>
    private string? Incomplete(Scope scope, AuditRunOutcome full, List<(AuditProject, TestProjectSelection?, List<string>)> pending)
    {
        var (project, decision, verdict) = (scope.Project, scope.Decision, scope.Verdict);
        if (decision?.Mode == TestRunMode.All)
        {
            var again = Part(scope, context.UserArguments, "again", existing: null, whole: true);
            switch (again.Kind)
            {
                case PartKind.Stopped:
                    return again.Note;
                case PartKind.Crashes:
                    verdict.Catch(CrashClass(scope, true, $"the run did not complete with the change ({full.Problem}), nor again ({again.Note}), and completes without it"));
                    return null;
                case PartKind.Inconclusive:
                    verdict.Unconfirmed($"{project.Relative}: {again.Note}");
                    return null;
            }

            if (New(project, again.Run!).Count == 0)
            {
                verdict.NotCounted.Add(CrashClass(scope, true, $"the run did not complete once with the change ({full.Problem}) and passed when run again"));
                return null;
            }

            return Failures(scope, again.Run!, pending);
        }

        if (decision is not { Mode: TestRunMode.Filtered, TestClasses: { Count: > 0 } classes })
        {
            // Skipped, not affected, or a filter of no class: the whole run is the part left out.
            var control = Part(scope, context.UserArguments, "full", existing: full, whole: true);
            switch (control.Kind)
            {
                case PartKind.Stopped:
                    return control.Note;
                case PartKind.Crashes:
                    return LeftOutAgain(scope, context.UserArguments, whole: true, pending,
                        $"the run did not complete with the change ({full.Problem}), nor again, and completes without it; the selection ran none of this project");
                default:
                    verdict.Doubt($"{project.Relative}: {control.Note}");
                    return null;
            }
        }

        if (Excluding(classes, project) is not { } leftOutArguments || Within(classes, project) is not { } selectedArguments)
        {
            verdict.Doubt($"{project.Relative}: {full.Problem}, and your own filter cannot be joined with a class filter to run its parts apart");
            return null;
        }

        var left = Part(scope, leftOutArguments, "left-out", existing: null, whole: false);
        switch (left.Kind)
        {
            case PartKind.Stopped:
                return left.Note;
            case PartKind.Crashes:
                return LeftOutAgain(scope, leftOutArguments, whole: false, pending,
                    $"the run did not complete with the change ({full.Problem}); the classes the selection left out, run apart, do not complete with it ({left.Note}), nor again, and complete without it");
            case PartKind.Inconclusive:
                verdict.Doubt($"{project.Relative}: {full.Problem}; the classes the selection left out: {left.Note}");
                return null;
        }

        var leftFailed = New(project, left.Run!);
        if (leftFailed.Count > 0 && Failures(scope, left.Run!, pending) is { } leftStopped)
        {
            return leftStopped;
        }

        if (verdict.MissedIn(project.Relative))
        {
            return null;
        }

        var selected = Part(scope, selectedArguments, "selected", existing: null, whole: false);
        switch (selected.Kind)
        {
            case PartKind.Stopped:
                return selected.Note;
            case PartKind.Crashes:
                verdict.Catch(CrashClass(scope, true, $"the run did not complete with the change ({full.Problem}); the classes the selection ran, run apart, do not complete with it ({selected.Note}) and complete without it"));
                return null;
            case PartKind.Inconclusive:
                verdict.Unconfirmed($"{project.Relative}: {full.Problem}; the classes the selection ran: {selected.Note}");
                return null;
        }

        if (New(project, selected.Run!).Count > 0)
        {
            return Failures(scope, selected.Run!, pending);
        }

        if (leftFailed.Count == 0)
        {
            verdict.Doubt($"{project.Relative}: the run did not complete with the change ({full.Problem}), but neither the classes the selection ran nor those it left out fail when run apart");
        }

        return null;
    }

    /// <summary>
    /// Runs the left-out classes alone with the change. One that fails alone runs alone without it (the original
    /// assemblies back for that run): passing there, or a run without it that cannot tell, makes it a miss; failing
    /// there too, it fails alone anyway and is not counted. One that passes alone failed only next to other tests and is
    /// not counted; one that did not run alone (a filter that matched none of its tests) leaves the change inconclusive.
    /// When the run alone does not complete, the same run without the change decides: a class that passes there is a miss.
    /// </summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="leftOut">Classes that broke and were left out.</param>
    /// <returns>Why the change stopped, or null.</returns>
    private string? ConfirmLeftOut(Scope scope, List<string> leftOut)
    {
        var (project, verdict) = (scope.Project, scope.Verdict);
        if (Within(leftOut, project) is not { } arguments)
        {
            verdict.Doubt($"{project.Relative}: your own filter cannot be joined with a class filter, so the classes left out could not be confirmed alone");
            return null;
        }

        var alone = Run(scope, arguments, "alone");
        if (Stopped(alone) is { } stopped)
        {
            return stopped;
        }

        if (!alone.Complete)
        {
            var without = Lifted(scope, arguments, "alone-without");
            if (Stopped(without) is { } withoutStopped)
            {
                return withoutStopped;
            }

            if (!without.Complete)
            {
                verdict.Doubt($"{project.Relative}: the classes left out, run alone: {alone.Problem} with the change, and {without.Problem} without it");
                return null;
            }

            foreach (var testClass in leftOut)
            {
                if (!without.Executed.Contains(testClass))
                {
                    verdict.Doubt($"{project.Relative}: {testClass}: its run alone did not complete with the change ({alone.Problem}) and ran none of its tests without it");
                }
                else if (without.Failed.Contains(testClass))
                {
                    verdict.NotCounted.Add(Class(scope, testClass, false) with { Note = "fails alone without the change too" });
                }
                else
                {
                    verdict.Miss(Class(scope, testClass, false) with { Note = $"its run alone did not complete with the change ({alone.Problem}); alone without it, it passes" });
                }
            }

            return null;
        }

        var again = new List<string>();
        foreach (var testClass in leftOut)
        {
            if (!alone.Executed.Contains(testClass))
            {
                verdict.Doubt($"{project.Relative}: {testClass} failed with the change, but its run alone ran none of its tests, so it could not be confirmed");
            }
            else if (!alone.Failed.Contains(testClass))
            {
                verdict.NotCounted.Add(Class(scope, testClass, false) with { Note = "failed only next to other tests: passed alone with the change" });
            }
            else
            {
                again.Add(testClass);
            }
        }

        if (again.Count == 0)
        {
            return null;
        }

        var control = Lifted(scope, Within(again, project)!, "control");
        if (Stopped(control) is { } controlStopped)
        {
            return controlStopped;
        }

        foreach (var testClass in again)
        {
            if (control.Complete && control.Executed.Contains(testClass) && control.Failed.Contains(testClass))
            {
                verdict.NotCounted.Add(Class(scope, testClass, false) with { Note = "fails alone without the change too" });
                continue;
            }

            // A run without the change that cannot tell proves nothing; the failure with the change stands.
            verdict.Miss(Class(scope, testClass, false) with
            {
                Note = control.Complete && control.Executed.Contains(testClass)
                    ? "fails alone with the change and passes alone without it"
                    : $"fails alone with the change; the run without it: {control.Problem ?? "ran none of its tests"}",
            });
        }

        return null;
    }

    /// <summary>
    /// Confirms the classes the selection ran that failed, the way left-out ones are confirmed: alone with the change
    /// (passing there, it failed only next to other tests and is not counted), then alone without it (failing there
    /// too, it fails alone anyway and is not counted). Only a class that fails alone with the change and passes alone
    /// without it is caught. A run without the change that cannot tell leaves the class unconfirmed: unlike a miss, a
    /// catch needs the proof. When the run alone does not complete, the same run without the change decides.
    /// </summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="classes">Classes the selection ran that failed with the change.</param>
    /// <returns>Why the change stopped, or null.</returns>
    private string? ConfirmSelected(Scope scope, List<string> classes)
    {
        var (project, verdict) = (scope.Project, scope.Verdict);
        if (Within(classes, project) is not { } arguments)
        {
            verdict.Unconfirmed($"{project.Relative}: your own filter cannot be joined with a class filter, so the classes the selection ran could not be confirmed alone");
            return null;
        }

        var alone = Run(scope, arguments, "selected-alone");
        if (Stopped(alone) is { } stopped)
        {
            return stopped;
        }

        if (!alone.Complete)
        {
            var without = Lifted(scope, arguments, "selected-alone-without");
            if (Stopped(without) is { } withoutStopped)
            {
                return withoutStopped;
            }

            foreach (var testClass in classes)
            {
                if (!without.Complete || !without.Executed.Contains(testClass))
                {
                    verdict.Unconfirmed($"{project.Relative}: {testClass}: its run alone did not complete with the change ({alone.Problem}), and the run without it {without.Problem ?? "ran none of its tests"}");
                }
                else if (without.Failed.Contains(testClass))
                {
                    verdict.NotCounted.Add(Class(scope, testClass, true) with { Note = "fails alone without the change too" });
                }
                else
                {
                    verdict.Catch(Class(scope, testClass, true) with { Note = $"its run alone did not complete with the change ({alone.Problem}); alone without it, it passes", Crash = true });
                }
            }

            return null;
        }

        var again = new List<string>();
        foreach (var testClass in classes)
        {
            if (!alone.Executed.Contains(testClass))
            {
                verdict.Unconfirmed($"{project.Relative}: {testClass} failed with the change, but its run alone ran none of its tests, so it could not be confirmed");
            }
            else if (!alone.Failed.Contains(testClass))
            {
                verdict.NotCounted.Add(Class(scope, testClass, true) with { Note = "failed only next to other tests: passed alone with the change" });
            }
            else
            {
                again.Add(testClass);
            }
        }

        if (again.Count == 0)
        {
            return null;
        }

        var control = Lifted(scope, Within(again, project)!, "selected-control");
        if (Stopped(control) is { } controlStopped)
        {
            return controlStopped;
        }

        foreach (var testClass in again)
        {
            if (!control.Complete || !control.Executed.Contains(testClass))
            {
                verdict.Unconfirmed($"{project.Relative}: {testClass} fails alone with the change, but the run without it {control.Problem ?? "ran none of its tests"}");
            }
            else if (control.Failed.Contains(testClass))
            {
                verdict.NotCounted.Add(Class(scope, testClass, true) with { Note = "fails alone without the change too" });
            }
            else
            {
                verdict.Catch(Class(scope, testClass, true) with { Note = "fails alone with the change and passes alone without it" });
            }
        }

        return null;
    }

    /// <summary>How a part of a project's tests behaved: see <see cref="Part"/>.</summary>
    private enum PartKind
    {
        /// <summary>It completed with the change; judge its failures.</summary>
        Completed,

        /// <summary>It did not complete with the change and completed without it: the change breaks it.</summary>
        Crashes,

        /// <summary>It did not complete without the change either: nothing can be told.</summary>
        Inconclusive,

        /// <summary>A run was cancelled.</summary>
        Stopped,
    }

    /// <summary>What <see cref="Part"/> found.</summary>
    /// <param name="Kind">How the part behaved.</param>
    /// <param name="Run">The complete run with the change, for <see cref="PartKind.Completed"/>.</param>
    /// <param name="Note">Why it crashes, is inconclusive or stopped.</param>
    private sealed record PartResult(PartKind Kind, AuditRunOutcome? Run, string? Note);

    /// <summary>
    /// Runs part of a project with the change (or takes the run already made), and when that does not complete, the same
    /// invocation without the change: a crash or hang only with the change is the change's doing.
    /// </summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="arguments">The part's arguments.</param>
    /// <param name="label">Name of its runs.</param>
    /// <param name="existing">A run with the change already made with these arguments, or null to make one.</param>
    /// <param name="whole">True when the part is the whole project, run as the baseline ran it (<see cref="Whole"/>).</param>
    private PartResult Part(Scope scope, IReadOnlyList<string> arguments, string label, AuditRunOutcome? existing, bool whole)
    {
        AuditRunOutcome Checked(AuditRunOutcome run) => whole ? Whole(scope.Project, run) : run;
        var with = existing ?? Checked(Run(scope, arguments, label));
        if (Stopped(with) is { } stopped)
        {
            return new PartResult(PartKind.Stopped, null, stopped);
        }

        if (with.Complete)
        {
            return new PartResult(PartKind.Completed, with, null);
        }

        var without = Checked(Lifted(scope, arguments, label + "-without"));
        if (Stopped(without) is { } withoutStopped)
        {
            return new PartResult(PartKind.Stopped, null, withoutStopped);
        }

        return without.Complete
            ? new PartResult(PartKind.Crashes, with, with.Problem)
            : new PartResult(PartKind.Inconclusive, with, $"{with.Problem} with the change, and {without.Problem} without it");
    }

    /// <summary>
    /// A run of the whole project, as the baseline ran it, that reports fewer test classes than the baseline did is not
    /// complete, whatever else it says: the test host died before the rest ran, in a way no message recognized (a crash
    /// message in another language, say). Its classes that never ran must not count as passed.
    /// </summary>
    /// <param name="project">The test project.</param>
    /// <param name="run">A run with the user's arguments only.</param>
    internal static AuditRunOutcome Whole(AuditProject project, AuditRunOutcome run) =>
        run.Complete && run.Classes < project.BaselineClasses
            ? run with { Complete = false, Problem = $"only {run.Classes} of the {project.BaselineClasses} test classes of the run without changes reported" }
            : run;

    /// <summary>The classes that failed in a run and not in the baseline.</summary>
    /// <param name="project">The test project.</param>
    /// <param name="run">A run with the change.</param>
    private static List<string> New(AuditProject project, AuditRunOutcome run) => [.. run.Failed.Where(c => !project.BaselineFailed.Contains(c))];

    /// <summary>
    /// Why the user's arguments cannot be joined with a class filter for <paramref name="syntax"/>, or null when they
    /// can: an xUnit v3 class or query filter (it joins two class filters with "or" and refuses a query next to one),
    /// a TUnit tree-node filter (it takes one). Such a project cannot have its classes run apart, so it is not audited:
    /// only catches could be counted in it, never a miss.
    /// </summary>
    /// <param name="syntax">The project's filter syntax.</param>
    /// <param name="userArguments">The user's <c>dotnet test</c> arguments.</param>
    internal static string? Unjoinable(TestFilterSyntax syntax, IReadOnlyList<string> userArguments)
    {
        string[] conflicting = syntax switch
        {
            TestFilterSyntax.TUnit => ["--treenode-filter"],
            TestFilterSyntax.XunitV3 => ["--filter-class", "--filter-not-class", "--filter-query"],
            _ => [],
        };
        var options = TestRunArguments.BeforeRunSettings(userArguments);
        var found = options.FirstOrDefault(a => conflicting.Any(c => a == c || a.StartsWith(c + "=", StringComparison.Ordinal) || a.StartsWith(c + ":", StringComparison.Ordinal)));
        return found is null
            ? null
            : $"your own {found.Split('=', ':')[0]} cannot be joined with a class filter, so the classes TestDetta leaves out could not be run apart: auditing it could count catches but never a miss";
    }

    /// <summary>The arguments that run <paramref name="classes"/> within the user's own filter, or null when they cannot be joined.</summary>
    /// <param name="classes">Runtime class names.</param>
    /// <param name="project">The test project.</param>
    private List<string>? Within(IReadOnlyCollection<string> classes, AuditProject project) =>
        TestFilter.WithinUserFilter(classes, project.Syntax, context.UserArguments) is var (filter, others) ? [.. filter, .. others] : null;

    /// <summary>
    /// The arguments that run every class of a project but <paramref name="classes"/>, within the user's own filter, or
    /// null when they cannot be joined (<see cref="Unjoinable"/>).
    /// </summary>
    /// <param name="classes">Runtime class names the selection ran.</param>
    /// <param name="project">The test project.</param>
    private List<string>? Excluding(IReadOnlyCollection<string> classes, AuditProject project) => ExcludingWithin(classes, project.Syntax, context.UserArguments);

    /// <summary>
    /// See <see cref="Excluding"/>. A VSTest filter of the user's is joined with the excluding filter as
    /// <c>(user)&amp;(excluding)</c>, as <see cref="TestFilter.WithinUserFilter"/> joins an including one; the other
    /// runners take the excluding options next to the user's own.
    /// </summary>
    /// <param name="classes">Runtime class names the selection ran.</param>
    /// <param name="syntax">The project's filter syntax.</param>
    /// <param name="userArguments">The user's <c>dotnet test</c> arguments.</param>
    internal static List<string>? ExcludingWithin(IReadOnlyCollection<string> classes, TestFilterSyntax syntax, IReadOnlyList<string> userArguments)
    {
        if (Unjoinable(syntax, userArguments) is not null)
        {
            return null;
        }

        var ours = TestFilter.Arguments(classes, exclude: true, syntax);
        if (syntax != TestFilterSyntax.VSTest)
        {
            return [.. ours, .. userArguments];
        }

        var end = userArguments.ToList().IndexOf("--");
        end = end < 0 ? userArguments.Count : end;
        var userFilters = new List<string>();
        var rest = new List<string>(userArguments.Count);
        for (var i = 0; i < end; i++)
        {
            if (userArguments[i] == "--filter" && i + 1 < end)
            {
                userFilters.Add(userArguments[++i]);
            }
            else if (userArguments[i].StartsWith("--filter=", StringComparison.Ordinal))
            {
                userFilters.Add(userArguments[i]["--filter=".Length..]);
            }
            else
            {
                rest.Add(userArguments[i]);
            }
        }

        rest.AddRange(userArguments.Skip(end));
        var combined = userFilters.Count == 0 ? ours[1] : string.Join('&', userFilters.Select(static f => $"({f})")) + $"&({ours[1]})";
        return ["--filter", combined, .. rest];
    }

    /// <summary>Runs a project of the worker's copy with the change's limit: its baseline time times five, at least <see cref="MinimumTimeout"/>.</summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="arguments">Arguments after the project.</param>
    /// <param name="label">Name of the run.</param>
    /// <remarks>
    /// The budget does not cut a change's runs: a change that started runs to its end within its own limits, so the
    /// changes judged stay a prefix of the sample's order (docs/design/audit.md, "The sample, and how it stays unbiased").
    /// </remarks>
    private AuditRunOutcome Run(Scope scope, IReadOnlyList<string> arguments, string label) => Run(scope, arguments, label, withChange: true);

    /// <summary>See <see cref="Run(Scope, IReadOnlyList{string}, string)"/>.</summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="arguments">Arguments after the project.</param>
    /// <param name="label">Name of the run.</param>
    /// <param name="withChange">False for a run with the mutant lifted, for <see cref="AuditHooks.Runs"/>.</param>
    private AuditRunOutcome Run(Scope scope, IReadOnlyList<string> arguments, string label, bool withChange)
    {
        var project = scope.Project;
        var own = TimeSpan.FromSeconds(Math.Max(MinimumTimeout.TotalSeconds, project.BaselineSeconds * TimeoutFactor * Math.Max(1, context.Parallelism)));
        var output = project.Outputs.Count > 0 ? Path.Combine(scope.Worker.Copy, project.Outputs[0]) : null;
        var name = $"m{scope.Index}-{label}";
        hooks.Running?.Invoke(name);
        var request = new AuditRunRequest(scope.Worker.Copy, project.Project, arguments, name) { NoBuild = true, Timeout = own, Environment = scope.Worker.Environment };
        return hooks.Runs is { } fake ? fake(request, withChange) : Runner.Run(request, output);
    }

    /// <summary>Runs a project with the original assemblies back in place of the mutant, then puts the mutant back.</summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="arguments">Arguments after the project.</param>
    /// <param name="label">Name of the run.</param>
    private AuditRunOutcome Lifted(Scope scope, IReadOnlyList<string> arguments, string label)
    {
        using (scope.Mutation.Lift())
        {
            return Run(scope, arguments, label, withChange: false);
        }
    }

    /// <summary>Why a run stopped the change (a cancellation), or null when it did not.</summary>
    /// <param name="run">The run.</param>
    private static string? Stopped(AuditRunOutcome run) => run.Cancelled ? "cancelled" : null;

    /// <summary>A failed class with the selection's decision chain for it.</summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="testClass">Runtime class name.</param>
    /// <param name="selected">True when the selection ran it.</param>
    private AuditClass Class(Scope scope, string testClass, bool selected)
    {
        const string NotAffected = "the change does not affect this test project";
        var (project, decision) = (scope.Project, scope.Decision);
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

    /// <summary>
    /// A part the selection left out that did not complete with the change and completed without it runs once more with
    /// the change. Not completing again, the change breaks it: a miss. Completing, the first run's crash did not come
    /// back (a test host that died of the machine's load, not of the change, as a mutant of a member nothing calls
    /// showed in the corpus on 2026-10-08), so it proves no miss; nor does the repeat prove there is none, so the change
    /// is inconclusive, and the repeat's own failures are judged like any complete run's.
    /// </summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="arguments">The part's arguments.</param>
    /// <param name="whole">True when the part is the whole project, as the baseline ran it.</param>
    /// <param name="pending">Receives the classes the selection ran that failed.</param>
    /// <param name="missNote">What the runs showed, for the miss.</param>
    /// <returns>Why the change stopped, or null.</returns>
    private string? LeftOutAgain(Scope scope, IReadOnlyList<string> arguments, bool whole,
        List<(AuditProject, TestProjectSelection?, List<string>)> pending, string missNote)
    {
        var again = Run(scope, arguments, whole ? "again" : "left-out-again");
        if (whole)
        {
            again = Whole(scope.Project, again);
        }

        if (Stopped(again) is { } stopped)
        {
            return stopped;
        }

        if (!again.Complete)
        {
            scope.Verdict.Miss(CrashClass(scope, false, missNote));
            return null;
        }

        scope.Verdict.Doubt($"{scope.Project.Relative}: the classes the selection left out did not complete once with the change and completed when run again");
        return New(scope.Project, again).Count > 0 ? Failures(scope, again, pending) : null;
    }

    /// <summary>
    /// A part of a project whose run did not complete with the change, in place of a class: no class can be named,
    /// since the run's report is partial.
    /// </summary>
    /// <param name="scope">The project and the change.</param>
    /// <param name="selected">True for the part the selection ran, false for the part it left out.</param>
    /// <param name="note">What the runs showed.</param>
    private AuditClass CrashClass(Scope scope, bool selected, string note) =>
        Class(scope, selected ? AuditClass.SelectedPart : AuditClass.LeftOutPart, selected) with
        {
            Crash = true,
            Note = note,
            Decision = scope.Decision?.Mode switch
            {
                null => "not-affected",
                TestRunMode.All => nameof(ClassDecisionKind.WholeProject),
                TestRunMode.Filtered => selected ? "the classes it selected" : "the classes it left out",
                _ => nameof(TestRunMode.Skipped),
            },
            Evidence = scope.Decision?.Reason ?? "the change does not affect this test project",
        };

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

    /// <summary>
    /// What one change's runs showed, collected as they come, so that whatever stops the change later (a cancellation,
    /// an error, a failed restore) keeps a miss confirmed before it. The outcome's order: a miss; else a doubt that could
    /// hide one (inconclusive); else a confirmed catch; else a catch that could not be confirmed (inconclusive); else
    /// nothing broke.
    /// </summary>
    /// <param name="cancel">The audit's cancellation: a miss "confirmed" by runs cut short by it is no confirmation.</param>
    private sealed class Verdict(CancellationToken cancel)
    {
        /// <summary>The change with what the analysis saw, once it ran; the base of a result kept after a failure.</summary>
        public AuditMutant? Mutant { get; set; }

        /// <summary>Confirmed catches (selected) and misses (not selected).</summary>
        public List<AuditClass> Broken { get; } = [];

        /// <summary>Failures not counted, with the reason.</summary>
        public List<AuditClass> NotCounted { get; } = [];

        /// <summary>The first doubt that could hide a miss: a part or class left out whose runs cannot tell.</summary>
        public string? Inconclusive { get; private set; }

        /// <summary>The first failure of a part or class the selection ran that could not be confirmed.</summary>
        public string? UnconfirmedCatch { get; private set; }

        /// <summary>True once a miss is confirmed.</summary>
        public bool Missed => Broken.Any(static c => !c.Selected);

        /// <summary>True once a miss is confirmed in the given project.</summary>
        /// <param name="project">Its path relative to the repository root.</param>
        public bool MissedIn(string project) => Broken.Any(c => !c.Selected && c.Project == project);

        /// <summary>Adds a confirmed miss, unless the audit was cancelled meanwhile: runs cut short confirm nothing.</summary>
        /// <param name="testClass">The class or part.</param>
        public void Miss(AuditClass testClass)
        {
            if (!cancel.IsCancellationRequested)
            {
                Broken.Add(testClass);
            }
        }

        /// <summary>Adds a confirmed catch.</summary>
        /// <param name="testClass">The class or part.</param>
        public void Catch(AuditClass testClass) => Broken.Add(testClass);

        /// <summary>Records a doubt that could hide a miss.</summary>
        /// <param name="note">What could not be told.</param>
        public void Doubt(string note) => Inconclusive ??= note;

        /// <summary>Records a catch that could not be confirmed.</summary>
        /// <param name="note">What could not be told.</param>
        public void Unconfirmed(string note) => UnconfirmedCatch ??= note;

        /// <summary>The change's outcome from what was collected.</summary>
        /// <param name="mutant">The change so far.</param>
        public AuditMutant Final(AuditMutant mutant)
        {
            var outcome = Missed ? AuditOutcome.Missed
                : Inconclusive is not null ? AuditOutcome.Inconclusive
                : Broken.Count > 0 ? AuditOutcome.Caught
                : UnconfirmedCatch is not null ? AuditOutcome.Inconclusive
                : AuditOutcome.DidNotBreak;
            return mutant with
            {
                Outcome = outcome,
                Broken = [.. Broken],
                NotCounted = [.. NotCounted],
                CaughtByCrash = outcome == AuditOutcome.Caught && Broken.Any(static c => c.Crash),
                Note = outcome == AuditOutcome.Inconclusive ? Inconclusive ?? UnconfirmedCatch : null,
            };
        }

        /// <summary>The change cut short: missed when a miss was confirmed before, else stopped.</summary>
        /// <param name="mutant">The change so far.</param>
        /// <param name="reason">What stopped it.</param>
        public AuditMutant Stopped(AuditMutant mutant, string reason) => Missed
            ? Final(mutant) with { Note = reason == "cancelled" ? "the miss was confirmed before the change was cancelled" : $"the miss was confirmed before the change was cut short ({reason})" }
            : mutant with { Outcome = AuditOutcome.Stopped, Note = reason, Broken = [.. Broken], NotCounted = [.. NotCounted] };
    }
}
