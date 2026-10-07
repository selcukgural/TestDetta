using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// The judging rules of <c>td audit</c> (docs/design/audit.md, "Judging") against made-up runs, so every path of a crash,
/// a hang, a flaky class and a run cut short is checked in milliseconds: <see cref="AuditHooks.Runs"/> answers each run
/// in place of <c>dotnet test</c>. Every doubt must go against TestDetta.
/// </summary>
public sealed class AuditJudgingTests : IDisposable
{
    private static readonly string[] AllClasses = ["T.A", "T.B", "T.C"];

    private readonly CancellationTokenSource _cancel = new();

    private readonly List<(string Label, bool WithChange, IReadOnlyList<string> Arguments)> _calls = [];

    public void Dispose() => _cancel.Dispose();

    [Fact]
    public void A_project_the_selection_skipped_that_hangs_with_the_change_and_completes_without_it_is_a_miss()
    {
        var mutant = Judge(Skipped(), (label, with) => label == "m1-full" && with ? Hung() : Ok());

        Assert.Equal(AuditOutcome.Missed, mutant.Outcome);
        var miss = Assert.Single(mutant.Broken);
        Assert.True(miss.Crash);
        Assert.False(miss.Selected);
        Assert.Equal(AuditClass.LeftOutPart, miss.Name);
        Assert.Contains("completes without it", miss.Note, StringComparison.Ordinal);
        Assert.Equal([("m1-full", true), ("m1-full-without", false)], _calls.Select(static c => (c.Label, c.WithChange)));
    }

    [Fact]
    public void A_whole_project_run_that_reports_fewer_classes_than_the_baseline_is_incomplete_whatever_language_its_crash_message_was_in()
    {
        // The test host died after two of three classes; the report holds them, and the crash message was not recognized.
        var mutant = Judge(Skipped(), (label, with) => label == "m1-full" && with ? Ok() with { Classes = 2 } : Ok(), baselineClasses: 3);

        Assert.Equal(AuditOutcome.Missed, mutant.Outcome);
        Assert.Contains("only 2 of the 3 test classes of the run without changes reported", Assert.Single(mutant.Broken).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_skipped_project_that_hangs_without_the_change_too_is_inconclusive_not_left_out_of_the_worst_case()
    {
        var mutant = Judge(Skipped(), (_, _) => Hung());

        Assert.Equal(AuditOutcome.Inconclusive, mutant.Outcome);
        Assert.Contains("without it", mutant.Note, StringComparison.Ordinal);
        Assert.Empty(mutant.Broken);
    }

    [Fact]
    public void A_flaky_class_the_selection_ran_that_passes_alone_with_the_change_is_not_counted_as_caught()
    {
        var mutant = Judge(Filtered("T.A"), (label, _) => label == "m1-full" ? Ok("T.A") : Ok());

        Assert.Equal(AuditOutcome.DidNotBreak, mutant.Outcome);
        Assert.Empty(mutant.Broken);
        var dropped = Assert.Single(mutant.NotCounted);
        Assert.Equal("T.A", dropped.Name);
        Assert.True(dropped.Selected);
        Assert.Equal("failed only next to other tests: passed alone with the change", dropped.Note);
    }

    [Fact]
    public void A_class_the_selection_ran_is_caught_only_when_it_fails_alone_with_the_change_and_passes_alone_without_it()
    {
        var mutant = Judge(Filtered("T.A"), (label, with) => with && label is "m1-full" or "m1-selected-alone" ? Ok("T.A") : Ok());

        Assert.Equal(AuditOutcome.Caught, mutant.Outcome);
        var caught = Assert.Single(mutant.Broken);
        Assert.True(caught.Selected);
        Assert.Equal("fails alone with the change and passes alone without it", caught.Note);
        Assert.Equal([("m1-full", true), ("m1-selected-alone", true), ("m1-selected-control", false)], _calls.Select(static c => (c.Label, c.WithChange)));
        Assert.Contains("FullyQualifiedName~T.A.", _calls[1].Arguments);
    }

    [Fact]
    public void With_a_miss_the_failures_of_classes_the_selection_ran_are_listed_unconfirmed_and_cost_no_runs()
    {
        var mutant = Judge(Filtered("T.A"), (label, with) => with && label is "m1-full" ? Ok("T.A", "T.B") : with && label == "m1-alone" ? Ok("T.B") : Ok());

        Assert.Equal(AuditOutcome.Missed, mutant.Outcome);
        Assert.Equal("T.B", Assert.Single(mutant.Broken).Name);
        Assert.Equal("not confirmed: the change is missed anyway", Assert.Single(mutant.NotCounted, static c => c.Name == "T.A").Note);
        Assert.DoesNotContain(_calls, static c => c.Label.StartsWith("m1-selected", StringComparison.Ordinal));
    }

    [Fact]
    public void A_selected_class_that_fails_alone_without_the_change_too_is_not_counted()
    {
        var mutant = Judge(Filtered("T.A"), (label, _) => label == "m1-full" || label.StartsWith("m1-selected", StringComparison.Ordinal) ? Ok("T.A") : Ok());

        Assert.Equal(AuditOutcome.DidNotBreak, mutant.Outcome);
        Assert.Equal("fails alone without the change too", Assert.Single(mutant.NotCounted).Note);
    }

    [Fact]
    public void A_selected_failure_whose_run_without_the_change_cannot_tell_leaves_the_change_inconclusive_not_caught()
    {
        var mutant = Judge(Filtered("T.A"), (label, with) => label == "m1-selected-control" ? Hung() : with && label is "m1-full" or "m1-selected-alone" ? Ok("T.A") : Ok());

        Assert.Equal(AuditOutcome.Inconclusive, mutant.Outcome);
        Assert.Empty(mutant.Broken);
        Assert.Contains("the run without it", mutant.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_left_out_class_whose_run_alone_ran_none_of_its_tests_leaves_the_change_inconclusive()
    {
        var mutant = Judge(Filtered("T.A"), (label, _) => label == "m1-full" ? Ok("T.B") : label == "m1-alone" ? Ok() with { Executed = Set() } : Ok());

        Assert.Equal(AuditOutcome.Inconclusive, mutant.Outcome);
        Assert.Contains("ran none of its tests", mutant.Note, StringComparison.Ordinal);
        Assert.Empty(mutant.NotCounted);
    }

    [Fact]
    public void A_left_out_class_that_fails_alone_with_the_change_is_a_miss_even_when_the_run_without_it_cannot_tell()
    {
        var mutant = Judge(Filtered("T.A"), (label, with) => label == "m1-control" ? Hung() : with && label is "m1-full" or "m1-alone" ? Ok("T.B") : Ok());

        Assert.Equal(AuditOutcome.Missed, mutant.Outcome);
        var miss = Assert.Single(mutant.Broken);
        Assert.Equal("T.B", miss.Name);
        Assert.StartsWith("fails alone with the change; the run without it: timed out", miss.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_left_out_class_whose_run_alone_crashes_with_the_change_and_passes_without_it_is_a_miss()
    {
        var mutant = Judge(Filtered("T.A"), (label, with) => label == "m1-full" ? Ok("T.B") : label == "m1-alone" && with ? Hung() : Ok());

        Assert.Equal(AuditOutcome.Missed, mutant.Outcome);
        Assert.Contains("alone without it, it passes", Assert.Single(mutant.Broken).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_project_selection_counts_a_crash_as_caught_only_when_it_crashes_again_and_completes_without_the_change()
    {
        var caught = Judge(All(), (_, with) => with ? Hung() : Ok());
        Assert.Equal(AuditOutcome.Caught, caught.Outcome);
        Assert.True(caught.CaughtByCrash);
        Assert.True(Assert.Single(caught.Broken).Crash);
        Assert.Equal([("m1-full", true), ("m1-again", true), ("m1-again-without", false)], _calls.Select(static c => (c.Label, c.WithChange)));

        // A crash that does not come back is not the change's doing as far as anyone can tell: not counted.
        _calls.Clear();
        var once = Judge(All(), (label, _) => label == "m1-full" ? Hung() : Ok());
        Assert.Equal(AuditOutcome.DidNotBreak, once.Outcome);
        Assert.False(once.CaughtByCrash);
        Assert.True(Assert.Single(once.NotCounted).Crash);
    }

    [Fact]
    public void Under_a_filter_a_crash_in_the_classes_left_out_is_a_miss()
    {
        var mutant = Judge(Filtered("T.A"), (label, with) => with && label is "m1-full" or "m1-left-out" ? Hung() : Ok());

        Assert.Equal(AuditOutcome.Missed, mutant.Outcome);
        Assert.True(Assert.Single(mutant.Broken).Crash);
        Assert.Contains("FullyQualifiedName!~T.A.", _calls.Single(static c => c.Label == "m1-left-out").Arguments);
    }

    [Fact]
    public void Under_a_filter_a_crash_in_the_classes_selected_is_caught_and_one_neither_part_shows_is_inconclusive()
    {
        var caught = Judge(Filtered("T.A"), (label, with) => with && label is "m1-full" or "m1-selected" ? Hung() : Ok());
        Assert.Equal(AuditOutcome.Caught, caught.Outcome);
        Assert.True(caught.CaughtByCrash);

        var unexplained = Judge(Filtered("T.A"), (label, with) => with && label == "m1-full" ? Hung() : Ok());
        Assert.Equal(AuditOutcome.Inconclusive, unexplained.Outcome);
        Assert.Contains("neither the classes the selection ran nor those it left out", unexplained.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_miss_confirmed_before_a_later_run_is_cancelled_is_kept()
    {
        var first = Project("A.Tests");
        var second = Project("B.Tests");
        var trial = Trial([first, second], (request, with) =>
        {
            if (request.Project == second.Project)
            {
                _cancel.Cancel();
                return new AuditRunOutcome(Set(), false, "cancelled") { Cancelled = true };
            }

            return with && request.Label is "m1-full" or "m1-alone" ? Ok("T.B") : Ok();
        });

        var mutant = trial.Judge(Worker(), Mutant(), NoMutation(), [first, second], [Decision(first, TestRunMode.Filtered, "T.A"), Decision(second, TestRunMode.All)]);

        Assert.Equal(AuditOutcome.Missed, mutant.Outcome);
        Assert.Equal("T.B", Assert.Single(mutant.Broken).Name);
        Assert.Equal("the miss was confirmed before the change was cancelled", mutant.Note);
    }

    [Fact]
    public void A_change_cancelled_before_any_miss_is_stopped_and_a_miss_from_runs_cut_short_does_not_count()
    {
        var mutant = Judge(Skipped(), (label, _) =>
        {
            if (label == "m1-full-without")
            {
                // The run without the change "completed", but only after Ctrl+C reached its test host.
                _cancel.Cancel();
                return Ok();
            }

            return Hung();
        });

        Assert.Equal(AuditOutcome.Stopped, mutant.Outcome);
        Assert.Empty(mutant.Broken);
    }

    [Fact]
    public void A_user_filter_is_joined_with_the_excluding_filter_and_one_that_cannot_be_is_refused()
    {
        Assert.Equal(["--filter", "(Category!=Slow)&(FullyQualifiedName!~T.A.)", "-c", "Release"],
            AuditTrial.ExcludingWithin(["T.A"], TestFilterSyntax.VSTest, ["--filter", "Category!=Slow", "-c", "Release"]));
        Assert.Equal(["--filter-not-class", "T.A", "--filter-trait", "Fast=true"],
            AuditTrial.ExcludingWithin(["T.A"], TestFilterSyntax.XunitV3, ["--filter-trait", "Fast=true"]));
        Assert.Null(AuditTrial.Unjoinable(TestFilterSyntax.VSTest, ["--filter", "Category!=Slow"]));
        Assert.Contains("--treenode-filter cannot be joined", AuditTrial.Unjoinable(TestFilterSyntax.TUnit, ["--treenode-filter", "/*/*/*/*"]), StringComparison.Ordinal);
        Assert.Contains("--filter-class cannot be joined", AuditTrial.Unjoinable(TestFilterSyntax.XunitV3, ["--filter-class=T.A"]), StringComparison.Ordinal);
        Assert.Null(AuditTrial.ExcludingWithin(["T.A"], TestFilterSyntax.XunitV3, ["--filter-query", "/*/*/*/*"]));
    }

    private AuditMutant Judge(Func<AuditProject, TestProjectSelection?> decision, Func<string, bool, AuditRunOutcome> runs, int baselineClasses = 0)
    {
        var project = Project("T.Tests") with { BaselineClasses = baselineClasses };
        var trial = Trial([project], (request, with) => runs(request.Label, with));
        return trial.Judge(Worker(), Mutant(), NoMutation(), [project], decision(project) is { } d ? [d] : []);
    }

    private AuditTrial Trial(IReadOnlyList<AuditProject> projects, Func<AuditRunRequest, bool, AuditRunOutcome> runs)
    {
        var context = new AuditContext(null!, projects, [], [], null!, new Dictionary<string, CoverageMap>());
        return new AuditTrial(context, new AuditHooks
        {
            Runs = (request, with) =>
            {
                _calls.Add((request.Label, with, request.Arguments));
                return runs(request, with);
            },
        }, _cancel.Token);
    }

    private static Func<AuditProject, TestProjectSelection?> Skipped() => static p => Decision(p, TestRunMode.Skipped);

    private static Func<AuditProject, TestProjectSelection?> All() => static p => Decision(p, TestRunMode.All);

    private static Func<AuditProject, TestProjectSelection?> Filtered(params string[] classes) => p => Decision(p, TestRunMode.Filtered, classes);

    private static TestProjectSelection Decision(AuditProject project, TestRunMode mode, params string[] classes) =>
        new(project.Project, mode, classes, $"{mode} for the test");

    private static AuditProject Project(string name) =>
        new(new ProjectNode($"/repo/tests/{name}/{name}.csproj", $"/repo/tests/{name}", true, []), $"tests/{name}/{name}.csproj", DotnetTestMode.VSTest, TestFilterSyntax.VSTest)
        {
            Outputs = [$"tests/{name}/bin"],
            BaselineSeconds = 1,
        };

    private static AuditWorker Worker() => new("/copy", "/backups", new Dictionary<string, string>());

    private static AuditMutant Mutant() =>
        new(1, new SyntheticTarget(new CoveredMethod("Lib.Code", "M"), [new SourceEdit("/repo/src/Lib/Code.cs", 0, ";")], Covered: true), AuditOutcome.NotReached);

    /// <summary>A mutant with nothing to lift: the runs are made up.</summary>
    private static Mutation NoMutation() => new(new Dictionary<string, string>(), 1);

    /// <summary>A complete run in which every class ran and <paramref name="failed"/> failed.</summary>
    private static AuditRunOutcome Ok(params string[] failed) => new(Set(failed), true, null) { Classes = AllClasses.Length, Executed = Set(AllClasses) };

    private static AuditRunOutcome Hung() => new(Set(), false, "timed out after 2 min");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
