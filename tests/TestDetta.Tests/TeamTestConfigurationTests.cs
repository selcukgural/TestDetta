using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// TestDetta narrows the team's runs; it never widens or changes them (docs/design/team-test-configuration.md): a test the
/// team's filter excludes stays out of recordings and selections, and recording runs under the team's run settings, with
/// TestDetta's own elements added.
/// </summary>
public sealed class TeamTestConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-team-" + Guid.NewGuid().ToString("N"));

    public TeamTestConfigurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Recording_adds_its_settings_to_a_copy_of_the_teams_run_settings()
    {
        var team = Path.Combine(_directory, "ci.runsettings");
        File.WriteAllText(team, """
            <RunSettings>
              <RunConfiguration>
                <TestCaseFilter>Category!=Integration</TestCaseFilter>
                <MaxCpuCount>4</MaxCpuCount>
                <EnvironmentVariables><TEAM_SETTING>on</TEAM_SETTING></EnvironmentVariables>
                <DisableParallelization>false</DisableParallelization>
              </RunConfiguration>
              <TestRunParameters><Parameter name="baseUrl" value="http://localhost" /></TestRunParameters>
              <InProcDataCollectionRunSettings><InProcDataCollectors><InProcDataCollector friendlyName="Team" uri="team://collector" /></InProcDataCollectors></InProcDataCollectionRunSettings>
            </RunSettings>
            """);

        var written = XDocument.Load(CoverageRecorder.WriteRunSettings(_directory, new CoverageRecorder.SettingsParts(WithCollector: true, Sequential: true, "/t/TestDetta.Coverage.Runtime.dll"), team)).Root!;

        var configuration = written.Element("RunConfiguration")!;
        Assert.Equal("Category!=Integration", configuration.Element("TestCaseFilter")!.Value);
        Assert.Equal("4", configuration.Element("MaxCpuCount")!.Value);
        Assert.Equal("on", configuration.Element("EnvironmentVariables")!.Element("TEAM_SETTING")!.Value);
        Assert.Equal("http://localhost", written.Element("TestRunParameters")!.Element("Parameter")!.Attribute("value")!.Value);
        // TestDetta's own elements win where they meet the team's: one test at a time, no AppDomains.
        Assert.Equal("true", configuration.Element("DisableParallelization")!.Value);
        Assert.Equal("true", configuration.Element("DisableAppDomain")!.Value);
        Assert.Equal(["Team", "TestDetta"], written.Descendants("InProcDataCollector").Select(static c => c.Attribute("friendlyName")!.Value));
        Assert.Equal("1", written.Element("MSTest")!.Element("Parallelize")!.Element("Workers")!.Value);
    }

    [Fact]
    public void Recording_without_team_settings_writes_its_own_alone()
    {
        var written = XDocument.Load(CoverageRecorder.WriteRunSettings(_directory, new CoverageRecorder.SettingsParts(WithCollector: false, Sequential: true, "/t/x.dll"), teamSettings: null)).Root!;

        Assert.Equal("true", written.Element("RunConfiguration")!.Element("DisableParallelization")!.Value);
        Assert.Empty(written.Descendants("InProcDataCollector"));
    }

    [Fact]
    public void A_run_settings_file_that_cannot_be_read_stops_the_recording_rather_than_change_the_settings()
    {
        var missing = Path.Combine(_directory, "missing.runsettings");

        var error = Assert.Throws<InvalidOperationException>(() => CoverageRecorder.WriteRunSettings(_directory, new CoverageRecorder.SettingsParts(true, true, "/t/x.dll"), missing));

        Assert.Contains("missing.runsettings could not be read", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--no-build" }, null, new[] { "--no-build", "--ignore-exit-code", "8" })]
    [InlineData(new[] { "--ignore-exit-code", "2" }, null, new[] { "--ignore-exit-code", "2;8" })]
    [InlineData(new[] { "--ignore-exit-code=2;8" }, null, new[] { "--ignore-exit-code=2;8" })]
    [InlineData(new[] { "--ignore-exit-code:2" }, null, new[] { "--ignore-exit-code:2;8" })]
    [InlineData(new[] { "--no-build" }, "--filter-not-trait category=failing --ignore-exit-code 8", new[] { "--no-build" })]
    [InlineData(new[] { "--no-build" }, "--report-trx --ignore-exit-codes-are-not-this 8", new[] { "--no-build", "--ignore-exit-code", "8" })]
    public void An_empty_shard_accepts_zero_tests_without_repeating_the_teams_ignore_exit_code(string[] arguments, string? platformArguments, string[] expected) =>
        Assert.Equal(expected, CoverageRecorder.AcceptingZeroTests(arguments, platformArguments));

    [Fact]
    [Trait("Category", "Slow")]
    public void Recording_in_shards_runs_when_the_project_already_ignores_exit_code_8()
    {
        // EF Core and Orleans do this: the testing platform rejects a second --ignore-exit-code with exit code 5.
        using var repo = new GitRepoFixture();
        repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");
        repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        repo.Write("src/Calc/Calc.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        repo.Write("src/Calc/Calculator.cs", "namespace Calc; public static class Calculator { public static int Add(int a, int b) => a + b; public static int Mul(int a, int b) => a * b; }");
        repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <TestingPlatformCommandLineArguments>$(TestingPlatformCommandLineArguments) --ignore-exit-code 8</TestingPlatformCommandLineArguments>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="4.0.1" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("tests/Calc.Tests/Tests.cs", """
            using Xunit;
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calculator.Add(1, 2)); }
            public class MulTests { [Fact] public void Muls() => Assert.Equal(6, Calculator.Mul(2, 3)); }
            """);
        repo.Commit("base");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, new MsBuildProjectEvaluator(NullLoggerFactory.Instance, diskCache: false)).Load(repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        Assert.Contains("--ignore-exit-code 8", testProject.TestConfiguration?.PlatformArguments, StringComparison.Ordinal);
        var output = new StringWriter();

        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance).Record(new RecordRequest(repo.Root, graph, testProject, new RecordOptions(Parallelism: 2)), output);

        Assert.True(passed, output.ToString());
        Assert.Empty(map.FailedClasses!);
        Assert.Contains(new CoveredMethod("Calc.Calculator", "Mul"), map.Classes["Calc.Tests.MulTests"].Select(id => map.Methods[id]));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Recording_and_selection_run_only_what_the_team_runs_under_the_teams_settings()
    {
        using var repo = new GitRepoFixture();
        repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        repo.Write("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        repo.Write("src/Shop/Prices.cs", "namespace Shop; public static class Prices { public static int Net(int gross) => gross - 1; }");
        repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <VSTestTestCaseFilter>Category!=Integration</VSTestTestCaseFilter>
                <RunSettingsFilePath>$(MSBuildProjectDirectory)/ci.runsettings</RunSettingsFilePath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("tests/Shop.Tests/ci.runsettings", """
            <RunSettings>
              <RunConfiguration>
                <TestCaseFilter>Category!=Slow</TestCaseFilter>
                <EnvironmentVariables><TEAM_SETTING>on</TEAM_SETTING></EnvironmentVariables>
              </RunConfiguration>
            </RunSettings>
            """);
        repo.Write("tests/Shop.Tests/Tests.cs", """
            using Xunit;
            namespace Shop.Tests;
            public class PriceTests
            {
                // Needs the team's environment: it fails under other settings.
                [Fact] public void Nets() { Assert.Equal("on", System.Environment.GetEnvironmentVariable("TEAM_SETTING")); Assert.Equal(1, Prices.Net(2)); }
                [Fact, Trait("Category", "Integration")] public void NeedsDatabase() => Assert.Fail("an integration test the team never runs ran");
                [Fact, Trait("Category", "Slow")] public void TakesAnHour() => Assert.Fail("a slow test the team never runs ran");
            }
            public class OtherTests { [Fact] public void Holds() => Assert.True(true); }
            """);
        repo.Commit("base");
        var root = new GitClient(NullLogger<GitClient>.Instance).TopLevel(repo.Root);
        var publicRepository = new CliHost(static name => name == "CI_PROJECT_VISIBILITY" ? "public" : null, Path.Combine(repo.Root, ".testdetta", "home"));

        var record = new StringWriter();
        Assert.True(CliApp.Run(["record", "--repo", repo.Root], record, record, publicRepository) == 0, record.ToString());
        var map = CoverageMapStore.Read(CoverageMapStore.PathFor(root, Path.Combine(root, "tests/Shop.Tests/Shop.Tests.csproj")))!;
        Assert.Empty(map.FailedClasses!);
        Assert.Contains(new CoveredMethod("Shop.Prices", "Net"), map.Classes["Shop.Tests.PriceTests"].Select(id => map.Methods[id]));

        repo.Replace("src/Shop/Prices.cs", "gross - 1", "gross + 1 - 2");
        var test = new StringWriter();
        var exit = CliApp.Run(["test", "--repo", repo.Root, "--base", "HEAD"], test, test, publicRepository);

        Assert.True(exit == 0, test.ToString());
        Assert.Contains("Shop.Tests.PriceTests", test.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("never runs ran", test.ToString(), StringComparison.Ordinal);
        Assert.Contains("tests/Shop.Tests/Shop.Tests.csproj: your test case filter Category!=Integration, run settings tests/Shop.Tests/ci.runsettings kept in every run", test.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_job_summary_says_which_test_configuration_was_kept()
    {
        var project = new ProjectNode("/repo/tests/A.Tests/A.Tests.csproj", "/repo/tests/A.Tests", true, [])
        {
            TestConfiguration = new TestConfiguration("Category!=Integration", "/repo/ci.runsettings", null),
        };
        var plain = new ProjectNode("/repo/tests/B.Tests/B.Tests.csproj", "/repo/tests/B.Tests", true, []);
        TestProjectSelection[] tests =
        [
            new(project, TestRunMode.Filtered, ["A.Tests.OrderTests"], "1 of 2"),
            new(plain, TestRunMode.Filtered, ["B.Tests.CartTests"], "1 of 2"),
            new(project with { Path = "/repo/tests/C.Tests/C.Tests.csproj" }, TestRunMode.Skipped, [], "nothing reaches it"),
        ];

        Assert.Equal([("tests/A.Tests/A.Tests.csproj", "test case filter Category!=Integration, run settings ci.runsettings")], ResultWriter.TeamConfiguration(tests, "/repo"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Recording_keeps_the_filter_and_settings_the_team_passes_on_the_command_line()
    {
        using var repo = new GitRepoFixture();
        repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        repo.Write("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        repo.Write("src/Shop/Prices.cs", "namespace Shop; public static class Prices { public static int Net(int gross) => gross - 1; }");
        repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
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
        repo.Write("ci.runsettings", """
            <RunSettings><RunConfiguration><EnvironmentVariables><TEAM_SETTING>on</TEAM_SETTING></EnvironmentVariables></RunConfiguration></RunSettings>
            """);
        repo.Write("tests/Shop.Tests/Tests.cs", """
            using Xunit;
            namespace Shop.Tests;
            public class PriceTests
            {
                [Fact] public void Nets() { Assert.Equal("on", System.Environment.GetEnvironmentVariable("TEAM_SETTING")); Assert.Equal(1, Prices.Net(2)); }
                [Fact, Trait("Category", "Integration")] public void NeedsDatabase() => Assert.Fail("an integration test the team never runs ran");
            }
            """);
        repo.Commit("base");
        var root = new GitClient(NullLogger<GitClient>.Instance).TopLevel(repo.Root);

        var record = new StringWriter();
        var exit = CliApp.Run(["record", "--repo", repo.Root, "--", "--filter", "Category!=Integration", "--settings", "ci.runsettings"], record, record);

        Assert.True(exit == 0, record.ToString());
        var map = CoverageMapStore.Read(CoverageMapStore.PathFor(root, Path.Combine(root, "tests/Shop.Tests/Shop.Tests.csproj")))!;
        Assert.Empty(map.FailedClasses!);
        Assert.DoesNotContain("never runs ran", record.ToString(), StringComparison.Ordinal);
    }
}
