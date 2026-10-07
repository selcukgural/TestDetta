namespace TestDetta.Tests;

/// <summary>
/// What the Action promises in shadow mode (docs/design/shadow-mode.md): no step before the tests fails the job, the
/// tool is never run when its checksum does not match, and the report artifact cannot collide or fail the job.
/// </summary>
public sealed class ActionShadowTests
{
    private static readonly string Action = File.ReadAllText(Path.Combine(RepositoryRoot(), "action.yml"));

    [Fact]
    public void Every_step_before_the_tests_steps_aside_in_shadow_mode_instead_of_failing_the_job()
    {
        foreach (var step in (string[])["Check git history", "Get TestDetta", "Find comparison base", "Find newest coverage map"])
        {
            Assert.Contains("if [ \"$MODE\" = shadow ]; then", Step(step), StringComparison.Ordinal);
            Assert.Contains("skip=", Step(step), StringComparison.Ordinal);
        }

        Assert.Contains("continue-on-error: ${{ inputs.mode == 'shadow' }}", Step("Set up .NET for TestDetta"), StringComparison.Ordinal);
        Assert.Contains("continue-on-error: ${{ inputs.mode == 'shadow' }}", Step("Restore coverage map"), StringComparison.Ordinal);

        // Skipped, or td ended without a result of the tests: the team's own dotnet test with the same arguments.
        var shadow = Step("Run every test in shadow mode");
        Assert.Contains("dotnet test -c \"$CONFIGURATION\" \"${extra[@]}\"", shadow, StringComparison.Ordinal);
        Assert.Contains("if [ \"$status\" = 0 ] || [ \"$status\" = 3 ]; then exit \"$status\"; fi", shadow, StringComparison.Ordinal);
        Assert.Contains("--shadow-analysis-timeout", shadow, StringComparison.Ordinal);
        Assert.DoesNotContain("TESTDETTA_LICENSE", shadow, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_that_does_not_match_its_checksum_is_never_unpacked()
    {
        var tool = Step("Get TestDetta");
        var check = tool.IndexOf("if [ \"$actual\" != \"$sha512\" ]; then", StringComparison.Ordinal);
        var unpack = tool.IndexOf("unzip", StringComparison.Ordinal);

        Assert.InRange(check, 0, unpack);
        Assert.Contains("fail \"The TestDetta $version package does not match the SHA-512 in tool.lock; it was not run\"", tool[check..unpack], StringComparison.Ordinal);
    }

    [Fact]
    public void The_shadow_report_artifact_has_a_name_of_its_own_per_job_and_leg_and_cannot_fail_the_job()
    {
        var naming = Step("Name the shadow report");
        Assert.Contains("${{ github.job }}", naming, StringComparison.Ordinal);
        Assert.Contains("${{ job.check_run_id }}", naming, StringComparison.Ordinal);
        Assert.Contains("/dev/urandom", naming, StringComparison.Ordinal);

        var upload = Step("Keep the shadow report");
        Assert.Contains("continue-on-error: true", upload, StringComparison.Ordinal);
        Assert.Contains("name: ${{ steps.artifact.outputs.name }}", upload, StringComparison.Ordinal);
    }

    /// <summary>The text of one step of the Action, from its name to the next step.</summary>
    private static string Step(string name)
    {
        var start = Action.IndexOf($"- name: {name}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, name);
        var end = Action.IndexOf("\n    - name: ", start + 1, StringComparison.Ordinal);
        return end < 0 ? Action[start..] : Action[start..end];
    }

    /// <summary>The repository the tests were built from.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "action.yml")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("action.yml not found above " + AppContext.BaseDirectory);
    }
}
