using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// <c>td test --shadow</c> against <see cref="FakeDotnet"/> instead of real test runs, so the paths that do not depend on
/// the SDK run in a second: where reports are read, what reruns are given, which projects run, how failures are told
/// apart, and that nothing TestDetta does can fail the job or leave anything behind. Real runs: <see cref="ShadowModeTests"/>.
/// </summary>
public sealed class ShadowRunTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly FakeDotnet _dotnet;
    private readonly string _root;

    public ShadowRunTests()
    {
        _repo.Write(".gitignore", "TestResults/\n.testdetta/\nres/\nout/\n");
        _repo.Write("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        foreach (var project in (string[])["Shop.Tests", "Other.Tests"])
        {
            _repo.Write($"tests/{project}/{project}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
                </Project>
                """);
        }

        _repo.Commit("base");
        _root = new GitClient(NullLogger<GitClient>.Instance).TopLevel(_repo.Root);
        _dotnet = new FakeDotnet(_root);
        _dotnet.Classes["Shop.Tests.csproj"] = new()
        {
            ["Shop.Tests.TaxTests"] = ["Taxes"],
            ["Shop.Tests.DiscountTests"] = ["Discounts"],
        };
        _dotnet.Classes["Other.Tests.csproj"] = new() { ["Other.Tests.OtherTests"] = ["Holds"] };
    }

    public void Dispose()
    {
        var locked = Path.Combine(_repo.Root, "res", "locked");
        if (!OperatingSystem.IsWindows() && Directory.Exists(locked))
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _repo.Dispose();
    }

    [Fact]
    public void Reports_are_read_where_the_teams_run_wrote_them_and_only_the_report_TestDetta_asked_for_is_deleted()
    {
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.DiscountTests";
        _dotnet.After = static (_, results) =>
        {
            // What a coverage collector leaves beside the report.
            Directory.CreateDirectory(Path.Combine(results, "attachments"));
            File.WriteAllText(Path.Combine(results, "attachments", "coverage.cobertura.xml"), "<coverage />");
        };

        var (exit, report, _) = Run(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"), "--shadow-no-classify");

        Assert.Equal(3, exit);
        var run = report.GetProperty("run");
        Assert.Equal("verbatim", run.GetProperty("kind").GetString());
        Assert.Equal("dotnet test tests/Shop.Tests/Shop.Tests.csproj --logger trx", run.GetProperty("command").GetString());
        Assert.DoesNotContain("--results-directory", string.Join(' ', _dotnet.Calls.Single().Arguments), StringComparison.Ordinal);

        // VSTest's own results folder keeps everything else the run wrote; the report TestDetta's logger made is read and deleted.
        var results = Path.Combine(_repo.Root, "tests", "Shop.Tests", "TestResults");
        Assert.Empty(Directory.GetFiles(results, "*.trx"));
        Assert.True(File.Exists(Path.Combine(results, "attachments", "coverage.cobertura.xml")));
        Assert.True(Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("failed").GetBoolean());
        Assert.Equal("unclassified", Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("verdict").GetString());
    }

    [Fact]
    public void Only_new_reports_of_TestDettas_logger_are_deleted_and_older_reports_or_reports_a_test_writes_elsewhere_stay()
    {
        // Each project runs on its own into the team's results directory, which already holds a report of an earlier run.
        var res = Path.Combine(_repo.Root, "res");
        Directory.CreateDirectory(res);
        FakeDotnet.WriteTrx(Path.Combine(res, "earlier.trx"), Path.Combine(_repo.Root, "tests", "Shop.Tests", "bin", "Debug", "net10.0", "Shop.Tests.dll"), [("Shop.Tests.TaxTests", "Taxes", false)]);
        File.WriteAllText(Path.Combine(res, "results.junit.xml"), "<testsuites />");
        _dotnet.After = (call, _) =>
            // A test of the team's that writes a TRX file of its own somewhere else in the tree.
            FakeDotnet.WriteTrx(Path.Combine(_repo.Root, "out", $"fixture-{call.Index}.trx"), Path.Combine(_repo.Root, "x.dll"), [("X.Tests", "T", false)]);
        Directory.CreateDirectory(Path.Combine(_repo.Root, "out"));
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.DiscountTests";

        var (exit, report, stdout) = Run(["--results-directory", "res"], LeftOut("Shop.Tests.DiscountTests"), "--shadow-no-classify");

        Assert.True(exit == 3, stdout);
        Assert.Equal("per-project", report.GetProperty("run").GetProperty("kind").GetString());
        Assert.True(Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("failed").GetBoolean());
        Assert.Equal(["earlier.trx", "results.junit.xml"], Directory.GetFiles(res).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_repo.Root, "out"), "*.trx").Length);
    }

    [Fact]
    public void A_report_is_TestDettas_own_only_when_it_is_new_and_lies_where_its_report_option_puts_one()
    {
        var before = new Dictionary<string, DateTime>(StringComparer.Ordinal) { ["/r/t/TestResults/old.trx"] = DateTime.UnixEpoch };

        Assert.True(ShadowRun.IsOwnReport("/r/t/TestResults/new.trx", before, null));
        Assert.True(ShadowRun.IsOwnReport("/r/t/bin/Debug/net10.0/TestResults/new.trx", before, null));
        Assert.True(ShadowRun.IsOwnReport("/r/res/new.trx", before, "/r/res"));
        Assert.False(ShadowRun.IsOwnReport("/r/t/TestResults/old.trx", before, null));
        Assert.False(ShadowRun.IsOwnReport("/r/res/sub/new.trx", before, "/r/res"));
        Assert.False(ShadowRun.IsOwnReport("/r/t/fixtures/new.trx", before, null));
    }

    [Fact]
    public void The_teams_own_logger_and_results_directory_are_kept_and_read_there()
    {
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.DiscountTests";

        var (exit, report, _) = Run(["tests/Shop.Tests/Shop.Tests.csproj", "-l", "trx;LogFileName=team.trx", "--results-directory", "res"], LeftOut("Shop.Tests.DiscountTests"), "--shadow-no-classify");

        Assert.Equal(3, exit);
        Assert.Equal("dotnet test tests/Shop.Tests/Shop.Tests.csproj -l \"trx;LogFileName=team.trx\" --results-directory res", report.GetProperty("run").GetProperty("command").GetString());
        Assert.Equal(["team.trx"], Directory.GetFiles(Path.Combine(_repo.Root, "res")).Select(Path.GetFileName));
        Assert.True(Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("failed").GetBoolean());
    }

    [Fact]
    public void A_report_that_cannot_be_parsed_and_a_folder_that_cannot_be_read_cost_nothing_but_that_report()
    {
        // No solution in the root: each project runs on its own, its reports found in the team's results directory.
        var res = Path.Combine(_repo.Root, "res");
        Directory.CreateDirectory(Path.Combine(res, "locked"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(res, "locked"), UnixFileMode.None);
        }

        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.DiscountTests";
        _dotnet.After = static (call, results) =>
        {
            File.WriteAllText(Path.Combine(results, $"dtd-{call.Index}.trx"), """<?xml version="1.0"?><!DOCTYPE TestRun [<!ENTITY x "y">]><TestRun>&x;</TestRun>""");
            File.WriteAllText(Path.Combine(results, $"broken-{call.Index}.trx"), "<TestRun><Results>");
        };

        var (exit, report, stdout) = Run(["--results-directory", "res"], LeftOut("Shop.Tests.DiscountTests"), "--shadow-no-classify");

        Assert.True(exit == 3, stdout);
        Assert.True(report.GetProperty("compared").GetBoolean());
        Assert.Equal("per-project", report.GetProperty("run").GetProperty("kind").GetString());
        Assert.True(Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("failed").GetBoolean());
        Assert.Equal(2, _dotnet.Calls.Count);
    }

    [Fact]
    public void A_solution_filter_of_an_unexpected_shape_leaves_the_scope_unknown_and_the_command_runs_as_given()
    {
        _repo.Write("Odd.slnf", """{ "solution": { "projects": "not a list" } }""");
        _dotnet.Solutions["Odd.slnf"] = ["Shop.Tests.csproj"];
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.TaxTests";

        var (exit, report, stdout) = Run(["Odd.slnf"], LeftOut("Shop.Tests.DiscountTests"), "--shadow-no-classify");

        Assert.True(exit == 3, stdout);
        var run = report.GetProperty("run");
        Assert.Equal("verbatim", run.GetProperty("kind").GetString());
        Assert.False(run.GetProperty("scopeKnown").GetBoolean());
        Assert.Equal("caught", Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.TaxTests").GetProperty("verdict").GetString());
    }

    [Fact]
    public void A_broken_global_json_beside_a_test_project_does_not_keep_any_test_from_running()
    {
        _repo.Write("tests/Shop.Tests/global.json", "{ not json");
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.DiscountTests";

        var (exit, report, stdout) = Run([], LeftOut("Shop.Tests.DiscountTests"), "--shadow-no-classify");

        Assert.True(exit == 3, stdout);
        Assert.True(report.GetProperty("compared").GetBoolean());
        Assert.Equal(2, _dotnet.Calls.Count);
        Assert.True(Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("failed").GetBoolean());
    }

    [Fact]
    public void A_broken_global_json_where_the_command_runs_leaves_the_teams_plain_command_to_run()
    {
        _repo.Write("global.json", "{ not json");

        var (exit, report, _) = Run(["-c", "Release"], LeftOut("Shop.Tests.DiscountTests"));

        Assert.Equal(0, exit);
        Assert.Equal(["test", "-c", "Release"], _dotnet.Calls.Single().Arguments);
        Assert.Equal("plain", report.GetProperty("run").GetProperty("kind").GetString());
        Assert.False(report.GetProperty("compared").GetBoolean());
        Assert.StartsWith("shadow mode could not plan the run (InvalidDataException", report.GetProperty("noResultReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_before_any_test_ran_leaves_the_teams_plain_command_to_run()
    {
        var first = true;
        _dotnet.Before = _ =>
        {
            if (first)
            {
                first = false;
                throw new IOException("injected");
            }
        };
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.TaxTests";

        var (exit, report, _) = Run(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"));

        Assert.Equal(3, exit);
        Assert.Equal(2, _dotnet.Calls.Count);
        Assert.Equal(["test", "tests/Shop.Tests/Shop.Tests.csproj"], _dotnet.Calls[1].Arguments);
        Assert.Equal("plain", report.GetProperty("run").GetProperty("kind").GetString());
        Assert.StartsWith("shadow mode failed before the tests ran (IOException: injected)", report.GetProperty("noResultReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_after_the_full_run_keeps_its_exit_code_and_still_writes_a_report()
    {
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.TaxTests";

        // Writing the held log after the full run throws.
        var (exit, report, _) = RunWith(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"), stderr: new ThrowingWriter(), log: "held log\n");

        Assert.Equal(3, exit);
        Assert.Single(_dotnet.Calls);
        Assert.False(report.GetProperty("compared").GetBoolean());
        Assert.StartsWith("shadow mode failed after the tests ran (IOException", report.GetProperty("noResultReason").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, report.GetProperty("run").GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void The_per_project_run_runs_only_the_projects_of_the_teams_command()
    {
        // Two projects of the solution build the same assembly, so it runs per project; Other.Tests is not in it and fails.
        _repo.Write("tests/Dup.Tests/Dup.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Shop.Tests</AssemblyName></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /></ItemGroup>
            </Project>
            """);
        _repo.Write("Shop.slnx", """<Solution><Project Path="tests/Shop.Tests/Shop.Tests.csproj" /><Project Path="tests/Dup.Tests/Dup.Tests.csproj" /></Solution>""");
        _repo.Commit("solution");
        _dotnet.Classes["Dup.Tests.csproj"] = new() { ["Dup.Tests.DupTests"] = ["Duplicates"] };
        _dotnet.AssemblyNames["Dup.Tests.csproj"] = "Shop.Tests";
        _dotnet.Fails = static (_, c, _) => c == "Other.Tests.OtherTests";

        var (exit, report, stdout) = Run(["Shop.slnx"], LeftOut("Shop.Tests.DiscountTests"));

        Assert.True(exit == 0, stdout);
        var run = report.GetProperty("run");
        Assert.Equal("per-project", run.GetProperty("kind").GetString());
        Assert.Contains("both build Shop.Tests", run.GetProperty("fallbackReason").GetString(), StringComparison.Ordinal);
        Assert.True(run.GetProperty("scopeKnown").GetBoolean());
        Assert.Equal(["Dup.Tests.csproj", "Shop.Tests.csproj"], _dotnet.Calls.Select(static c => Path.GetFileName(c.Project)).Order());
        Assert.Equal(["tests/Dup.Tests/Dup.Tests.csproj", "tests/Shop.Tests/Shop.Tests.csproj"], report.GetProperty("projects").EnumerateArray().Select(static p => p.GetProperty("path").GetString()));
    }

    [Fact]
    public void When_the_commands_projects_cannot_be_told_every_project_under_its_directory_runs_and_the_report_says_so()
    {
        var summary = Path.Combine(_repo.Root, ".testdetta", "summary.md");

        var (exit, report, _) = Run([], LeftOut("Shop.Tests.DiscountTests"), "--summary-file", summary);

        Assert.Equal(0, exit);
        var run = report.GetProperty("run");
        Assert.False(run.GetProperty("scopeKnown").GetBoolean());
        Assert.Equal(Path.GetFullPath(_repo.Root), run.GetProperty("scope").GetString());
        Assert.Equal(2, _dotnet.Calls.Count);
        Assert.Contains("Which test projects your command runs could not be told, so every test project under", File.ReadAllText(summary), StringComparison.Ordinal);
    }

    [Fact]
    public void A_class_with_one_test_broken_before_the_change_and_another_broken_by_it_is_a_miss()
    {
        _dotnet.Classes["Shop.Tests.csproj"]["Shop.Tests.DiscountTests"] = ["Old", "New"];

        // Old fails on the base commit too; New only with the change.
        _dotnet.Fails = static (call, c, t) => c == "Shop.Tests.DiscountTests" && (t == "Old" || !call.OnBase);

        var (exit, report, stdout) = Run(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"));

        Assert.True(exit == 3, stdout);
        var discount = Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests");
        Assert.Equal("miss", discount.GetProperty("verdict").GetString());
        Assert.Contains("Shop.Tests.DiscountTests.New passes alone on the base commit", discount.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(["Shop.Tests.DiscountTests.New", "Shop.Tests.DiscountTests.Old"], discount.GetProperty("failedTests").EnumerateArray().Select(static t => t.GetString()));
        Assert.Single(_dotnet.OnBase);
        Assert.Equal(4, report.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void A_class_every_failing_test_of_which_fails_on_the_base_commit_too_is_pre_existing()
    {
        _dotnet.Classes["Shop.Tests.csproj"]["Shop.Tests.DiscountTests"] = ["Old", "Also"];
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.DiscountTests";

        var (_, report, _) = Run(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"));

        Assert.Equal("pre-existing", Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData(false, "miss", "order-dependent, caused by the change")]
    [InlineData(true, "pre-existing", "fails in that context on the base commit")]
    public void A_class_that_passes_alone_but_fails_next_to_the_others_again_is_order_dependent(bool failsOnBase, string verdict, string note)
    {
        // DiscountTests fails whenever TaxTests runs in the same process: it passes alone.
        _dotnet.Fails = (call, c, _) => c == "Shop.Tests.DiscountTests" && call.Classes.Contains("Shop.Tests.TaxTests") && (failsOnBase || !call.OnBase);

        var (_, report, _) = Run(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"));

        var discount = Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests");
        Assert.Equal(verdict, discount.GetProperty("verdict").GetString());
        Assert.Contains(note, discount.GetProperty("note").GetString(), StringComparison.Ordinal);

        // Full run, alone, in context with the change, in context on the base commit.
        Assert.Equal(4, _dotnet.Calls.Count);
        Assert.False(_dotnet.Calls[2].Filtered);
        Assert.True(_dotnet.Calls[3].OnBase);
    }

    [Fact]
    public void A_class_that_passes_alone_and_next_to_the_others_is_flaky()
    {
        _dotnet.Fails = static (call, c, _) => c == "Shop.Tests.DiscountTests" && call.Index == 0;

        var (_, report, _) = Run(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"));

        var discount = Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests");
        Assert.Equal("flaky", discount.GetProperty("verdict").GetString());
        Assert.Empty(_dotnet.OnBase);
    }

    [Fact]
    public void A_test_project_of_a_failed_command_that_wrote_no_report_is_unexplained()
    {
        _repo.Write("Shop.slnx", """<Solution><Project Path="tests/Shop.Tests/Shop.Tests.csproj" /><Project Path="tests/Other.Tests/Other.Tests.csproj" /></Solution>""");
        _repo.Commit("solution");
        _dotnet.Solutions["Shop.slnx"] = ["Shop.Tests.csproj", "Other.Tests.csproj"];
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.TaxTests";
        _dotnet.WritesReport = static call => !call.Project.EndsWith("Other.Tests.csproj", StringComparison.Ordinal);

        var (exit, report, _) = Run(["Shop.slnx"], LeftOut("Shop.Tests.DiscountTests"));

        Assert.Equal(3, exit);
        var other = report.GetProperty("projects").EnumerateArray().Single(static p => p.GetProperty("path").GetString() == "tests/Other.Tests/Other.Tests.csproj");
        Assert.True(other.GetProperty("missingReport").GetBoolean());
        Assert.Equal(1, report.GetProperty("totals").GetProperty("unexplainedProjects").GetInt32());
        Assert.Contains("1 test project(s) failed without a report", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reruns_write_their_reports_only_into_TestDettas_folder_and_the_base_rerun_builds_on_its_own()
    {
        _dotnet.Fails = static (call, c, _) => c == "Shop.Tests.DiscountTests" && !call.OnBase;

        var (_, report, _) = Run(
            ["tests/Shop.Tests/Shop.Tests.csproj", "--no-restore", "-o", "out", "--artifacts-path=art", "--results-directory", "res", "--logger:trx;LogFileName=team.trx"],
            LeftOut("Shop.Tests.DiscountTests"));

        Assert.Equal("miss", Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("verdict").GetString());
        Assert.Equal(["team.trx"], Directory.GetFiles(Path.Combine(_repo.Root, "res")).Select(Path.GetFileName));
        foreach (var rerun in _dotnet.Calls.Skip(1))
        {
            var arguments = string.Join(' ', rerun.Arguments);
            Assert.DoesNotContain("team.trx", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("--results-directory res", arguments, StringComparison.Ordinal);
            Assert.Contains("--results-directory " + Path.GetTempPath().TrimEnd('/'), arguments, StringComparison.Ordinal);
        }

        var onBase = string.Join(' ', _dotnet.OnBase.Single().Arguments);
        Assert.DoesNotContain("--no-restore", onBase, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-build", onBase, StringComparison.Ordinal);
        Assert.DoesNotContain("-o out", onBase, StringComparison.Ordinal);
        Assert.DoesNotContain("--artifacts-path", onBase, StringComparison.Ordinal);
        Assert.Contains("--no-build", string.Join(' ', _dotnet.Calls[1].Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_some_of_whose_classes_did_not_run_shows_neither_its_mode_nor_its_reason()
    {
        _dotnet.Classes["Shop.Tests.csproj"]["Shop.Tests.HiddenTests"] = ["Hidden"];
        var analysis = Analysis(new Selection("Shop.Tests", TestRunMode.Filtered, "1 of 3 test class(es) run or reference the changed code",
            [("Shop.Tests.TaxTests", true), ("Shop.Tests.DiscountTests", false), ("Shop.Tests.HiddenTests", false)]));
        var summary = Path.Combine(_repo.Root, ".testdetta", "summary.md");

        // The user's filter keeps HiddenTests from running.
        var (_, report, stdout) = Run(["tests/Shop.Tests/Shop.Tests.csproj", "--filter", "FullyQualifiedName~Shop.Tests.TaxTests.|FullyQualifiedName~Shop.Tests.DiscountTests."], analysis, "--summary-file", summary);

        var project = report.GetProperty("projects").EnumerateArray().Single(static p => p.GetProperty("path").GetString() == "tests/Shop.Tests/Shop.Tests.csproj");
        Assert.True(project.GetProperty("partial").GetBoolean());
        Assert.Equal("filtered", project.GetProperty("mode").GetString());
        Assert.Equal(["Shop.Tests.HiddenTests"], project.GetProperty("notRun").EnumerateArray().Select(static c => c.GetString()));
        var everything = File.ReadAllText(Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json")) + stdout + File.ReadAllText(summary);
        Assert.DoesNotContain("1 of 3 test class(es)", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_run_starts_in_the_teams_working_directory()
    {
        var team = Path.Combine(_repo.Root, "build");
        Directory.CreateDirectory(team);
        File.WriteAllText(Path.Combine(team, "ci.runsettings"), "<RunSettings />");
        _repo.Write("build/.keep", string.Empty);
        _repo.Commit("build folder");
        _dotnet.Fails = static (call, c, _) => c == "Shop.Tests.DiscountTests" && !call.OnBase;

        var (_, report, _) = RunWith(["../tests/Shop.Tests/Shop.Tests.csproj", "--settings", "ci.runsettings"], LeftOut("Shop.Tests.DiscountTests"), repo: team);

        Assert.Equal("miss", Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests").GetProperty("verdict").GetString());
        Assert.All(_dotnet.Calls, static c => Assert.Equal("build", Path.GetFileName(c.Directory)));
        Assert.Contains(_dotnet.Calls, static c => c.OnBase);
    }

    [Fact]
    public void Cancelling_during_the_reruns_removes_the_base_worktree_and_the_temp_folder()
    {
        using var cancel = new CancellationTokenSource();
        _dotnet.Fails = static (_, c, _) => c == "Shop.Tests.DiscountTests";
        _dotnet.Before = call =>
        {
            if (call.OnBase)
            {
                cancel.Cancel();
            }
        };

        var (exit, report, _) = RunWith(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"), cancel: cancel.Token);

        Assert.Equal(3, exit);
        var discount = Class(report, "tests/Shop.Tests/Shop.Tests.csproj", "Shop.Tests.DiscountTests");
        Assert.Equal("unclassified", discount.GetProperty("verdict").GetString());
        Assert.Contains("cancelled", discount.GetProperty("note").GetString(), StringComparison.Ordinal);
        var work = Path.GetDirectoryName(_dotnet.OnBase.Single().Directory.TrimEnd('/'))!;
        Assert.StartsWith("testdetta-shadow-", Path.GetFileName(work), StringComparison.Ordinal);
        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(Path.Combine(_repo.Root, ".git", "worktrees")) && Directory.EnumerateFileSystemEntries(Path.Combine(_repo.Root, ".git", "worktrees")).Any());
    }

    [Fact]
    public void Cancelling_before_the_tests_runs_nothing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var stdout = new StringWriter();
        var exit = Shadow(["tests/Shop.Tests/Shop.Tests.csproj"], LeftOut("Shop.Tests.DiscountTests"), [], stdout, new StringWriter(), _repo.Root, string.Empty, cancel.Token);

        Assert.Equal(1, exit);
        Assert.Empty(_dotnet.Calls);
        Assert.Contains("cancelled before the tests ran", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_checkout_of_the_base_commit_that_runs_out_of_time_leaves_no_worktree_registered()
    {
        var git = new GitClient(NullLogger<GitClient>.Instance);
        var path = Path.Combine(Path.GetTempPath(), "testdetta-worktree-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<TimeoutException>(() => git.AddWorktree(_root, path, git.Head(_root), TimeSpan.Zero, CancellationToken.None));

        Assert.True(git.DiscardWorktree(_root, path));
        Assert.False(Directory.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(_repo.Root, ".git", "worktrees")) && Directory.EnumerateFileSystemEntries(Path.Combine(_repo.Root, ".git", "worktrees")).Any());

        // Stopped before git wrote where the worktree lives: locked as "initializing", unknown to remove and prune.
        var registration = Path.Combine(_repo.Root, ".git", "worktrees", Path.GetFileName(path));
        Directory.CreateDirectory(registration);
        File.WriteAllText(Path.Combine(registration, "locked"), "initializing");
        var other = Path.Combine(_repo.Root, ".git", "worktrees", "someone-elses");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "gitdir"), "/elsewhere/.git");
        File.WriteAllText(Path.Combine(other, "locked"), "on a removable disk");

        Assert.True(git.DiscardWorktree(_root, path));
        Assert.False(Directory.Exists(registration));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void An_analysis_that_runs_past_its_time_limit_is_stopped_and_every_test_still_runs()
    {
        Assert.True(CliOptions.TryParse(["test", "--shadow", "--shadow-analysis-timeout", "1s", "--repo", _repo.Root], _repo.Root, out var options, out var error), error);
        var stdout = new StringWriter();
        var stopped = false;

        var analysis = CliApp.ShadowAnalyze(options, stdout, CancellationToken.None, (factory, _, stop) =>
        {
            factory.CreateLogger("TestSelector").Log(LogLevel.Warning, default, "a decision that must not be shown", null, static (state, _) => state);
            stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            stopped = stop.IsCancellationRequested;
            stop.ThrowIfCancellationRequested();
            throw new InvalidOperationException("not stopped");
        });

        Assert.Null(analysis.Result);
        Assert.Equal("the analysis did not finish within 1 s (--shadow-analysis-timeout)", analysis.Error);
        Assert.Empty(analysis.Log);
        Assert.True(stopped);
        Assert.Contains("every test still runs", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_reruns_arguments_drop_what_would_write_into_the_teams_folders_or_read_the_working_trees_build()
    {
        List<string> user = ["-c", "Release", "--results-directory=res", "--logger", "trx;LogFileName=/abs/team.trx", "-l:console", "--no-restore", "--output:out", "-o", "o2", "--artifacts-path", "art", "--no-build", "--", "RunConfiguration.X=1"];

        Assert.Equal(["-c", "Release", "-l:console", "--no-restore", "--output:out", "-o", "o2", "--artifacts-path", "art", "--no-build", "--", "RunConfiguration.X=1"], ShadowRun.RerunArguments(user, DotnetTestMode.VSTest, onBase: false));
        Assert.Equal(["-c", "Release", "-l:console", "--", "RunConfiguration.X=1"], ShadowRun.RerunArguments(user, DotnetTestMode.VSTest, onBase: true));
        Assert.Equal(
            ["--filter-class", "A", "--no-build"],
            ShadowRun.RerunArguments(["--report-trx", "--report-trx-filename", "mine.trx", "--filter-class", "A", "--report-xunit-trx-filename=x.trx", "--report-xunit-trx", "--no-build"], DotnetTestMode.TestingPlatform, onBase: false));
    }

    /// <summary>A selection for one project: its mode, reason and per-class decisions.</summary>
    private sealed record Selection(string Project, TestRunMode Mode, string Reason, (string Class, bool Selected)[] Decisions);

    /// <summary>The selection that leaves <paramref name="leftOut"/> out of Shop.Tests and selects its other classes; Other.Tests is not affected.</summary>
    private AnalysisResult LeftOut(string leftOut) => Analysis(new Selection("Shop.Tests", TestRunMode.Filtered, "the selection's reason",
        [.. _dotnet.Classes["Shop.Tests.csproj"].Keys.Select(c => (c, c != leftOut))]));

    private AnalysisResult Analysis(params Selection[] selections)
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_root);
        var tests = selections.Select(s =>
        {
            var node = graph.Projects.Single(p => Path.GetFileNameWithoutExtension(p.Path) == s.Project);
            return new TestProjectSelection(node, s.Mode, [.. s.Decisions.Where(static d => d.Selected).Select(static d => d.Class)], s.Reason)
            {
                Decisions = [.. s.Decisions.Select(static d => new ClassDecision(d.Class, d.Selected, d.Selected ? ClassDecisionKind.Coverage : ClassDecisionKind.Skipped, d.Selected ? "ran the change" : "ran none of it"))],
            };
        }).ToList();
        return new AnalysisResult(_root, [], graph, new ImpactResult([], [], [], []), new TestPlan(tests, null, 0));
    }

    private (int Exit, JsonElement Report, string Stdout) Run(
        string[] userArguments,
        AnalysisResult analysis,
        params string[] shadowOptions) => RunWith(userArguments, analysis, shadowOptions);

    private (int Exit, JsonElement Report, string Stdout) RunWith(string[] userArguments, AnalysisResult analysis, string[]? shadowOptions = null, TextWriter? stderr = null, string? log = null, string? repo = null, CancellationToken cancel = default)
    {
        var stdout = new StringWriter();
        var exit = Shadow(userArguments, analysis, shadowOptions ?? [], stdout, stderr ?? new StringWriter(), repo ?? _repo.Root, log ?? string.Empty, cancel);
        var path = Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json");
        Assert.True(File.Exists(path), stdout.ToString());
        return (exit, JsonDocument.Parse(File.ReadAllText(path)).RootElement, stdout.ToString());
    }

    private int Shadow(string[] userArguments, AnalysisResult analysis, string[] shadowOptions, TextWriter stdout, TextWriter stderr, string repo, string log, CancellationToken cancel)
    {
        Assert.True(CliOptions.TryParse(["test", "--shadow", "--repo", repo, "--base", "HEAD", "--shadow-report", Path.Combine(_repo.Root, ".testdetta", "testdetta-shadow.json"), .. shadowOptions, "--", .. userArguments], repo, out var options, out var error), error);
        return new ShadowRun(NullLoggerFactory.Instance, stdout, stderr, new ShadowHooks { Dotnet = _dotnet.Run }, cancel).Run(options, new ShadowAnalysis(analysis, null, 0.5, log));
    }

    private static JsonElement Class(JsonElement report, string project, string name) =>
        report.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("path").GetString() == project)
            .GetProperty("classes").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    /// <summary>A writer whose every write fails, as a closed pipe would.</summary>
    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("the pipe is closed");

        public override void Write(string? value) => throw new IOException("the pipe is closed");
    }
}
