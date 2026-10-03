using Microsoft.Extensions.Logging;
using Sift.Core;
using Sift.Core.Licensing;

namespace Sift.Cli;

/// <summary>
/// Applies the license to a run of <c>sift affected</c> or <c>sift test</c> (docs/design/licensing.md):
/// without a covering license every test project runs completely and the run says why. Never fails
/// the build, and never uses the network.
/// </summary>
internal static partial class LicenseGate
{
    /// <summary>Checks the license for the repository at <paramref name="repoRoot"/>.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="loggerFactory">Creates the git client's logger.</param>
    /// <param name="environment">Reads environment variables.</param>
    /// <param name="home">The user's home directory.</param>
    public static LicenseStatus Check(string repoRoot, ILoggerFactory loggerFactory, Func<string, string?> environment, string home)
    {
        try
        {
            var inputs = new LicenseInputs(new GitClient(loggerFactory.CreateLogger<GitClient>()), environment, home);
            using (var handler = new SocketsHttpHandler())
            {
                new LicenseRefresher(handler, environment, home, TimeProvider.System, loggerFactory.CreateLogger<LicenseRefresher>())
                    .RefreshAsync(inputs.FindLicenses(repoRoot), LicenseKeys.Trusted).GetAwaiter().GetResult();
            }

            return LicenseCheck.Check(inputs.Gather(repoRoot, DateTimeOffset.UtcNow), LicenseKeys.Trusted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            // git or the file system failed: the run goes on as without a license.
            return new LicenseStatus(LicenseState.Missing, $"the license could not be checked ({ex.Message}); every test runs", null, null);
        }
    }

    /// <summary>
    /// Records <paramref name="status"/> on the analysis and, when <paramref name="enforced"/> and the
    /// license does not cover the run, replaces the plan with every test project of the repository,
    /// run completely.
    /// </summary>
    /// <param name="analysis">Finished analysis.</param>
    /// <param name="status">Result of <see cref="Check"/>.</param>
    /// <param name="enforced">False until launch (<see cref="LicenseKeys.Enforced"/>): the status is only reported.</param>
    /// <param name="logger">Receives the status.</param>
    public static AnalysisResult Apply(AnalysisResult analysis, LicenseStatus status, bool enforced, ILogger logger)
    {
        if (!enforced || status.AllowsSelection)
        {
            LogStatus(logger, status.State, status.Message);
            return analysis with { License = status, LicenseEnforced = enforced };
        }

        LogEveryTest(logger, status.Message);
        var reason = $"license: {status.Message}";
        var everything = analysis.Graph.Projects
            .Where(static p => p.IsTestProject)
            .OrderBy(static p => p.Path, StringComparer.Ordinal)
            .Select(p => new TestProjectSelection(p, TestRunMode.All, [], reason))
            .ToList();
        return analysis with { Plan = new TestPlan(everything, null, 0), License = status, LicenseEnforced = true };
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "License: {State}: {Message}")]
    private static partial void LogStatus(ILogger logger, LicenseState state, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License: {Message}")]
    private static partial void LogEveryTest(ILogger logger, string message);
}
