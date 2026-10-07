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
    public void The_budget_starts_no_change_once_it_is_spent_and_the_change_running_then_finishes_and_counts()
    {
        var clock = TimeSpan.Zero;
        var budget = new ShadowBudget(TimeSpan.FromMinutes(10), () => clock);

        // The third change starts at 8 minutes and ends at 12, past the budget: it is judged, and no fourth starts.
        var (mutants, stop, _) = AuditLoop.Run(Sample(20), 1, new AuditStopRules(100, null, budget), (_, index, target) =>
        {
            clock += TimeSpan.FromMinutes(4);
            return Mutant(index, target, index == 3 ? AuditOutcome.Missed : AuditOutcome.Caught);
        }, static _ => { }, CancellationToken.None);

        Assert.Equal("budget", stop);
        Assert.Equal([1, 2, 3], mutants.Select(static m => m.Index));
        Assert.Equal(3, mutants.Count(static m => m.Broke));
        Assert.Equal(AuditOutcome.Missed, mutants[2].Outcome);
    }

    [Fact]
    public void With_parallel_workers_the_budget_lets_every_running_change_finish_so_the_judged_changes_are_a_prefix()
    {
        var budget = new ShadowBudget(TimeSpan.FromMilliseconds(1500));
        var (mutants, stop, _) = AuditLoop.Run(Sample(40), 3, new AuditStopRules(100, null, budget), (_, index, target) =>
        {
            // Misses take longer (two more runs): they must not be the ones dropped at the end.
            Thread.Sleep(index % 3 == 0 ? 600 : 200);
            return Mutant(index, target, index % 3 == 0 ? AuditOutcome.Missed : AuditOutcome.Caught);
        }, static _ => { }, CancellationToken.None);

        Assert.Equal("budget", stop);
        Assert.Equal(Enumerable.Range(1, mutants.Count), mutants.Select(static m => m.Index));
        Assert.DoesNotContain(mutants, static m => m.Outcome == AuditOutcome.Stopped);
    }

    [Fact]
    public void A_result_that_comes_in_after_a_cancellation_is_stopped_unless_it_is_a_miss_confirmed_before()
    {
        using var cancel = new CancellationTokenSource();
        var (mutants, stop, _) = AuditLoop.Run(Sample(5), 1, new AuditStopRules(100, null, new ShadowBudget(null)), (_, index, target) =>
        {
            cancel.Cancel();
            return Mutant(index, target, AuditOutcome.Caught);
        }, static _ => { }, cancel.Token);

        Assert.Equal("cancelled", stop);
        Assert.Equal(AuditOutcome.Stopped, Assert.Single(mutants).Outcome);

        using var again = new CancellationTokenSource();
        (mutants, _, _) = AuditLoop.Run(Sample(5), 1, new AuditStopRules(100, null, new ShadowBudget(null)), (_, index, target) =>
        {
            again.Cancel();
            return Mutant(index, target, AuditOutcome.Missed);
        }, static _ => { }, again.Token);
        Assert.Equal(AuditOutcome.Missed, Assert.Single(mutants).Outcome);
    }

    [Fact]
    public void A_restore_that_fails_after_a_confirmed_miss_keeps_the_miss_and_stops_the_audit()
    {
        var (mutants, stop, failure) = AuditLoop.Run(Sample(10), 1, new AuditStopRules(100, null, new ShadowBudget(null)), static (_, index, target) =>
            index == 2
                ? throw new AuditChangeFailedException(Mutant(index, target, AuditOutcome.Missed), new IOException("the test host still holds Lib.dll"))
                : Mutant(index, target, AuditOutcome.Caught), static _ => { }, CancellationToken.None);

        Assert.Equal("error", stop);
        Assert.Equal("the test host still holds Lib.dll", failure?.Message);
        Assert.Equal([AuditOutcome.Caught, AuditOutcome.Missed], mutants.Select(static m => m.Outcome));
    }

    [Fact]
    public void Too_little_disk_space_stops_the_audit_with_an_error_before_the_next_change()
    {
        var calls = 0;
        var rules = new AuditStopRules(100, null, new ShadowBudget(null)) { Healthy = () => ++calls > 2 ? "the disk of the work folder has 512 MB free" : null };

        var (mutants, stop, failure) = AuditLoop.Run(Sample(10), 1, rules, static (_, index, target) => Mutant(index, target, AuditOutcome.Caught), static _ => { }, CancellationToken.None);

        Assert.Equal("error", stop);
        Assert.Contains("512 MB free", failure?.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2], mutants.Select(static m => m.Index));
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

        // The change that failed is part of the order taken: kept as stopped, which the worst-case bound counts.
        Assert.Equal(3, mutants.Count);
        Assert.Equal(AuditOutcome.Stopped, mutants[2].Outcome);
        Assert.Equal("stopped by an error: restore failed", mutants[2].Note);
    }

    [Fact]
    public void Only_work_folders_of_processes_of_this_machine_that_ended_and_hold_no_lock_are_swept()
    {
        var parent = Path.Combine(Path.GetTempPath(), "td-audit-sweep-" + Guid.NewGuid().ToString("N"));
        var host = System.Text.Json.JsonSerializer.Serialize(Environment.MachineName);
        try
        {
            var dead = Folder(parent, "td-audit-1-dead", $$"""{ "pid": 2147483646, "host": {{host}} }""");
            var mine = Folder(parent, "td-audit-2-mine", $$"""{ "pid": {{Environment.ProcessId}}, "host": {{host}} }""");
            var foreign = Folder(parent, "td-audit-3-foreign", null);

            // A container sharing /tmp: its process ids mean nothing here.
            var container = Folder(parent, "td-audit-4-container", """{ "pid": 2147483646, "host": "a1b2c3d4e5f6" }""");
            var older = Folder(parent, "td-audit-5-older", """{ "pid": 2147483646 }""");

            // Another PID namespace on this machine: the pid looks dead, but the audit holds its owner file locked.
            var locked = Folder(parent, "td-audit-6-locked", $$"""{ "pid": 2147483646, "host": {{host}} }""");
            File.WriteAllText(Path.Combine(dead, "Lib.dll"), "a mutated assembly");
            File.SetAttributes(Path.Combine(dead, "Lib.dll"), FileAttributes.ReadOnly);

            List<string> removed;
            using (new FileStream(Path.Combine(locked, AuditWorkspace.OwnerFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                removed = AuditWorkspace.RemoveStale(parent);
            }

            Assert.Equal([dead], removed);
            Assert.False(Directory.Exists(dead));
            Assert.All((string[])[mine, foreign, container, older, locked], static f => Assert.True(Directory.Exists(f), f));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void The_work_folder_is_readable_by_its_owner_only_and_its_owner_file_stays_locked_while_it_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), "td-audit-mode-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(Path.GetTempPath(), "td-audit-modes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "secret.env"), "TOKEN=1");
        try
        {
            using var workspace = AuditWorkspace.Create(AuditWorkspace.RealPath(root), parent);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(workspace.Folder));
            }

            Assert.Throws<IOException>(() => File.ReadAllBytes(Path.Combine(workspace.Folder, AuditWorkspace.OwnerFile)));
            Assert.Empty(AuditWorkspace.RemoveStale(AuditWorkspace.RealPath(parent)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void A_linked_assembly_is_replaced_by_a_file_of_its_own_so_a_mutant_never_reaches_the_link_target()
    {
        var folder = Path.Combine(Path.GetTempPath(), "td-audit-links-" + Guid.NewGuid().ToString("N"));
        var team = Path.Combine(folder, "team", "bin");
        var copy = Path.Combine(folder, "copy", "bin");
        Directory.CreateDirectory(team);
        Directory.CreateDirectory(copy);
        try
        {
            var original = Path.Combine(team, "Lib.dll");
            File.WriteAllText(original, "the team's build");
            var linked = Path.Combine(copy, "Lib.dll");
            File.CreateSymbolicLink(linked, original);
            var nested = Path.Combine(copy, "sub");
            Directory.CreateDirectory(nested);
            File.CreateSymbolicLink(Path.Combine(nested, "Lib.pdb"), original);

            AuditWorkspace.MaterializeLinks(copy, AuditWorkspace.RealPath(Path.Combine(folder, "copy")));
            File.WriteAllText(linked, "a mutant");
            File.WriteAllText(Path.Combine(nested, "Lib.pdb"), "a mutant");

            Assert.Null(new FileInfo(linked).LinkTarget);
            Assert.Equal("the team's build", File.ReadAllText(original));

            // A folder link out of the copy cannot be made safe: the audit stops.
            Directory.CreateSymbolicLink(Path.Combine(copy, "runtimes"), team);
            var error = Assert.Throws<IOException>(() => AuditWorkspace.MaterializeLinks(copy, AuditWorkspace.RealPath(Path.Combine(folder, "copy"))));
            Assert.Contains("outside the working copy", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_hard_linked_assembly_is_detached_before_it_is_rewritten()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "td-audit-hard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var original = Path.Combine(folder, "team.dll");
            var linked = Path.Combine(folder, "copy.dll");
            File.WriteAllText(original, "the team's build");
            using (var ln = System.Diagnostics.Process.Start("ln", [original, linked]))
            {
                ln.WaitForExit();
            }

            AuditWorkspace.Detach(linked);
            File.WriteAllText(linked, "a mutant");

            Assert.Equal("the team's build", File.ReadAllText(original));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_copy_needs_room_for_the_tree_or_on_a_clone_for_its_build_output_and_1_GB_to_spare()
    {
        Assert.Null(AuditWorkspace.SpaceProblem(free: 3L << 30, needed: 1L << 30, cloned: false));
        Assert.Contains("needs about 1024 MB and 1 GB to spare", AuditWorkspace.SpaceProblem(free: 1L << 30, needed: 1L << 30, cloned: false), StringComparison.Ordinal);
        Assert.Contains("the build rewrites its bin and obj folders", AuditWorkspace.SpaceProblem(free: 1L << 29, needed: 1L << 28, cloned: true), StringComparison.Ordinal);
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

        Assert.Equal(
            "On your code: 112 changes, 103 broke tests, 0 missed (95% upper bound 2.9%, n = 103); 0 inconclusive, 0 stopped " +
            "(worst case, counting them as misses: 95% upper bound 2.9%, n = 103); target of 100 reached.",
            AuditReport.Verdict(result));

        mutants[0] = mutants[0] with { Outcome = AuditOutcome.Missed };
        mutants[60] = mutants[60] with { Outcome = AuditOutcome.Inconclusive };
        mutants[61] = mutants[61] with { Outcome = AuditOutcome.Stopped };
        var stopped = result with { StopReason = "budget", Mutants = [.. mutants.Take(62)] };
        Assert.Equal(
            "On your code: 61 changes, 60 broke tests, 1 MISSED (95% upper bound 7.7%, n = 60); 1 inconclusive, 1 stopped " +
            "(worst case, counting them as misses: 95% upper bound 12.1%, n = 62); stopped by the budget (4 h) before the target of 100.",
            AuditReport.Verdict(stopped));
    }

    [Fact]
    public void The_bound_is_shown_rounded_up_so_100_changes_and_no_miss_read_as_at_most_3_0_percent()
    {
        Assert.Equal("3.0%", AuditReport.Percent(TestDetta.Mutants.MissRate.UpperBound(0, 100)));   // 2.951%
        Assert.Equal("3.0%", AuditReport.Percent(TestDetta.Mutants.MissRate.UpperBound(0, 101)));   // 2.922%: never shown as 2.9%
        Assert.Equal("2.9%", AuditReport.Percent(TestDetta.Mutants.MissRate.UpperBound(0, 102)));   // 2.894%
        Assert.Equal("2.9%", AuditReport.Percent(0.029));
        Assert.Equal("0.0%", AuditReport.Percent(0));
        Assert.Equal("100.0%", AuditReport.Percent(1));
    }

    [Fact]
    public void A_run_is_complete_only_when_its_report_says_so_whatever_language_the_runner_speaks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "td-audit-trx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // xUnit writes every failed test as a run message of outcome Error: no sign of a crash.
            File.WriteAllText(Path.Combine(folder, "a.trx"), Trx("[xUnit.net 00:00:00.07]     Crash.Tests.BTests.Fails [FAIL]"));
            Assert.Null(AuditTestRun.ReportProblem(folder));

            // What VSTest writes when the test host dies; the runs are made in English.
            File.WriteAllText(Path.Combine(folder, "a.trx"), Trx("The active test run was aborted. Reason: Test host process crashed : Process terminated.\nboom"));
            Assert.Equal("the test run was aborted: The active test run was aborted. Reason: Test host process crashed : Process terminated.", AuditTestRun.ReportProblem(folder));

            // The same under a German runner is not recognized here; the exit code without a failed test, or the classes
            // missing from a whole project's run, still tell (below, and AuditJudgingTests).
            File.WriteAllText(Path.Combine(folder, "a.trx"), Trx("Der aktive Testlauf wurde abgebrochen. Grund: Der Testhostprozess ist abgestürzt."));
            Assert.Null(AuditTestRun.ReportProblem(folder));

            File.WriteAllText(Path.Combine(folder, "a.trx"), """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Aborted" /></TestRun>""");
            Assert.Equal("the test run ended as Aborted", AuditTestRun.ReportProblem(folder));

            File.WriteAllText(Path.Combine(folder, "a.trx"), """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Failed" /></TestRun>""");
            Assert.Null(AuditTestRun.ReportProblem(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.Equal("the test host crashed", AuditTestRun.Problem(1, crashLine: true, reportProblem: null, failedClasses: 3));
        Assert.Equal("it failed (exit code 1) without a failed test", AuditTestRun.Problem(1, crashLine: false, reportProblem: null, failedClasses: 0));
        Assert.Equal("it failed (exit code 8) without a failed test", AuditTestRun.Problem(8, crashLine: false, reportProblem: null, failedClasses: 0));
        Assert.Null(AuditTestRun.Problem(1, crashLine: false, reportProblem: null, failedClasses: 2));
        Assert.Null(AuditTestRun.Problem(0, crashLine: false, reportProblem: null, failedClasses: 0));
        Assert.Equal("en", AuditProcess.English["DOTNET_CLI_UI_LANGUAGE"]);
        Assert.Equal("1033", AuditProcess.English["VSLANG"]);
    }

    [Fact]
    public void A_project_that_writes_outside_the_working_copy_is_refused_with_the_property_that_says_so()
    {
        var folder = AuditWorkspace.RealPath(Path.GetTempPath()) + "/td-audit-paths-" + Guid.NewGuid().ToString("N");
        var copy = Path.Combine(folder, "copy");
        Directory.CreateDirectory(Path.Combine(copy, "src", "Lib"));
        Directory.CreateDirectory(Path.Combine(folder, "team-bin"));
        try
        {
            var project = Path.Combine(copy, "src", "Lib", "Lib.csproj");
            string Relative(string p) => Path.GetRelativePath(copy, p);
            List<AuditOutputPath> inside = [new(project, "net10.0", "OutputPath", Path.Combine(copy, "src", "Lib", "bin", "Debug", "net10.0"))];
            Assert.Null(AuditOutputPaths.Outside(copy, [project], inside, Relative));

            List<AuditOutputPath> absolute = [.. inside, new(project, "net10.0", "BaseOutputPath", "/build/out")];
            Assert.Contains("src/Lib/Lib.csproj (net10.0) writes outside the working copy: its BaseOutputPath is /build/out", AuditOutputPaths.Outside(copy, [project], absolute, Relative), StringComparison.Ordinal);

            // A bin folder in the copy that is a link to the team's own.
            Directory.CreateSymbolicLink(Path.Combine(copy, "src", "Lib", "obj"), Path.Combine(folder, "team-bin"));
            List<AuditOutputPath> linked = [new(project, "net10.0", "IntermediateOutputPath", Path.Combine(copy, "src", "Lib", "obj", "Debug"))];
            Assert.Contains("(through a link:", AuditOutputPaths.Outside(copy, [project], linked, Relative), StringComparison.Ordinal);

            Assert.Contains("MSBuild did not say where", AuditOutputPaths.Outside(copy, [project, Path.Combine(copy, "B.csproj")], inside, Relative), StringComparison.Ordinal);
            Assert.Contains("--results-directory /tmp/results lies outside the working copy", AuditOutputPaths.OutsideOption(copy, copy, ["--results-directory", "/tmp/results"]), StringComparison.Ordinal);
            Assert.Null(AuditOutputPaths.OutsideOption(copy, copy, ["-c", "Release", "--results-directory", "results", "-o", "out"]));
            Assert.Equal([new AuditOutputPath(Paths.Normalize(project), "", "BaseOutputPath", Paths.Normalize(Path.Combine(copy, "bin")))],
                AuditOutputPaths.Parse([$"{project}\t\tBaseOutputPath\t{Path.Combine(copy, "bin")}/", "bad line"]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Trx(string message) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <ResultSummary outcome="Failed">
            <RunInfos><RunInfo computerName="ci" outcome="Error" timestamp="2026-10-07T05:24:20+03:00"><Text>{message}</Text></RunInfo></RunInfos>
          </ResultSummary>
        </TestRun>
        """;

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
