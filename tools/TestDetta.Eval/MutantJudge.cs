using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Eval;

/// <summary>Which tests of a project a judged run runs.</summary>
internal enum RunPart
{
    /// <summary>Every test, as the baseline ran the project.</summary>
    Whole,

    /// <summary>Only the given classes.</summary>
    Only,

    /// <summary>Every class but the given ones.</summary>
    Except,
}

/// <summary>A test project a mutant reaches, with what judging it needs.</summary>
/// <param name="Node">The project.</param>
/// <param name="Relative">Its path relative to the clone, as the outcome cache stores it.</param>
/// <param name="Decision">TestDetta's decision for it; null when the change does not affect it.</param>
/// <param name="BaselineFailed">Classes that failed in the run without mutants: a failure of theirs is not the mutant's.</param>
/// <param name="BaselineClasses">Test classes in the baseline's report; a whole run that reports fewer did not complete.</param>
internal sealed record JudgedProject(ProjectNode Node, string Relative, TestProjectSelection? Decision, IReadOnlySet<string> BaselineFailed, int BaselineClasses)
{
    /// <summary>The project's name in <c>Project: Class</c> (its file name without extension).</summary>
    public string Name => Path.GetFileNameWithoutExtension(Node.Path);
}

/// <summary>One run a judgement asks for.</summary>
/// <param name="Project">The project.</param>
/// <param name="Part">Which of its tests run.</param>
/// <param name="Classes">The classes of <see cref="RunPart.Only"/> or <see cref="RunPart.Except"/>, sorted; empty for <see cref="RunPart.Whole"/>.</param>
/// <param name="WithMutant">False for a run with the mutant lifted.</param>
/// <param name="Label">Name of the run's logs, after <c>synthetic-N</c>.</param>
internal sealed record RunAsk(JudgedProject Project, RunPart Part, IReadOnlyList<string> Classes, bool WithMutant, string Label)
{
    /// <summary>True for the second whole run with the mutant after a first that did not complete.</summary>
    public bool Again { get; init; }

    /// <summary>
    /// What makes two asks the same run, whatever they are for: the project, the part, the classes, the mutant on or
    /// off, and for a whole run with the mutant, which attempt. The outcome cache stores runs under it.
    /// </summary>
    public string Key
    {
        get
        {
            var classes = Classes.Count == 0 ? "-" : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('\n', Classes))))[..16];
            return $"{Project.Relative}|{Part}|{(WithMutant ? "with" : "without")}|{(Again ? 2 : 1)}|{classes}";
        }
    }
}

/// <summary>Makes (or finds) the runs a judgement asks for.</summary>
internal interface IMutantRuns
{
    /// <summary>The run <paramref name="ask"/> describes.</summary>
    /// <param name="ask">What to run.</param>
    /// <exception cref="RunNeededException">The run is not stored and this source cannot make it.</exception>
    ProjectRun Run(RunAsk ask);
}

/// <summary>A judgement needs a run its source does not have: the mutant must run.</summary>
/// <param name="ask">The run.</param>
internal sealed class RunNeededException(RunAsk ask) : Exception($"No stored run {ask.Key}")
{
    /// <summary>The run.</summary>
    public RunAsk Ask { get; } = ask;
}

/// <summary>What the runs showed about one mutant.</summary>
/// <param name="Killed">Classes (<c>Project: Class</c>) that fail because of the mutant, or a part of a project named for a crash.</param>
/// <param name="Missed">Of those, what TestDetta left out.</param>
/// <param name="Inconclusive">True when a doubt could hide a miss, or a catch could not be confirmed and nothing else decided.</param>
/// <param name="CaughtByCrash">True when caught and a part TestDetta ran crashed or hung only with the mutant.</param>
/// <param name="Note">Why the mutant is inconclusive; null otherwise.</param>
internal sealed record Judgement(IReadOnlyList<string> Killed, IReadOnlyList<string> Missed, bool Inconclusive, bool CaughtByCrash, string? Note)
{
    /// <summary>
    /// The failures every selector is judged on (<c>Project: Class</c>): classes that fail alone with the mutant and not
    /// alone without it (or whose run without it cannot tell, for a class TestDetta left out), and projects that do not
    /// build with it. Only meaningful when <see cref="ByClasses"/> is true.
    /// </summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>True when every reached project's full run completed, so the failures are named by class and other selectors can be judged on them.</summary>
    public bool ByClasses { get; init; }

    /// <summary>Failures not counted, with the reason (<c>Project: Class: why</c>).</summary>
    public IReadOnlyList<string> NotCounted { get; init; } = [];
}

/// <summary>
/// Judges one mutant from its runs by the strict rules (docs/evaluation.md, "Judging"; the same rules as
/// <c>td audit</c>, docs/design/audit.md): every doubt goes against TestDetta. A class TestDetta left out that failed
/// counts as a miss once it fails alone with the mutant and not alone without it (a run without it that cannot tell
/// leaves the miss standing); a class it ran counts as caught only once it fails alone with the mutant and passes
/// alone without it; a run that does not complete with the mutant runs again without it, and a part TestDetta left out
/// that completes only without it is a miss, while a part it ran counts as caught only when it does not complete with
/// the mutant a second time. A class whose run alone ran none of its tests is inconclusive, never "passed alone".
/// </summary>
/// <param name="runs">Makes or finds the runs.</param>
/// <param name="othersSelect">
/// True when every other selector (drift modes, project level, dotnet-tia) runs a class of a project (normalized path):
/// a class one of them leaves out is confirmed even when the mutant's own verdict no longer depends on it.
/// </param>
internal sealed class MutantJudge(IMutantRuns runs, Func<string, string, bool> othersSelect)
{
    /// <summary>The rules this class applies, as the reports and the outcome cache name them.</summary>
    public const string Rules = "strict (2026-10-07)";

    /// <summary>Confirmed catches (selected) and misses (left out), as <c>Project: Class</c>.</summary>
    private readonly List<(string Name, bool Selected, bool Crash)> _broken = [];

    /// <summary>Classes that failed and were not counted, with the reason.</summary>
    private readonly List<string> _notCounted = [];

    /// <summary>Selected classes whose confirmation could not change the verdict, kept with the failures for the precision figure.</summary>
    private readonly List<string> _notNeeded = [];

    /// <summary>The first doubt that could hide a miss.</summary>
    private string? _doubt;

    /// <summary>The first failure of a part or class TestDetta ran that could not be confirmed.</summary>
    private string? _unconfirmed;

    /// <summary>True once a whole project's run with the mutant did not complete.</summary>
    private bool _incomplete;

    /// <summary>True once a miss is confirmed.</summary>
    private bool Missed => _broken.Exists(static b => !b.Selected);

    /// <summary>Judges the mutant in <paramref name="projects"/>.</summary>
    /// <param name="projects">The test projects it reaches.</param>
    /// <param name="unbuilt">Test projects that do not build with it (a generator mutant): a failure no rerun changes.</param>
    /// <exception cref="RunNeededException">A run is needed that the source does not have.</exception>
    public Judgement Judge(IReadOnlyList<JudgedProject> projects, IReadOnlyList<JudgedProject> unbuilt)
    {
        foreach (var project in unbuilt)
        {
            var name = $"{project.Name}: {GeneratorMutants.DoesNotBuild}";
            _broken.Add((name, EvalSteps.IsSelected(project.Decision is null ? [] : [project.Decision], project.Node.Path, GeneratorMutants.DoesNotBuild), false));
        }

        var pending = new List<(JudgedProject Project, List<string> Classes)>();
        foreach (var project in projects)
        {
            var full = Whole(project, runs.Run(new RunAsk(project, RunPart.Whole, [], WithMutant: true, string.Empty)));
            if (full.Complete)
            {
                Failures(project, full, pending);
            }
            else
            {
                _incomplete = true;
                Incomplete(project, full, pending);
            }
        }

        foreach (var (project, classes) in pending)
        {
            // Once the verdict is decided (a miss, a doubt, or a catch confirmed), confirming more classes changes it no
            // more; only classes another selector left out still need it, for that selector's result.
            var decided = Missed || _doubt is not null || _broken.Exists(static b => b.Selected);
            var confirm = decided ? classes.FindAll(c => !othersSelect(project.Node.Path, c)) : classes;
            _notNeeded.AddRange(classes.Except(confirm, StringComparer.Ordinal).Select(c => $"{project.Name}: {c}"));
            if (confirm.Count > 0)
            {
                ConfirmSelected(project, confirm);
            }
        }

        return Final();
    }

    /// <summary>The judgement from what was collected: a miss; else a doubt; else a catch; else an unconfirmed catch; else nothing broke.</summary>
    private Judgement Final()
    {
        var missed = _broken.Where(static b => !b.Selected).Select(static b => b.Name).ToList();
        var failures = _broken.Select(static b => b.Name).ToList();
        var killed = new List<string>(failures);
        killed.AddRange(_notNeeded);
        var inconclusive = missed.Count == 0 && (_doubt is not null || (_broken.Count == 0 && _unconfirmed is not null));
        var note = missed.Count > 0 ? null : _doubt ?? (_broken.Count == 0 ? _unconfirmed : null);
        return new Judgement(
            inconclusive ? [] : killed,
            missed,
            inconclusive,
            !inconclusive && missed.Count == 0 && _broken.Exists(static b => b.Crash),
            note)
        {
            Failures = failures,
            ByClasses = !_incomplete,
            NotCounted = [.. _notCounted],
        };
    }

    /// <summary>
    /// A whole project's run that reports fewer test classes than the baseline did did not complete, whatever else it
    /// says: the test host died before the rest ran, in a way no message recognized.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="run">A whole run.</param>
    internal static ProjectRun Whole(JudgedProject project, ProjectRun run) =>
        run.Complete && run.Classes < project.BaselineClasses
            ? run with { Complete = false, Problem = $"only {run.Classes} of the {project.BaselineClasses} test classes of the run without mutants reported" }
            : run;

    /// <summary>The classes that failed in a run and not in the baseline, sorted.</summary>
    /// <param name="project">The project.</param>
    /// <param name="run">A run with the mutant.</param>
    private static List<string> New(JudgedProject project, ProjectRun run) =>
        [.. run.Failed.Where(c => !project.BaselineFailed.Contains(c)).Order(StringComparer.Ordinal)];

    /// <summary>Splits a complete run's new failures by TestDetta's decision and confirms the left-out ones.</summary>
    /// <param name="project">The project.</param>
    /// <param name="run">A complete run with the mutant.</param>
    /// <param name="pending">Receives the failed classes TestDetta ran, confirmed after every project.</param>
    private void Failures(JudgedProject project, ProjectRun run, List<(JudgedProject, List<string>)> pending)
    {
        var selected = new List<string>();
        var leftOut = new List<string>();
        foreach (var testClass in New(project, run))
        {
            (Selections.Runs(project.Decision, testClass) ? selected : leftOut).Add(testClass);
        }

        if (leftOut.Count > 0)
        {
            ConfirmLeftOut(project, leftOut);
        }

        if (selected.Count > 0)
        {
            pending.Add((project, selected));
        }
    }

    /// <summary>
    /// A whole run that did not complete with the mutant. Each part runs again on its own, and a part that does not
    /// complete with the mutant runs once more without it: TestDetta ran nothing here (the whole run is the part left
    /// out: completing without the mutant is a miss); it ran everything (the run is repeated with the mutant: not
    /// completing again, and completing without it, is a catch); it ran some classes (first the classes it left out,
    /// as for nothing, then, when no miss showed, the classes it ran, as for everything; when neither part fails on
    /// its own, the mutant is inconclusive).
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="full">The incomplete run with the mutant.</param>
    /// <param name="pending">Receives the failed classes TestDetta ran.</param>
    private void Incomplete(JudgedProject project, ProjectRun full, List<(JudgedProject, List<string>)> pending)
    {
        var decision = project.Decision;
        if (decision?.Mode == TestRunMode.All)
        {
            var again = Part(new RunAsk(project, RunPart.Whole, [], true, "again") { Again = true }, existing: null);
            switch (again.Kind)
            {
                case PartKind.Crashes:
                    Catch(project, AuditClass.SelectedPart, crash: true);
                    return;
                case PartKind.Inconclusive:
                    _unconfirmed ??= $"{project.Name}: {again.Note}";
                    return;
            }

            if (New(project, again.Run!).Count == 0)
            {
                _notCounted.Add($"{project.Name}: the run did not complete once with the mutant ({full.Problem}) and passed when run again");
                return;
            }

            Failures(project, again.Run!, pending);
            return;
        }

        if (decision is not { Mode: TestRunMode.Filtered, TestClasses: { Count: > 0 } classes })
        {
            // Skipped, not affected, or a filter of no class: the whole run is the part left out.
            var control = Part(new RunAsk(project, RunPart.Whole, [], true, string.Empty), existing: full);
            if (control.Kind == PartKind.Crashes)
            {
                Miss(project, AuditClass.LeftOutPart);
            }
            else
            {
                _doubt ??= $"{project.Name}: {control.Note}";
            }

            return;
        }

        var sorted = classes.Order(StringComparer.Ordinal).ToList();
        var left = Part(new RunAsk(project, RunPart.Except, sorted, true, "left-out"), existing: null);
        switch (left.Kind)
        {
            case PartKind.Crashes:
                Miss(project, AuditClass.LeftOutPart);
                return;
            case PartKind.Inconclusive:
                _doubt ??= $"{project.Name}: {full.Problem}; the classes TestDetta left out: {left.Note}";
                return;
        }

        var leftFailed = New(project, left.Run!);
        if (leftFailed.Count > 0)
        {
            Failures(project, left.Run!, pending);
        }

        if (_broken.Exists(b => !b.Selected && b.Name.StartsWith(project.Name + ": ", StringComparison.Ordinal)))
        {
            return;
        }

        var selected = Part(new RunAsk(project, RunPart.Only, sorted, true, "selected"), existing: null);
        switch (selected.Kind)
        {
            case PartKind.Crashes:
                Catch(project, AuditClass.SelectedPart, crash: true);
                return;
            case PartKind.Inconclusive:
                _unconfirmed ??= $"{project.Name}: {full.Problem}; the classes TestDetta ran: {selected.Note}";
                return;
        }

        if (New(project, selected.Run!).Count > 0)
        {
            Failures(project, selected.Run!, pending);
            return;
        }

        if (leftFailed.Count == 0)
        {
            _doubt ??= $"{project.Name}: the run did not complete with the mutant ({full.Problem}), but neither the classes TestDetta ran nor those it left out fail when run apart";
        }
    }

    /// <summary>
    /// Runs the left-out classes alone with the mutant, in one run. One that fails alone runs alone without it, the
    /// failing ones together in one run: passing there, or a run without it that cannot tell, makes it a miss; failing
    /// there too, it fails alone anyway and is not counted. One that passes alone failed only next to other tests and is
    /// not counted; one whose run alone ran none of its tests leaves the mutant inconclusive. When the run alone does
    /// not complete, the same run without the mutant decides: a class that passes there is a miss.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="leftOut">Classes that failed and were left out, sorted.</param>
    private void ConfirmLeftOut(JudgedProject project, List<string> leftOut)
    {
        var alone = runs.Run(new RunAsk(project, RunPart.Only, leftOut, true, "confirm"));
        if (!alone.Complete)
        {
            var without = runs.Run(new RunAsk(project, RunPart.Only, leftOut, false, "control"));
            if (!without.Complete)
            {
                _doubt ??= $"{project.Name}: the classes left out, run alone: {alone.Problem} with the mutant, and {without.Problem} without it";
                return;
            }

            foreach (var testClass in leftOut)
            {
                if (!without.Executed.Contains(testClass))
                {
                    _doubt ??= $"{project.Name}: {testClass}: its run alone did not complete with the mutant ({alone.Problem}) and ran none of its tests without it";
                }
                else if (without.Failed.Contains(testClass))
                {
                    _notCounted.Add($"{project.Name}: {testClass}: fails alone without the mutant too");
                }
                else
                {
                    Miss(project, testClass);
                }
            }

            return;
        }

        var again = new List<string>();
        foreach (var testClass in leftOut)
        {
            if (!alone.Executed.Contains(testClass))
            {
                _doubt ??= $"{project.Name}: {testClass} failed with the mutant, but its run alone ran none of its tests, so it could not be confirmed";
            }
            else if (!alone.Failed.Contains(testClass))
            {
                _notCounted.Add($"{project.Name}: {testClass}: failed only next to other tests, passed alone with the mutant");
            }
            else
            {
                again.Add(testClass);
            }
        }

        if (again.Count == 0)
        {
            return;
        }

        var control = runs.Run(new RunAsk(project, RunPart.Only, again, false, "control"));
        foreach (var testClass in again)
        {
            if (control.Complete && control.Executed.Contains(testClass) && control.Failed.Contains(testClass))
            {
                _notCounted.Add($"{project.Name}: {testClass}: fails alone without the mutant too");
                continue;
            }

            // A run without the mutant that cannot tell proves nothing; the failure with the mutant stands.
            Miss(project, testClass);
        }
    }

    /// <summary>
    /// Confirms classes TestDetta ran that failed, the way left-out ones are confirmed: alone with the mutant, all of
    /// them in one run (passing there, a class failed only next to other tests and is not counted), then the ones that
    /// failed there alone without it, in one run (failing there too, it fails alone anyway and is not counted). Only a
    /// class that fails alone with the mutant and passes alone without it is caught; a run without it that cannot tell
    /// leaves the class unconfirmed: unlike a miss, a catch needs the proof.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="classes">Classes TestDetta ran that failed with the mutant, sorted.</param>
    private void ConfirmSelected(JudgedProject project, List<string> classes)
    {
        var alone = runs.Run(new RunAsk(project, RunPart.Only, classes, true, "selected-alone"));
        if (!alone.Complete)
        {
            var without = runs.Run(new RunAsk(project, RunPart.Only, classes, false, "selected-control"));
            foreach (var testClass in classes)
            {
                if (!without.Complete || !without.Executed.Contains(testClass))
                {
                    _unconfirmed ??= $"{project.Name}: {testClass}: its run alone did not complete with the mutant ({alone.Problem}), and the run without it {without.Problem ?? "ran none of its tests"}";
                }
                else if (without.Failed.Contains(testClass))
                {
                    _notCounted.Add($"{project.Name}: {testClass}: fails alone without the mutant too");
                }
                else
                {
                    Catch(project, testClass, crash: true);
                }
            }

            return;
        }

        var again = new List<string>();
        foreach (var testClass in classes)
        {
            if (!alone.Executed.Contains(testClass))
            {
                _unconfirmed ??= $"{project.Name}: {testClass} failed with the mutant, but its run alone ran none of its tests, so it could not be confirmed";
            }
            else if (!alone.Failed.Contains(testClass))
            {
                _notCounted.Add($"{project.Name}: {testClass}: failed only next to other tests, passed alone with the mutant");
            }
            else
            {
                again.Add(testClass);
            }
        }

        if (again.Count == 0)
        {
            return;
        }

        var control = runs.Run(new RunAsk(project, RunPart.Only, again, false, "selected-control"));
        foreach (var testClass in again)
        {
            if (!control.Complete || !control.Executed.Contains(testClass))
            {
                _unconfirmed ??= $"{project.Name}: {testClass} fails alone with the mutant, but the run without it {control.Problem ?? "ran none of its tests"}";
            }
            else if (control.Failed.Contains(testClass))
            {
                _notCounted.Add($"{project.Name}: {testClass}: fails alone without the mutant too");
            }
            else
            {
                Catch(project, testClass, crash: false);
            }
        }
    }

    /// <summary>Adds a confirmed miss.</summary>
    /// <param name="project">The project.</param>
    /// <param name="testClass">The class, or a part's name.</param>
    private void Miss(JudgedProject project, string testClass) => _broken.Add(($"{project.Name}: {testClass}", false, false));

    /// <summary>Adds a confirmed catch.</summary>
    /// <param name="project">The project.</param>
    /// <param name="testClass">The class, or a part's name.</param>
    /// <param name="crash">True when it is a run that did not complete only with the mutant.</param>
    private void Catch(JudgedProject project, string testClass, bool crash) => _broken.Add(($"{project.Name}: {testClass}", true, crash));

    /// <summary>How a part of a project's tests behaved: see <see cref="Part"/>.</summary>
    private enum PartKind
    {
        /// <summary>It completed with the mutant; judge its failures.</summary>
        Completed,

        /// <summary>It did not complete with the mutant and completed without it: the mutant breaks it.</summary>
        Crashes,

        /// <summary>It did not complete without the mutant either: nothing can be told.</summary>
        Inconclusive,
    }

    /// <summary>What <see cref="Part"/> found.</summary>
    /// <param name="Kind">How the part behaved.</param>
    /// <param name="Run">The complete run with the mutant, for <see cref="PartKind.Completed"/>.</param>
    /// <param name="Note">Why it is inconclusive.</param>
    private sealed record PartResult(PartKind Kind, ProjectRun? Run, string? Note);

    /// <summary>
    /// Runs part of a project with the mutant (or takes the run already made), and when that does not complete, the
    /// same part without the mutant: a crash or hang only with the mutant is the mutant's doing.
    /// </summary>
    /// <param name="with">The part with the mutant.</param>
    /// <param name="existing">The run with the mutant already made, or null to make it.</param>
    private PartResult Part(RunAsk with, ProjectRun? existing)
    {
        ProjectRun Checked(ProjectRun run) => with.Part == RunPart.Whole ? Whole(with.Project, run) : run;
        var withRun = existing ?? Checked(runs.Run(with));
        if (withRun.Complete)
        {
            return new PartResult(PartKind.Completed, withRun, null);
        }

        var label = (with.Label.Length == 0 ? "full" : with.Label) + "-without";
        var without = Checked(runs.Run(with with { WithMutant = false, Again = false, Label = label }));
        return without.Complete
            ? new PartResult(PartKind.Crashes, withRun, withRun.Problem)
            : new PartResult(PartKind.Inconclusive, withRun, $"{withRun.Problem} with the mutant, and {without.Problem} without it");
    }
}
