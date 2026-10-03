using Microsoft.Extensions.Logging.Abstractions;
using Sift.Cli;
using Sift.Core;
using Sift.Core.Licensing;

namespace Sift.Tests;

/// <summary>The license applied to CLI runs: reported always, deciding the run only when enforced, never failing it.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class LicenseGateTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public LicenseGateTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public static class Calc { public static int Tax() => 2; public static int Fee() => 1; }");
        foreach (var name in (string[])["Lib.Tests", "Other.Tests"])
        {
            _repo.Write($"tests/{name}/{name}.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" />
                    {{(name == "Lib.Tests" ? """<ProjectReference Include="../../src/Lib/Lib.csproj" />""" : "")}}
                  </ItemGroup>
                </Project>
                """);
        }

        _repo.Write("tests/Lib.Tests/TaxTests.cs", "namespace Lib.Tests; public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2, Lib.Calc.Tax()); }");
        _repo.Write("tests/Lib.Tests/FeeTests.cs", "namespace Lib.Tests; public class FeeTests { [Fact] public void Fees() => Assert.Equal(1, Lib.Calc.Fee()); }");
        _repo.Write("tests/Other.Tests/OtherTests.cs", "namespace Other.Tests; public class OtherTests { [Fact] public void Holds() => Assert.True(true); }");
        _repo.Commit("base");
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");
    }

    private AnalysisResult Analyze() => new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main");

    private static readonly LicenseStatus Missing = new(LicenseState.Missing, "no Sift license found; every test runs", null, null);

    [Fact]
    public void Analysis_alone_knows_nothing_of_licenses_so_evaluations_are_unaffected()
    {
        var analysis = Analyze();

        Assert.Null(analysis.License);
        Assert.Equal(["Lib.Tests.TaxTests"], analysis.Tests.Single().TestClasses);
    }

    [Fact]
    public void Not_enforced_the_status_is_only_recorded()
    {
        var analysis = LicenseGate.Apply(Analyze(), Missing, enforced: false, NullLogger.Instance);

        Assert.Equal(Missing, analysis.License);
        Assert.False(analysis.LicenseEnforced);
        Assert.Equal(["Lib.Tests.TaxTests"], analysis.Tests.Single().TestClasses);
        Assert.Null(ResultWriter.LicenseLine(analysis.License, analysis.LicenseEnforced));
    }

    [Fact]
    public void Enforced_without_a_covering_license_every_test_project_runs_and_says_why()
    {
        var analysis = LicenseGate.Apply(Analyze(), Missing, enforced: true, NullLogger.Instance);

        Assert.Equal(["Lib.Tests.csproj", "Other.Tests.csproj"], analysis.Tests.Select(static t => Path.GetFileName(t.Project.Path)));
        Assert.All(analysis.Tests, static t => Assert.Equal(TestRunMode.All, t.Mode));
        Assert.All(analysis.Tests, static t => Assert.StartsWith("license: ", t.Reason, StringComparison.Ordinal));

        using var summary = new StringWriter();
        MarkdownSummary.Write(new SummaryInput(analysis, "main", null), summary);
        Assert.Contains("> **License:** `no Sift license found; every test runs`", summary.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Enforced_with_a_covering_license_tests_are_selected()
    {
        var valid = new LicenseStatus(LicenseState.Valid, "license L-1", null, 3);

        var analysis = LicenseGate.Apply(Analyze(), valid, enforced: true, NullLogger.Instance);

        Assert.Equal(["Lib.Tests.TaxTests"], analysis.Tests.Single().TestClasses);
        Assert.Null(ResultWriter.LicenseLine(analysis.License, analysis.LicenseEnforced));
    }

    [Fact]
    public void Json_output_carries_the_license()
    {
        var analysis = LicenseGate.Apply(Analyze(), Missing, enforced: false, NullLogger.Instance);
        Assert.True(CliOptions.TryParse(["affected", "--format", "json"], _repo.Root, out var options, out _));
        using var output = new StringWriter();

        ResultWriter.Write(
            new ResultReport(options, analysis.RepoRoot, new RunStats(1, 3), analysis.Impact, analysis.Plan) { License = analysis.License },
            output);

        Assert.Contains("\"license\": {", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"state\": \"missing\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"enforced\": false", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void License_commands_parse_and_need_a_subcommand()
    {
        Assert.True(CliOptions.TryParse(["license", "status", "--repo", "x"], "/cwd", out var status, out _));
        Assert.Equal((CliCommand.LicenseStatus, "x"), (status.Command, status.Analysis.RepoPath));
        Assert.True(CliOptions.TryParse(["license", "install", "sift1.a.b"], "/cwd", out var install, out _));
        Assert.Equal((CliCommand.LicenseInstall, "sift1.a.b"), (install.Command, install.License.Install));
        Assert.False(CliOptions.TryParse(["license"], "/cwd", out _, out _));
        Assert.False(CliOptions.TryParse(["license", "install"], "/cwd", out _, out _));
    }

    [Fact]
    public void Status_reports_without_failing_and_install_refuses_what_does_not_verify()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // The CLI reads the real environment, and on CI for a public repository the status would be
        // "public repository" instead of the missing license this test is about.
        string[] visibility = ["GITHUB_EVENT_PATH", "CI_PROJECT_VISIBILITY"];
        var previous = visibility.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var name in visibility)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Assert.Equal(0, CliApp.Run(["license", "status", "--repo", _repo.Root], stdout, stderr));
        }
        finally
        {
            for (var i = 0; i < visibility.Length; i++)
            {
                Environment.SetEnvironmentVariable(visibility[i], previous[i]);
            }
        }

        Assert.Contains("License: Missing", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Not enforced yet", stdout.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, CliApp.Run(["license", "install", "sift1.not.genuine"], stdout, stderr));
        Assert.Contains("not valid", stderr.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();
}
