namespace TestDetta.Tests;

/// <summary>
/// The Action's deprecated <c>license</c> input: workflows written for the paid versions still pass a secret
/// through it, so its value must never reach a step, an environment variable or the log.
/// </summary>
public sealed class ActionLicenseInputTests
{
    // A Windows checkout (core.autocrlf) has CRLF line ends; the assertions are written with LF.
    private static readonly string Action = File.ReadAllText(Path.Combine(RepositoryRoot(), "action.yml")).ReplaceLineEndings("\n");

    [Fact]
    public void The_license_input_is_only_checked_for_a_notice_and_never_passed_on()
    {
        var uses = Action.Split("inputs.license").Length - 1;

        Assert.Equal(1, uses);
        Assert.Contains("      if: inputs.license != ''\n", Action, StringComparison.Ordinal);
        Assert.DoesNotContain("TESTDETTA_LICENSE", Action, StringComparison.Ordinal);
    }

    [Fact]
    public void The_license_input_stays_declared_so_old_workflows_keep_working()
    {
        Assert.Contains("  license:\n    description: \"Deprecated and ignored:", Action, StringComparison.Ordinal);
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
