using TestDetta.Cli;
using TestDetta.Core;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// The corpus evaluator's strict judging (docs/evaluation.md, "Judging"; the rules of <c>td audit</c>) against made-up
/// runs, in milliseconds: a hang in a part TestDetta left out is a miss, a crash counts as caught only when the part it
/// ran crashes again, a class it ran is caught only once confirmed alone with and without the mutant, and a class whose
/// run alone ran none of its tests is inconclusive. Every doubt goes against TestDetta.
/// </summary>
[Trait("Category", "Eval")]
public sealed class EvalJudgingTests
{
    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    private readonly List<RunAsk> _asks = [];

    [Fact]
    public void A_hang_only_in_a_project_TestDetta_left_out_is_a_miss()
    {
        var judgement = Judge([Project("Left.Tests", Skipped("Left.Tests")), Project("Ran.Tests", All("Ran.Tests"))],
            ask => ask is { WithMutant: true, Part: RunPart.Whole, Project.Name: "Left.Tests" } ? Hung() : Ok());

        Assert.False(judgement.Inconclusive);
        Assert.Equal([$"Left.Tests: {AuditClass.LeftOutPart}"], judgement.Missed);
        Assert.False(judgement.ByClasses);
        Assert.Contains(_asks, static a => a is { Project.Name: "Left.Tests", Part: RunPart.Whole, WithMutant: false });
    }

    [Fact]
    public void A_hang_in_a_left_out_project_that_does_not_come_back_is_inconclusive_not_a_miss()
    {
        // Meziantou.Analyzer, 2026-10-08: a mutant of a member nothing calls, and the test host died once under load.
        var judgement = Judge([Project("Left.Tests", Skipped("Left.Tests"))],
            static ask => ask is { WithMutant: true, Part: RunPart.Whole, Again: false } ? Hung() : Ok());

        Assert.True(judgement.Inconclusive);
        Assert.Empty(judgement.Missed);
        Assert.Contains("completed when run again", judgement.Note, StringComparison.Ordinal);
        Assert.Contains(_asks, static a => a is { Part: RunPart.Whole, WithMutant: true, Again: true });
    }

    [Fact]
    public void A_hang_in_a_left_out_project_whose_repeat_fails_a_class_is_judged_by_that_class()
    {
        var judgement = Judge([Project("Left.Tests", Skipped("Left.Tests"))], static ask => ask switch
        {
            { WithMutant: true, Part: RunPart.Whole, Again: false } => Hung(),
            { WithMutant: true } => Ok("T.B"),
            _ => Ok(),
        });

        Assert.Equal(["Left.Tests: T.B"], judgement.Missed);
    }

    [Fact]
    public void A_hang_in_the_classes_left_out_of_a_filtered_project_that_does_not_come_back_is_inconclusive()
    {
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask is { WithMutant: true, Again: false, Part: RunPart.Whole or RunPart.Except } ? Hung() : Ok());

        Assert.True(judgement.Inconclusive);
        Assert.Empty(judgement.Missed);
        Assert.DoesNotContain(_asks, static a => a.Label == "selected");
    }

    [Fact]
    public void A_hang_in_a_left_out_project_that_hangs_without_the_mutant_too_is_inconclusive()
    {
        var judgement = Judge([Project("Left.Tests", Skipped("Left.Tests"))], static ask => ask.Part == RunPart.Whole ? Hung() : Ok());

        Assert.True(judgement.Inconclusive);
        Assert.Empty(judgement.Missed);
        Assert.Contains("without it", judgement.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_run_that_reports_fewer_classes_than_the_baseline_did_not_complete()
    {
        var judgement = Judge([Project("Left.Tests", Skipped("Left.Tests"), baselineClasses: 3)],
            static ask => ask is { WithMutant: true, Part: RunPart.Whole } ? Ok() with { Classes = 2 } : Ok() with { Classes = 3 });

        Assert.Equal([$"Left.Tests: {AuditClass.LeftOutPart}"], judgement.Missed);
    }

    [Fact]
    public void A_flaky_selected_failure_is_not_counted_as_caught()
    {
        // T.A failed in the full run and passes alone with the mutant: it failed only next to other tests.
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask is { Part: RunPart.Whole, WithMutant: true } ? Ok("T.A") : Ok());

        Assert.False(judgement.Inconclusive);
        Assert.Empty(judgement.Killed);
        Assert.Contains(judgement.NotCounted, static n => n.Contains("T.A: failed only next to other tests", StringComparison.Ordinal));
    }

    [Fact]
    public void A_selected_failure_that_fails_alone_without_the_mutant_too_is_not_counted()
    {
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask is { Part: RunPart.Whole } && !ask.WithMutant ? Ok() : Ok("T.A"));

        Assert.Empty(judgement.Killed);
        Assert.Contains(judgement.NotCounted, static n => n.Contains("fails alone without the mutant too", StringComparison.Ordinal));
    }

    [Fact]
    public void A_selected_failure_is_caught_once_it_fails_alone_with_the_mutant_and_passes_alone_without_it_in_one_run_each()
    {
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A", "T.B"))],
            static ask => ask.WithMutant ? Ok("T.A", "T.B") : Ok());

        Assert.False(judgement.Inconclusive);
        Assert.Equal(["P.Tests: T.A", "P.Tests: T.B"], judgement.Killed);
        Assert.Empty(judgement.Missed);
        Assert.Equal(
            [(RunPart.Whole, true, ""), (RunPart.Only, true, "T.A,T.B"), (RunPart.Only, false, "T.A,T.B")],
            _asks.Select(static a => (a.Part, a.WithMutant, string.Join(",", a.Classes))));
    }

    [Fact]
    public void A_selected_failure_whose_run_without_the_mutant_cannot_tell_is_an_unconfirmed_catch()
    {
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask.WithMutant ? Ok("T.A") : Hung());

        Assert.True(judgement.Inconclusive);
        Assert.Empty(judgement.Killed);
        Assert.Contains("the run without it", judgement.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_left_out_class_whose_run_alone_ran_none_of_its_tests_is_inconclusive_never_passed_alone()
    {
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask switch
            {
                { Part: RunPart.Whole, WithMutant: true } => Ok("T.B"),
                { Part: RunPart.Only } => Ok() with { Executed = None },
                _ => Ok(),
            });

        Assert.True(judgement.Inconclusive);
        Assert.Empty(judgement.Missed);
        Assert.Contains("ran none of its tests", judgement.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_left_out_class_that_fails_alone_with_the_mutant_is_a_miss_even_when_the_run_without_it_cannot_tell()
    {
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask.WithMutant ? Ok("T.B") : Hung());

        Assert.False(judgement.Inconclusive);
        Assert.Equal(["P.Tests: T.B"], judgement.Missed);
    }

    [Fact]
    public void A_crash_counts_as_caught_only_when_the_part_TestDetta_ran_crashes_again()
    {
        // The project runs whole; the first run hangs, the second hangs too, and it completes without the mutant.
        var caught = Judge([Project("P.Tests", All("P.Tests"))], static ask => ask.WithMutant ? Hung() : Ok());
        Assert.True(caught.CaughtByCrash);
        Assert.Equal([$"P.Tests: {AuditClass.SelectedPart}"], caught.Killed);

        // A crash once that passes when run again is no catch.
        _asks.Clear();
        var once = Judge([Project("P.Tests", All("P.Tests"))], static ask => ask is { WithMutant: true, Again: false, Part: RunPart.Whole } ? Hung() : Ok());
        Assert.False(once.CaughtByCrash);
        Assert.Empty(once.Killed);
        Assert.False(once.Inconclusive);
    }

    [Fact]
    public void A_crash_in_a_filtered_project_is_judged_by_its_parts_left_out_first()
    {
        // The classes TestDetta left out hang with the mutant and complete without it: a miss, and the selected part does not run.
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask.WithMutant && ask.Part is RunPart.Whole or RunPart.Except ? Hung() : Ok());

        Assert.Equal([$"P.Tests: {AuditClass.LeftOutPart}"], judgement.Missed);
        Assert.DoesNotContain(_asks, static a => a.Label == "selected");
    }

    [Fact]
    public void A_crash_in_a_filtered_project_whose_parts_both_pass_apart_is_inconclusive()
    {
        var judgement = Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))],
            static ask => ask is { WithMutant: true, Part: RunPart.Whole } ? Hung() : Ok());

        Assert.True(judgement.Inconclusive);
        Assert.Contains("neither the classes TestDetta ran nor those it left out fail when run apart", judgement.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirming_reuses_the_run_of_the_selected_part_and_stops_once_the_verdict_is_decided()
    {
        // Two projects; the first confirms a catch, so the second's selected failure is not confirmed (it cannot change
        // the verdict) but stays among the failed classes.
        var judgement = Judge(
            [Project("A.Tests", Filtered("A.Tests", "T.A")), Project("B.Tests", Filtered("B.Tests", "T.B"))],
            static ask => ask.WithMutant ? Ok("T.A", "T.B") with { Failed = ask.Project.Name == "A.Tests" ? Set("T.A") : Set("T.B") } : Ok());

        Assert.Equal(["A.Tests: T.A", "B.Tests: T.B"], judgement.Killed);
        Assert.DoesNotContain(_asks, static a => a is { Project.Name: "B.Tests", Part: RunPart.Only });
    }

    [Fact]
    public void A_class_another_selector_left_out_is_confirmed_even_when_the_verdict_is_decided()
    {
        var judgement = Judge(
            [Project("A.Tests", Filtered("A.Tests", "T.A")), Project("B.Tests", Filtered("B.Tests", "T.B"))],
            static ask => ask.WithMutant ? Ok() with { Failed = ask.Project.Name == "A.Tests" ? Set("T.A") : Set("T.B") } : Ok(),
            othersSelect: static (project, _) => !project.Contains("B.Tests", StringComparison.Ordinal));

        Assert.Contains(_asks, static a => a is { Project.Name: "B.Tests", Part: RunPart.Only, WithMutant: false });
        Assert.Equal(["A.Tests: T.A", "B.Tests: T.B"], judgement.Failures);
    }

    [Fact]
    public void A_run_the_source_cannot_make_is_reported_so_the_mutant_runs()
    {
        var judge = new MutantJudge(new Failing(), static (_, _) => true);

        Assert.Throws<RunNeededException>(() => judge.Judge([Project("P.Tests", Filtered("P.Tests", "T.A"))], []));
    }

    [Fact]
    public void The_same_run_asked_for_two_reasons_has_one_key()
    {
        var project = Project("P.Tests", Filtered("P.Tests", "T.A"));
        var selected = new RunAsk(project, RunPart.Only, ["T.A"], true, "selected");
        var alone = new RunAsk(project, RunPart.Only, ["T.A"], true, "selected-alone");
        var again = new RunAsk(project, RunPart.Whole, [], true, "again") { Again = true };

        Assert.Equal(selected.Key, alone.Key);
        Assert.NotEqual(again.Key, (again with { Again = false }).Key);
        Assert.NotEqual(selected.Key, (selected with { WithMutant = false }).Key);
    }

    private Judgement Judge(List<JudgedProject> projects, Func<RunAsk, ProjectRun> runs, Func<string, string, bool>? othersSelect = null) =>
        new MutantJudge(new Fake(this, runs), othersSelect ?? (static (_, _) => true)).Judge(projects, []);

    private static JudgedProject Project(string name, TestProjectSelection decision, int baselineClasses = 0) =>
        new(decision.Project, $"tests/{name}/{name}.csproj", decision, None, baselineClasses);

    private static ProjectNode Node(string name) => new($"/repo/tests/{name}/{name}.csproj", $"/repo/tests/{name}", true, []);

    private static TestProjectSelection Skipped(string name) => new(Node(name), TestRunMode.Skipped, [], "not affected");

    private static TestProjectSelection All(string name) => new(Node(name), TestRunMode.All, [], "everything");

    private static TestProjectSelection Filtered(string name, params string[] classes) => new(Node(name), TestRunMode.Filtered, classes, "some classes");

    private static HashSet<string> Set(params string[] classes) => new(classes, StringComparer.Ordinal);

    private static ProjectRun Ok(params string[] failed) => new(true, null, Set(failed), Set(["T.A", "T.B", "T.C"]), 3);

    private static ProjectRun Hung() => new(false, "timed out", None, None, 0);

    private sealed class Fake(EvalJudgingTests owner, Func<RunAsk, ProjectRun> runs) : IMutantRuns
    {
        public ProjectRun Run(RunAsk ask)
        {
            owner._asks.Add(ask);
            return runs(ask);
        }
    }

    private sealed class Failing : IMutantRuns
    {
        public ProjectRun Run(RunAsk ask) => throw new RunNeededException(ask);
    }
}
