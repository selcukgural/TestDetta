using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>The parts of <c>td audit</c> that need no test run: options, the stop conditions, stale work folders, the verdict.</summary>
public sealed class AuditUnitTests
{
    [Fact]
    public void Audit_options_have_the_documented_defaults()
    {
        Assert.True(CliOptions.TryParse(["audit"], "/repo", out var options, out var error), error);

        Assert.Equal(CliCommand.Audit, options.Command);
        var audit = options.Audit;
        Assert.Equal(100, audit.TargetBroken);
        Assert.Null(audit.MaxMutants);
        Assert.Equal(TimeSpan.FromHours(4), audit.Budget);
        Assert.Equal(1, audit.Parallelism);
        Assert.Equal(1, audit.Seed);
        Assert.Equal(4, audit.Kinds.Count);
        Assert.Empty(audit.Members);
        Assert.Null(audit.Report);
        Assert.Null(audit.WorkDirectory);
    }

    [Fact]
    public void Audit_options_are_parsed_with_the_dotnet_test_arguments_after_the_separator()
    {
        Assert.True(CliOptions.TryParse(
            ["audit", "--target-broken", "120", "--max-mutants", "400", "--budget", "1h30m", "--parallel", "2", "--seed", "-7", "--kinds", "throw,flip-branch",
             "--member", "Shop.Calc.Tax", "--member", "Shop.Calc..ctor", "--report", "out.json", "--summary-file", "summary.md", "--work-dir", "/scratch",
             "--max-map-age", "10", "--drift-fallback", "name", "--", "-c", "Release", "--filter", "Category!=Slow"],
            "/repo", out var options, out var error), error);

        var audit = options.Audit;
        Assert.Equal(120, audit.TargetBroken);
        Assert.Equal(400, audit.MaxMutants);
        Assert.Equal(TimeSpan.FromMinutes(90), audit.Budget);
        Assert.Equal(2, audit.Parallelism);
        Assert.Equal(-7, audit.Seed);
        Assert.Equal(new HashSet<MutantKind> { MutantKind.Throw, MutantKind.FlipBranch }, audit.Kinds);
        Assert.Equal("throw,flip-branch", audit.KindsText);
        Assert.Equal(["Shop.Calc.Tax", "Shop.Calc..ctor"], audit.Members);
        Assert.Equal("out.json", audit.Report);
        Assert.Equal("/scratch", audit.WorkDirectory);
        Assert.Equal("summary.md", options.Test.SummaryFile);
        Assert.Equal(10, options.Analysis.MaxMapAge);
        Assert.Equal(DriftFallback.ByName, options.Analysis.DriftFallback);
        Assert.Equal(["-c", "Release", "--filter", "Category!=Slow"], options.Test.DotnetTestArguments);

        Assert.True(CliOptions.TryParse(["audit", "--budget", "none"], "/repo", out options, out _));
        Assert.Null(options.Audit.Budget);
    }

    [Theory]
    [InlineData("--base", "origin/main", "compares every change with HEAD")]
    [InlineData("--target-broken", "0", "--target-broken needs a number from 1")]
    [InlineData("--max-mutants", "many", "--max-mutants needs a number")]
    [InlineData("--parallel", "65", "--parallel needs a number from 1 to 64")]
    [InlineData("--budget", "200h", "--budget needs a time")]
    [InlineData("--kinds", "explode", "Unknown mutant kind 'explode'")]
    [InlineData("--member", "Tax", "--member needs a type and a member")]
    [InlineData("--seed", "1.5", "--seed needs a whole number")]
    public void Invalid_audit_options_are_refused_with_the_reason(string option, string value, string expected)
    {
        Assert.False(CliOptions.TryParse(["audit", option, value], "/repo", out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_options_belong_to_td_audit_only()
    {
        Assert.False(CliOptions.TryParse(["test", "--target-broken", "5"], "/repo", out _, out var error));
        Assert.Contains("Unknown option for td test", error, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["audit", "--shadow"], "/repo", out _, out error));
        Assert.Contains("Unknown option for td audit", error, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["affected", "--", "-c", "Release"], "/repo", out _, out error));
        Assert.Contains("td test and td audit", error, StringComparison.Ordinal);

        // td record keeps its own --parallel.
        Assert.True(CliOptions.TryParse(["record", "--parallel", "3"], "/repo", out var options, out _));
        Assert.Equal(3, options.Record.Parallelism);
        Assert.Equal(1, options.Audit.Parallelism);
    }

    [Fact]
    public void The_audit_stops_at_the_target_after_a_prefix_of_the_sample()
    {
        // Every second change breaks tests: the third one that does is the fifth change.
        var (mutants, stop, failure) = AuditLoop.Run(Sample(20), 1, new AuditStopRules(3, null, new ShadowBudget(null)),
            static (_, index, target) => Mutant(index, target, index % 2 == 1 ? AuditOutcome.Caught : AuditOutcome.DidNotBreak), static _ => { }, CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal("target", stop);
        Assert.Equal([1, 2, 3, 4, 5], mutants.Select(static m => m.Index));
    }

    [Fact]
    public void The_limit_counts_changes_that_ran_tests_and_not_those_no_test_can_see()
    {
        var (mutants, stop, _) = AuditLoop.Run(Sample(20), 1, new AuditStopRules(100, 4, new ShadowBudget(null)),
            static (_, index, target) => Mutant(index, target, index <= 3 ? AuditOutcome.NotReached : AuditOutcome.DidNotBreak), static _ => { }, CancellationToken.None);

        Assert.Equal("max-mutants", stop);
        Assert.Equal(7, mutants.Count);
        Assert.Equal(4, mutants.Count(static m => m.Ran));
    }

    [Fact]
    public void The_budget_starts_no_change_once_it_is_spent_and_a_change_it_cut_is_not_counted()
    {
        var clock = TimeSpan.Zero;
        var budget = new ShadowBudget(TimeSpan.FromMinutes(10), () => clock);

        var (mutants, stop, _) = AuditLoop.Run(Sample(20), 1, new AuditStopRules(100, null, budget), (_, index, target) =>
        {
            clock += TimeSpan.FromMinutes(4);
            return Mutant(index, target, index == 3 ? AuditOutcome.Stopped : AuditOutcome.Caught);
        }, static _ => { }, CancellationToken.None);

        Assert.Equal("budget", stop);
        Assert.Equal(3, mutants.Count);
        Assert.Equal(2, mutants.Count(static m => m.Broke));
    }

    [Fact]
    public void A_cancellation_or_the_end_of_the_sample_stops_the_loop()
    {
        using var cancel = new CancellationTokenSource();
        var (mutants, stop, _) = AuditLoop.Run(Sample(20), 1, new AuditStopRules(100, null, new ShadowBudget(null)), (_, index, target) =>
        {
            if (index == 2)
            {
                cancel.Cancel();
            }

            return Mutant(index, target, AuditOutcome.Caught);
        }, static _ => { }, cancel.Token);

        Assert.Equal("cancelled", stop);
        Assert.Equal(2, mutants.Count);

        (mutants, stop, _) = AuditLoop.Run(Sample(3), 1, new AuditStopRules(100, null, new ShadowBudget(null)),
            static (_, index, target) => Mutant(index, target, AuditOutcome.Caught), static _ => { }, CancellationToken.None);
        Assert.Equal("sample", stop);
        Assert.Equal(3, mutants.Count);
    }

    [Fact]
    public void Parallel_workers_finish_what_they_started_so_the_judged_changes_stay_a_prefix()
    {
        var workers = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var (mutants, stop, _) = AuditLoop.Run(Sample(40), 3, new AuditStopRules(5, null, new ShadowBudget(null)), (worker, index, target) =>
        {
            workers.AddOrUpdate(worker, 1, static (_, n) => n + 1);
            Thread.Sleep(10);
            return Mutant(index, target, AuditOutcome.Caught);
        }, static _ => { }, CancellationToken.None);

        Assert.Equal("target", stop);
        Assert.InRange(mutants.Count(static m => m.Broke), 5, 7);
        Assert.Equal(Enumerable.Range(1, mutants.Count), mutants.Select(static m => m.Index));
        Assert.All(workers.Keys, static w => Assert.InRange(w, 0, 2));
    }

    [Fact]
    public void A_failing_change_stops_the_audit_and_keeps_what_was_judged()
    {
        var (mutants, stop, failure) = AuditLoop.Run(Sample(10), 1, new AuditStopRules(100, null, new ShadowBudget(null)),
            static (_, index, target) => index == 3 ? throw new IOException("restore failed") : Mutant(index, target, AuditOutcome.Caught), static _ => { }, CancellationToken.None);

        Assert.Equal("error", stop);
        Assert.Equal("restore failed", failure?.Message);
        Assert.Equal(2, mutants.Count);
    }

    [Fact]
    public void Only_work_folders_of_processes_that_ended_are_swept()
    {
        var parent = Path.Combine(Path.GetTempPath(), "td-audit-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dead = Folder(parent, "td-audit-1-dead", """{ "pid": 2147483646 }""");
            var mine = Folder(parent, "td-audit-2-mine", $$"""{ "pid": {{Environment.ProcessId}} }""");
            var foreign = Folder(parent, "td-audit-3-foreign", null);
            File.WriteAllText(Path.Combine(dead, "Lib.dll"), "a mutated assembly");
            File.SetAttributes(Path.Combine(dead, "Lib.dll"), FileAttributes.ReadOnly);

            var removed = AuditWorkspace.RemoveStale(parent);

            Assert.Equal([dead], removed);
            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(mine));
            Assert.True(Directory.Exists(foreign));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void A_work_folder_inside_the_repository_is_refused_before_anything_is_written()
    {
        var root = Path.Combine(Path.GetTempPath(), "td-audit-inside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = Assert.Throws<IOException>(() => AuditWorkspace.Create(AuditWorkspace.RealPath(root), Path.Combine(root, "work")));

            Assert.Contains("inside the repository", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_verdict_states_changes_broken_misses_and_the_bound_with_n()
    {
        Assert.True(CliOptions.TryParse(["audit"], "/repo", out var options, out _));
        var mutants = Sample(112).Select((t, i) => Mutant(i + 1, t, i < 103 ? AuditOutcome.Caught : AuditOutcome.DidNotBreak)).ToList();
        var result = new AuditResult("/repo", "target", mutants, [], options.Audit, new AuditReproduction("1.0", null, null, [], [], "td audit"));

        Assert.Equal("On your code: 112 changes, 103 broke tests, 0 missed (95% upper bound 2.9%, n = 103); target of 100 reached.", AuditReport.Verdict(result));

        mutants[0] = mutants[0] with { Outcome = AuditOutcome.Missed };
        var stopped = result with { StopReason = "budget", Mutants = [.. mutants.Take(50)] };
        Assert.Equal("On your code: 50 changes, 50 broke tests, 1 MISSED (95% upper bound 9.1%, n = 50); stopped by the budget (4 h) before the target of 100.", AuditReport.Verdict(stopped));
    }

    private static List<SyntheticTarget> Sample(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => new SyntheticTarget(new CoveredMethod("Lib.Code", $"M{i}"), [new SourceEdit("/repo/src/Lib/Code.cs", i, ";")], Covered: i % 2 == 0))];

    private static AuditMutant Mutant(int index, SyntheticTarget target, AuditOutcome outcome) => new(index, target, outcome);

    private static string Folder(string parent, string name, string? owner)
    {
        var folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        if (owner is not null)
        {
            File.WriteAllText(Path.Combine(folder, AuditWorkspace.OwnerFile), owner);
        }

        return folder;
    }
}
