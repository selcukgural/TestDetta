using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

public sealed class TestInvocationBuilderTests
{
    private static readonly ProjectNode Tests = new("/repo/tests/A.Tests/A.Tests.csproj", "/repo/tests/A.Tests", true, []);
    private static readonly ProjectNode Other = new("/repo/tests/B.Tests/B.Tests.csproj", "/repo/tests/B.Tests", true, []);
    private static readonly ProjectNode Skipped = new("/repo/tests/C.Tests/C.Tests.csproj", "/repo/tests/C.Tests", true, []);

    [Fact]
    public void Builds_filtered_and_full_runs_and_leaves_out_skipped_projects()
    {
        TestProjectSelection[] selections =
        [
            new(Tests, TestRunMode.Filtered, ["A.Tests.OrderTests"], "1 of 3"),
            new(Other, TestRunMode.All, [], "config changed"),
            new(Skipped, TestRunMode.Skipped, [], "nothing reaches it"),
        ];

        var invocations = TestInvocationBuilder.Build(selections, "/repo", ["-c", "Release"]);

        Assert.Equal(2, invocations.Count);
        Assert.Equal(["test", "tests/A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.Tests.OrderTests.", "-c", "Release"], invocations[0].Arguments);
        Assert.Equal(["test", "tests/B.Tests/B.Tests.csproj", "-c", "Release"], invocations[1].Arguments);
    }

    [Fact]
    public void Xunit_v3_on_the_testing_platform_gets_its_own_class_filters()
    {
        // It rejects the VSTest --filter with "Unknown option" (Scrutor, xunit.v3.mtp-v2).
        using var repo = new GitRepoFixture();
        repo.Write("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var xunit = Tests with { Path = Path.Combine(repo.Root, "tests/A.Tests/A.Tests.csproj"), PackageReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xunit.v3.mtp-v2" } };
        var mstest = Other with { Path = Path.Combine(repo.Root, "tests/B.Tests/B.Tests.csproj"), PackageReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MSTest" } };
        TestProjectSelection[] selections =
        [
            new(xunit, TestRunMode.Filtered, ["A.Tests.OrderTests", "A.Tests.Outer+Inner"], "2 of 3"),
            new(mstest, TestRunMode.Filtered, ["B.Tests.CartTests"], "1 of 3"),
        ];

        var invocations = TestInvocationBuilder.Build(selections, repo.Root, []);

        Assert.Equal(
            ["test", "--project", "tests/A.Tests/A.Tests.csproj", "--filter-class", "A.Tests.OrderTests", "--filter-class", "A.Tests.Outer+Inner"],
            invocations[0].Arguments);
        Assert.Equal(["test", "--project", "tests/B.Tests/B.Tests.csproj", "--filter", "FullyQualifiedName~B.Tests.CartTests."], invocations[1].Arguments);
    }

    [Fact]
    public void Command_line_quotes_only_arguments_the_shell_would_change()
    {
        var invocation = new TestInvocation(
            new TestProjectSelection(Tests, TestRunMode.Filtered, ["A.Outer+Inner"], ""),
            ["test", "tests/A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.X.|FullyQualifiedName~A.Y's."],
            new DotnetTestLocation("", DotnetTestMode.VSTest));

        Assert.Equal(
            @"dotnet test tests/A.Tests/A.Tests.csproj --filter 'FullyQualifiedName~A.X.|FullyQualifiedName~A.Y'\''s.'",
            ResultWriter.CommandLine(invocation));
    }

    [Fact]
    public void A_project_under_its_own_global_json_runs_there_with_the_runner_it_selects()
    {
        // Refit keeps the testing-platform opt-in in src/global.json; the root has none.
        using var repo = new GitRepoFixture();
        repo.Write("src/global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var inSrc = new ProjectNode(Path.Combine(repo.Root, "src/A.Tests/A.Tests.csproj"), Path.Combine(repo.Root, "src/A.Tests"), true, []);
        var atRoot = new ProjectNode(Path.Combine(repo.Root, "tests/B.Tests/B.Tests.csproj"), Path.Combine(repo.Root, "tests/B.Tests"), true, []);
        TestProjectSelection[] selections =
        [
            new(inSrc, TestRunMode.Filtered, ["A.Tests.OrderTests"], ""),
            new(atRoot, TestRunMode.All, [], ""),
        ];

        var invocations = TestInvocationBuilder.Build(selections, repo.Root, []);

        Assert.Equal(new DotnetTestLocation("src", DotnetTestMode.TestingPlatform), invocations[0].Location);
        Assert.Equal(["test", "--project", "A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.Tests.OrderTests."], invocations[0].Arguments);
        Assert.Equal("(cd src && dotnet test --project A.Tests/A.Tests.csproj --filter 'FullyQualifiedName~A.Tests.OrderTests.')", ResultWriter.CommandLine(invocations[0]));
        Assert.Equal(new DotnetTestLocation("", DotnetTestModeDetector.Detect(repo.Root)), invocations[1].Location);
        Assert.Equal("tests/B.Tests/B.Tests.csproj", invocations[1].Arguments[^1]);
    }

    [Fact]
    public void A_vstest_selection_runs_within_the_projects_own_test_case_filter()
    {
        // dotnet test --filter replaces VSTestTestCaseFilter: alone, the class filter would run the team's integration tests.
        var team = Tests with { TestConfiguration = new TestConfiguration("Category!=Integration", null, null) };
        TestProjectSelection[] selections = [new(team, TestRunMode.Filtered, ["A.Tests.OrderTests"], "1 of 3"), new(team, TestRunMode.All, [], "all")];

        var invocations = TestInvocationBuilder.Build(selections, "/repo", ["-c", "Release"]);

        Assert.Equal(["test", "tests/A.Tests/A.Tests.csproj", "--filter", "(Category!=Integration)&(FullyQualifiedName~A.Tests.OrderTests.)", "-c", "Release"], invocations[0].Arguments);
        // Without a filter of TestDetta's, dotnet test applies the project's own.
        Assert.Equal(["test", "tests/A.Tests/A.Tests.csproj", "-c", "Release"], invocations[1].Arguments);
    }

    [Fact]
    public void A_filter_on_the_command_line_stands_for_the_projects_as_in_the_teams_own_runs()
    {
        var team = Tests with { TestConfiguration = new TestConfiguration("Category!=Integration", null, null) };

        var invocation = TestInvocationBuilder.Build([new(team, TestRunMode.Filtered, ["A.Tests.OrderTests"], "1 of 3")], "/repo", ["--filter", "Priority=1"]).Single();

        Assert.Equal(["test", "tests/A.Tests/A.Tests.csproj", "--filter", "(Priority=1)&(FullyQualifiedName~A.Tests.OrderTests.)"], invocation.Arguments);
    }

    [Fact]
    public void The_team_filter_is_added_only_where_dotnet_test_would_drop_it()
    {
        var team = Tests with { TestConfiguration = new TestConfiguration("Category!=Integration", null, "--filter-not-trait category=failing") };

        Assert.Equal(["--filter", "Category!=Integration", "-c", "Release"], TestFilter.WithTeamFilter(["-c", "Release"], team, DotnetTestMode.VSTest));
        // The testing platform applies the project's own arguments to every run.
        Assert.Equal(["-c", "Release"], TestFilter.WithTeamFilter(["-c", "Release"], team, DotnetTestMode.TestingPlatform));
        Assert.Equal(["--filter=Priority=1"], TestFilter.WithTeamFilter(["--filter=Priority=1"], team, DotnetTestMode.VSTest));
        // A --filter after "--" belongs to the run settings, not to dotnet test.
        Assert.Equal(["--filter", "Category!=Integration", "--", "--filter"], TestFilter.WithTeamFilter(["--", "--filter"], team, DotnetTestMode.VSTest));
        Assert.Equal(["-c", "Release"], TestFilter.WithTeamFilter(["-c", "Release"], Tests, DotnetTestMode.VSTest));
    }

    [Fact]
    public void A_recording_shard_runs_within_the_teams_filter()
    {
        Assert.Equal(["--filter", "(Category!=Integration)&(FullyQualifiedName~A.Tests.OrderTests.)"],
            TestDetta.Coverage.CoverageRecorder.WithinTeamFilter(["--filter", "FullyQualifiedName~A.Tests.OrderTests."], "Category!=Integration"));
        Assert.Equal(["--filter", "Category!=Integration"], TestDetta.Coverage.CoverageRecorder.WithinTeamFilter([], "Category!=Integration"));
        Assert.Equal(["--filter", "FullyQualifiedName~A.Tests.OrderTests."], TestDetta.Coverage.CoverageRecorder.WithinTeamFilter(["--filter", "FullyQualifiedName~A.Tests.OrderTests."], null));
        Assert.Equal(["--filter-class", "A.Tests.OrderTests", "--filter", "Category!=Integration"], TestDetta.Coverage.CoverageRecorder.WithinTeamFilter(["--filter-class", "A.Tests.OrderTests"], "Category!=Integration"));
    }

    [Fact]
    public void The_teams_dotnet_test_arguments_split_into_filter_settings_controlled_options_and_the_rest()
    {
        var team = TeamTestArguments.Parse(["-c", "Release", "--filter", "Category!=Integration", "--settings=ci.runsettings", "--no-build",
            "--logger", "trx", "-e", "TEAM=on", "--filter-not-trait", "category=failing", "--", "RunConfiguration.MaxCpuCount=1"]);

        Assert.Equal("Category!=Integration", team.Filter);
        Assert.Equal("ci.runsettings", team.Settings);
        Assert.Equal(["-e", "TEAM=on", "--filter-not-trait", "category=failing"], team.Others);
        Assert.Equal(["--", "RunConfiguration.MaxCpuCount=1"], team.InlineSettings);
        Assert.Equal("x.runsettings", TeamTestArguments.Parse(["-s", "x.runsettings"]).Settings);
        var none = TeamTestArguments.Parse([]);
        Assert.Null(none.Filter);
        Assert.Null(none.Settings);
        Assert.Empty(none.Others);
        Assert.Empty(none.InlineSettings);
    }
}
