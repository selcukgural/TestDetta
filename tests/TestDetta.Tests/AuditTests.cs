using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// <c>td audit</c> (docs/design/audit.md) on a real repository: mutants in a working copy, the full runs, the selection
/// from the team's map, misses with their decision chain, and the team's tree byte-identical afterwards however the
/// audit ends. Slow: builds and runs the tests in a copy for every audit.
/// </summary>
[Trait("Category", "Slow")]
public sealed class AuditTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    /// <summary>Where the audits put their work folders, so a test can see that none is left.</summary>
    private readonly string _work = Path.Combine(Path.GetTempPath(), "td-audit-tests-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>Reports go outside the repository, so the whole tree can be compared before and after.</summary>
    private readonly string _report;

    public AuditTests()
    {
        _report = Path.Combine(_work, "report", "testdetta-audit.json");
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\nTestResults/\n");
        _repo.Write("src/Shop/Shop.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Shop/Calc.cs", """
            namespace Shop;
            public class Calc { public decimal Tax(decimal net) => net * 0.2m; }
            public class Discount { public decimal Apply(decimal price) => price - 1m; }
            public static class Unused { public static int Nothing(int x) => x + 1; }
            public static class Spin { public static bool Ready() { return true; } }
            """);
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/Tests.cs", """
            using Xunit;
            namespace Shop.Tests;
            public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2m, new Calc().Tax(10m)); }
            public class DiscountTests { [Fact] public void Discounts() => Assert.Equal(9m, new Discount().Apply(10m)); }
            public class SpinTests { [Fact] public void Waits() { while (!Spin.Ready()) { } } }
            """);
        _repo.Commit("base");

        // The map says nothing ran Discount.Apply, as a stale map might: the selection leaves DiscountTests out.
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.SpinTests"] = [2],
        });

        // The team has built: their build output must be the same bytes after every audit.
        Build("tests/Shop.Tests/Shop.Tests.csproj");
    }

    public void Dispose()
    {
        AuditTrial.MinimumTimeout = TimeSpan.FromMinutes(2);
        AuditTrial.TimeoutFactor = 5;
        _repo.Dispose();
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [Fact]
    public void A_break_the_selection_left_out_is_a_miss_with_its_decision_chain_and_the_team_tree_is_untouched()
    {
        var before = Snapshot();
        var summary = Path.Combine(_work, "summary.md");
        var stdout = new StringWriter();

        var exit = CliApp.Run(
            ["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Calc.Tax", "--member", "Shop.Discount.Apply", "--member", "Shop.Unused.Nothing",
             "--report", _report, "--work-dir", _work, "--summary-file", summary],
            stdout,
            new StringWriter());

        Assert.True(exit == 3, stdout.ToString());
        var report = Read();
        Assert.Equal(2, report.GetProperty("schema").GetInt32());
        Assert.StartsWith("On your code: 3 changes, 2 broke tests, 1 MISSED (95% upper bound", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Equal("sample", report.GetProperty("stopReason").GetString());
        Assert.Contains("No license is needed", report.GetProperty("license").GetString(), StringComparison.Ordinal);
        var totals = report.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("caught").GetInt32());
        Assert.Equal(1, totals.GetProperty("missed").GetInt32());
        Assert.Equal(1, totals.GetProperty("didNotBreak").GetInt32());

        Assert.Equal(0, totals.GetProperty("unjudged").GetInt32());
        Assert.Equal(totals.GetProperty("upperBound95").GetDouble(), totals.GetProperty("worstCaseUpperBound95").GetDouble());
        Assert.Contains("0 inconclusive, 0 stopped (worst case, counting them as misses", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);

        var mutants = report.GetProperty("mutants").EnumerateArray().ToDictionary(static m => m.GetProperty("member").GetString()!);
        Assert.Equal("caught", mutants["Shop.Calc.Tax"].GetProperty("outcome").GetString());

        // A catch is confirmed like a miss: alone with the change it fails, alone without it it passes.
        Assert.Equal("fails alone with the change and passes alone without it", mutants["Shop.Calc.Tax"].GetProperty("broken").EnumerateArray().Single().GetProperty("note").GetString());
        Assert.Equal("did-not-break", mutants["Shop.Unused.Nothing"].GetProperty("outcome").GetString());
        var miss = mutants["Shop.Discount.Apply"];
        Assert.Equal("missed", miss.GetProperty("outcome").GetString());
        Assert.Equal("return-default", miss.GetProperty("kind").GetString());
        Assert.Equal("src/Shop/Calc.cs", miss.GetProperty("file").GetString());
        Assert.Contains("Shop.Discount.Apply(decimal) modified", miss.GetProperty("seen").EnumerateArray().Select(static s => s.GetString()));
        var broken = miss.GetProperty("broken").EnumerateArray().Single();
        Assert.Equal("Shop.Tests.DiscountTests", broken.GetProperty("class").GetString());
        Assert.False(broken.GetProperty("selected").GetBoolean());
        Assert.Equal("fails alone with the change and passes alone without it", broken.GetProperty("note").GetString());
        var chain = broken.GetProperty("chain");
        Assert.Equal("Skipped", chain.GetProperty("decision").GetString());
        Assert.Equal(new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root), chain.GetProperty("mapCommit").GetString());
        Assert.False(string.IsNullOrEmpty(chain.GetProperty("projectReason").GetString()));
        Assert.Equal("td audit --member Shop.Discount.Apply --kinds return-default --max-map-age 50 --drift-fallback all", miss.GetProperty("reproduce").GetString());
        // The expression body in parentheses: the edit to make by hand to see the same decision.
        var edits = miss.GetProperty("edits").EnumerateArray().ToList();
        Assert.Equal(["(", ")"], edits.Select(static e => e.GetProperty("insert").GetString()));
        Assert.All(edits, static e => Assert.Equal(3, e.GetProperty("line").GetInt32()));
        Assert.Equal("src/Shop/Calc.cs", edits[0].GetProperty("file").GetString());

        var project = report.GetProperty("projects").EnumerateArray().Single();
        Assert.True(project.GetProperty("audited").GetBoolean());
        Assert.Equal(3, project.GetProperty("classes").GetInt32());
        Assert.True(report.GetProperty("run").GetProperty("workFolderRemoved").GetBoolean());
        Assert.StartsWith("td audit --seed 1 --kinds return-default --member Shop.Calc.Tax", report.GetProperty("reproduction").GetProperty("command").GetString(), StringComparison.Ordinal);
        Assert.Equal(64, report.GetProperty("reproduction").GetProperty("testDettaSha256").GetString()!.Length);

        var markdown = File.ReadAllText(summary);
        Assert.Contains("### TestDetta audit", markdown, StringComparison.Ordinal);
        Assert.Contains("**Misses** (1)", markdown, StringComparison.Ordinal);
        Assert.Contains("`Shop.Tests.DiscountTests`", markdown, StringComparison.Ordinal);
        Assert.Contains("MISS #", stdout.ToString(), StringComparison.Ordinal);

        AssertUntouched(before);
    }

    [Fact]
    public void A_dirty_working_tree_is_refused_before_anything_is_copied()
    {
        _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.3m");
        var stdout = new StringWriter();

        var exit = CliApp.Run(["audit", "--repo", _repo.Root, "--report", _report, "--work-dir", _work], stdout, new StringWriter());

        Assert.Equal(1, exit);
        var report = Read();
        Assert.Equal("error", report.GetProperty("stopReason").GetString());
        Assert.Contains("src/Shop/Calc.cs", report.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("No audit result: the working tree has changes", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(_work) ? Directory.GetDirectories(_work, AuditWorkspace.Prefix + "*") : []);
    }

    [Fact]
    public void A_change_that_hangs_is_killed_at_its_limit_and_caught_when_the_selected_part_hangs_again_and_passes_without_it()
    {
        var before = Snapshot();
        AuditTrial.MinimumTimeout = TimeSpan.FromSeconds(8);
        AuditTrial.TimeoutFactor = 0;
        var stdout = new StringWriter();

        // Spin.Ready returning false makes SpinTests loop for ever; the map has SpinTests run Spin.Ready.
        var exit = CliApp.Run(["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Spin.Ready", "--report", _report, "--work-dir", _work], stdout, new StringWriter());

        Assert.True(exit == 0, stdout.ToString());
        var mutant = Read().GetProperty("mutants").EnumerateArray().Single();
        Assert.Equal("caught", mutant.GetProperty("outcome").GetString());
        Assert.True(mutant.GetProperty("caughtByCrash").GetBoolean());
        var part = mutant.GetProperty("broken").EnumerateArray().Single();
        Assert.Equal("selected", part.GetProperty("part").GetString());
        Assert.Equal(JsonValueKind.Null, part.GetProperty("class").ValueKind);
        AssertUntouched(before);
    }

    [Fact]
    public void A_hang_in_a_project_the_selection_skipped_is_a_miss_once_the_same_run_completes_without_the_change()
    {
        AddWaitProject();
        var before = Snapshot();
        AuditTrial.MinimumTimeout = TimeSpan.FromSeconds(8);
        AuditTrial.TimeoutFactor = 0;
        var stdout = new StringWriter();

        // Turn.Done returning false makes WaitTests loop for ever; its map says WaitTests runs nothing of the change.
        var exit = CliApp.Run(["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Turn.Done", "--report", _report, "--work-dir", _work], stdout, new StringWriter());

        Assert.True(exit == 3, stdout.ToString());
        var mutant = Read().GetProperty("mutants").EnumerateArray().Single();
        Assert.Equal("missed", mutant.GetProperty("outcome").GetString());
        var miss = mutant.GetProperty("broken").EnumerateArray().Single();
        Assert.Equal("tests/Wait.Tests/Wait.Tests.csproj", miss.GetProperty("project").GetString());
        Assert.Equal("left-out", miss.GetProperty("part").GetString());
        Assert.True(miss.GetProperty("crash").GetBoolean());
        Assert.Contains("completes without it; the selection ran none of this project", miss.GetProperty("note").GetString(), StringComparison.Ordinal);
        AssertUntouched(before);
    }

    [Fact]
    public void A_test_host_crash_is_seen_when_the_runner_speaks_another_language_and_counts_only_when_it_comes_back()
    {
        AddWaitProject();
        var before = Snapshot();
        Assert.True(CliOptions.TryParse(
            ["audit", "--repo", _repo.Root, "--kinds", "throw", "--member", "Shop.Hook.Fire", "--report", _report, "--work-dir", _work],
            _repo.Root, out var options, out _));
        var stdout = new StringWriter();

        // The team's runner speaks German: the SDK's crash message would not be the English one.
        var hooks = new AuditHooks { Environment = new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "de", ["VSLANG"] = "1031" } };
        var exit = new AuditRun(NullLoggerFactory.Instance, stdout, hooks, CancellationToken.None).Run(options);

        Assert.True(exit == 0, stdout.ToString());
        var mutant = Read().GetProperty("mutants").EnumerateArray().Single();
        Assert.Equal("caught", mutant.GetProperty("outcome").GetString());
        Assert.True(mutant.GetProperty("caughtByCrash").GetBoolean());
        var part = mutant.GetProperty("broken").EnumerateArray().Single();
        Assert.Equal("tests/Wait.Tests/Wait.Tests.csproj", part.GetProperty("project").GetString());
        Assert.Contains("the test host crashed", part.GetProperty("note").GetString(), StringComparison.Ordinal);
        AssertUntouched(before);
    }

    [Fact]
    public void A_miss_confirmed_before_the_audit_is_cancelled_is_kept()
    {
        AddWaitProject();
        Assert.True(CliOptions.TryParse(
            ["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Discount.Apply", "--report", _report, "--work-dir", _work],
            _repo.Root, out var options, out _));
        using var cancel = new CancellationTokenSource();
        var fullRuns = 0;

        // Shop.Tests runs first and confirms the miss; the cancellation comes as Wait.Tests starts.
        var hooks = new AuditHooks
        {
            Running = name =>
            {
                if (name == "m1-full" && ++fullRuns == 2)
                {
                    cancel.Cancel();
                }
            },
        };
        var stdout = new StringWriter();
        var exit = new AuditRun(NullLoggerFactory.Instance, stdout, hooks, cancel.Token).Run(options);

        Assert.True(exit == 3, stdout.ToString());
        var report = Read();
        Assert.Equal("cancelled", report.GetProperty("stopReason").GetString());
        var mutant = report.GetProperty("mutants").EnumerateArray().Single();
        Assert.Equal("missed", mutant.GetProperty("outcome").GetString());
        Assert.Equal("the miss was confirmed before the change was cancelled", mutant.GetProperty("note").GetString());
        Assert.Equal("Shop.Tests.DiscountTests", mutant.GetProperty("broken").EnumerateArray().Single().GetProperty("class").GetString());
    }

    [Fact]
    public void An_assembly_the_team_built_as_a_link_into_its_own_output_is_never_written_through()
    {
        // The team builds with links instead of copies: the test project's Shop.dll points into src/Shop/bin.
        Directory.Delete(Path.Combine(_repo.Root, "tests", "Shop.Tests", "bin"), recursive: true);
        Build("tests/Shop.Tests/Shop.Tests.csproj", "-p:CreateSymbolicLinksForCopyLocalIfPossible=true");
        var linked = Path.Combine(_repo.Root, "tests", "Shop.Tests", "bin", "Debug", "net10.0", "Shop.dll");
        Assert.NotNull(new FileInfo(linked).LinkTarget);
        var team = Path.Combine(_repo.Root, "src", "Shop", "bin", "Debug", "net10.0", "Shop.dll");
        var bytes = File.ReadAllBytes(team);
        var before = Snapshot();
        Assert.True(CliOptions.TryParse(
            ["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Calc.Tax", "--report", _report, "--work-dir", _work, "--", "--no-build"],
            _repo.Root, out var options, out _));

        // While the mutant is in, the team's own Shop.dll must still be the team's.
        var during = Array.Empty<byte>();
        var hooks = new AuditHooks { Injected = _ => during = File.ReadAllBytes(team) };
        var stdout = new StringWriter();
        var exit = new AuditRun(NullLoggerFactory.Instance, stdout, hooks, CancellationToken.None).Run(options);

        Assert.True(exit == 0, stdout.ToString());
        Assert.Equal("caught", Read().GetProperty("mutants").EnumerateArray().Single().GetProperty("outcome").GetString());
        Assert.Equal(bytes, during);
        Assert.NotNull(new FileInfo(linked).LinkTarget);
        AssertUntouched(before);
    }

    [Fact]
    public void An_output_path_outside_the_repository_is_refused_before_anything_builds_in_the_copy()
    {
        var outside = Path.Combine(_work, "team-out");
        _repo.Write("Directory.Build.props", $"<Project><PropertyGroup><BaseOutputPath>{outside}/$(MSBuildProjectName)/</BaseOutputPath></PropertyGroup></Project>");
        _repo.Commit("an absolute output path");
        Build("tests/Shop.Tests/Shop.Tests.csproj");
        var files = Directory.GetFiles(outside, "*", SearchOption.AllDirectories).ToDictionary(static f => f, static f => File.GetLastWriteTimeUtc(f));
        var stdout = new StringWriter();

        var exit = CliApp.Run(["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Calc.Tax", "--report", _report, "--work-dir", _work], stdout, new StringWriter());

        Assert.Equal(1, exit);
        var report = Read();
        Assert.Equal("error", report.GetProperty("stopReason").GetString());
        Assert.Contains("writes outside the working copy: its BaseOutputPath is " + outside, report.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("without changes", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(files, Directory.GetFiles(outside, "*", SearchOption.AllDirectories).ToDictionary(static f => f, static f => File.GetLastWriteTimeUtc(f)));
        Assert.Empty(Directory.GetDirectories(_work, AuditWorkspace.Prefix + "*"));
    }

    [Fact]
    public void A_commit_made_while_the_audit_runs_stops_it_with_an_error()
    {
        Assert.True(CliOptions.TryParse(
            ["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Calc.Tax", "--report", _report, "--work-dir", _work],
            _repo.Root, out var options, out _));
        var hooks = new AuditHooks
        {
            Injected = _ =>
            {
                _repo.Write("NOTES.md", "a commit of the team's, made during the audit");
                _repo.Commit("meanwhile");
            },
        };
        var stdout = new StringWriter();

        var exit = new AuditRun(NullLoggerFactory.Instance, stdout, hooks, CancellationToken.None).Run(options);

        Assert.Equal(1, exit);
        var report = Read();
        Assert.Equal("error", report.GetProperty("stopReason").GetString());
        Assert.Contains("the repository moved from", report.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("stopped", report.GetProperty("mutants").EnumerateArray().Single().GetProperty("outcome").GetString());
        Assert.Contains("1 stopped (worst case, counting them as misses: 95% upper bound 100.0%, n = 1)", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(_work, AuditWorkspace.Prefix + "*"));
    }

    [Fact]
    public void Cancelling_while_a_change_runs_kills_its_tests_writes_the_report_and_deletes_the_copy()
    {
        var before = Snapshot();
        using var cancel = new CancellationTokenSource();
        Assert.True(CliOptions.TryParse(
            ["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Spin.Ready", "--report", _report, "--work-dir", _work],
            _repo.Root, out var options, out _));
        var stdout = new StringWriter();

        // The change makes SpinTests hang; the cancellation comes while its tests run, long before any limit.
        var hooks = new AuditHooks { Injected = _ => cancel.CancelAfter(TimeSpan.FromSeconds(4)) };
        var started = System.Diagnostics.Stopwatch.StartNew();
        var exit = new AuditRun(NullLoggerFactory.Instance, stdout, hooks, cancel.Token).Run(options);

        Assert.Equal(1, exit);
        Assert.InRange(started.Elapsed.TotalSeconds, 0, 100);
        var report = Read();
        Assert.Equal("cancelled", report.GetProperty("stopReason").GetString());
        Assert.Equal("stopped", report.GetProperty("mutants").EnumerateArray().Single().GetProperty("outcome").GetString());
        AssertUntouched(before);
    }

    [Fact]
    public void An_exception_in_the_middle_of_a_change_leaves_the_team_tree_untouched_and_deletes_the_copy()
    {
        var before = Snapshot();
        Assert.True(CliOptions.TryParse(
            ["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Calc.Tax", "--report", _report, "--work-dir", _work],
            _repo.Root, out var options, out _));
        var stdout = new StringWriter();
        var hooks = new AuditHooks { Injected = static _ => throw new IOException("the disk went away") };

        var exit = new AuditRun(NullLoggerFactory.Instance, stdout, hooks, CancellationToken.None).Run(options);

        Assert.Equal(1, exit);
        var report = Read();
        Assert.Equal("error", report.GetProperty("stopReason").GetString());
        Assert.Contains("the disk went away", report.GetProperty("error").GetString(), StringComparison.Ordinal);
        AssertUntouched(before);
    }

    [Fact]
    public void The_audit_stops_at_the_target_at_the_limit_and_when_the_budget_is_spent()
    {
        string[] both = ["audit", "--repo", _repo.Root, "--kinds", "return-default", "--member", "Shop.Calc.Tax", "--member", "Shop.Discount.Apply", "--report", _report, "--work-dir", _work];

        // Both break tests (one is caught, one missed); whichever comes first in the sample is the only one judged.
        CliApp.Run([.. both, "--target-broken", "1"], new StringWriter(), new StringWriter());
        var report = Read();
        Assert.Equal("target", report.GetProperty("stopReason").GetString());
        Assert.Equal(1, report.GetProperty("totals").GetProperty("broke").GetInt32());
        Assert.Single(report.GetProperty("mutants").EnumerateArray());
        Assert.EndsWith("target of 1 reached.", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);

        CliApp.Run([.. both, "--max-mutants", "1"], new StringWriter(), new StringWriter());
        report = Read();
        Assert.Equal("max-mutants", report.GetProperty("stopReason").GetString());
        Assert.Equal(1, report.GetProperty("totals").GetProperty("changes").GetInt32());

        // The copy and the baseline take longer than a second: no change starts.
        Assert.Equal(0, CliApp.Run([.. both, "--budget", "1s"], new StringWriter(), new StringWriter()));
        report = Read();
        Assert.Equal("budget", report.GetProperty("stopReason").GetString());
        Assert.Empty(report.GetProperty("mutants").EnumerateArray());
        Assert.Contains("stopped by the budget (1 s) before the target of 100", report.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(_work, AuditWorkspace.Prefix + "*"));
    }

    private JsonElement Read() => JsonDocument.Parse(File.ReadAllText(_report)).RootElement;

    /// <summary>
    /// A second test project, after Shop.Tests in path order, that the selection leaves out: WaitTests loops until
    /// Turn.Done returns true, HookTests runs Hook.Fire on a thread of its own (an exception there kills the test host).
    /// Its map says WaitTests runs nothing and HookTests runs Hook.Fire; both maps are made again at the new commit.
    /// </summary>
    private void AddWaitProject()
    {
        _repo.Write("src/Shop/More.cs", """
            namespace Shop;
            public static class Turn { public static bool Done() { return true; } }
            public static class Hook { public static void Fire() { } }
            """);
        _repo.Write("tests/Wait.Tests/Wait.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Wait.Tests/Tests.cs", """
            using Xunit;
            namespace Wait.Tests;
            public class WaitTests { [Fact] public void Waits() { while (!Shop.Turn.Done()) { } } }
            public class HookTests { [Fact] public void Fires() { var thread = new System.Threading.Thread(Shop.Hook.Fire); thread.Start(); thread.Join(); } }
            """);
        _repo.Commit("a second test project");
        WriteMap(new()
        {
            ["Shop.Tests.TaxTests"] = [0],
            ["Shop.Tests.DiscountTests"] = [],
            ["Shop.Tests.SpinTests"] = [2],
        });
        WriteMap(new() { ["Wait.Tests.WaitTests"] = [], ["Wait.Tests.HookTests"] = [4] }, "tests/Wait.Tests/Wait.Tests.csproj");
        Build("tests/Wait.Tests/Wait.Tests.csproj");
    }

    /// <summary>Builds a project of the team's tree, as the team would before an audit.</summary>
    /// <param name="project">Relative path.</param>
    /// <param name="extra">More arguments.</param>
    private void Build(string project, params string[] extra)
    {
        var build = new System.Diagnostics.ProcessStartInfo("dotnet", ["build", project, "-v", "q", .. extra]) { WorkingDirectory = _repo.Root, RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(build)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }

    /// <summary>
    /// Every file of the team's tree with its hash, but git's own and the project-graph cache every td command keeps in
    /// <c>.testdetta/cache</c>; the coverage map counts.
    /// </summary>
    private Dictionary<string, string> Snapshot()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_repo.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_repo.Root, file).Replace('\\', '/');
            if (relative.StartsWith(".git/", StringComparison.Ordinal) || relative.StartsWith(".testdetta/cache/", StringComparison.Ordinal))
            {
                continue;
            }

            files[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return files;
    }

    /// <summary>The team's tree is byte for byte as before, git sees no change, and no work folder is left.</summary>
    /// <param name="before">The tree before the audit.</param>
    private void AssertUntouched(Dictionary<string, string> before)
    {
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, file => Assert.True(after[file.Key] == file.Value, $"{file.Key} changed"));
        Assert.DoesNotContain(new GitClient(NullLogger<GitClient>.Instance).ChangedFiles(_repo.Root, "HEAD"), static f => !f.StartsWith(".testdetta/", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(_work, AuditWorkspace.Prefix + "*"));
    }

    private void WriteMap(Dictionary<string, IReadOnlyList<int>> classes, string project = "tests/Shop.Tests/Shop.Tests.csproj")
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader(project, head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Shop.Calc", "Tax"), new CoveredMethod("Shop.Discount", "Apply"), new CoveredMethod("Shop.Spin", "Ready"), new CoveredMethod("Shop.Turn", "Done"), new CoveredMethod("Shop.Hook", "Fire")],
            new CoverageHits(classes, new Dictionary<string, IReadOnlyList<int>>(), []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            FileReads = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
            OutputUses = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, project)), map);
    }
}
