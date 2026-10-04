using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Core.Licensing;

namespace TestDetta.Cli;

/// <summary>
/// Applies the license to a run of <c>td affected</c> or <c>td test</c> (docs/design/licensing.md):
/// without a covering license every test project runs completely and the run says why. Never fails
/// the build; its only network use is the optional refresh, which runs next to the analysis.
/// </summary>
internal static partial class LicenseGate
{
    /// <summary>
    /// Starts the daily license refresh (<see cref="LicenseRefresher"/>) in the background, so that it
    /// runs while the analysis does; <see cref="Check"/> waits for it. Never throws: a failure here is
    /// met again, and reported, by <see cref="Check"/>.
    /// </summary>
    /// <param name="repoPath">Repository path, or a folder inside it.</param>
    /// <param name="loggerFactory">Creates the loggers.</param>
    /// <param name="environment">Reads environment variables.</param>
    /// <param name="home">The user's home directory.</param>
    public static Task Refresh(string repoPath, ILoggerFactory loggerFactory, Func<string, string?> environment, string home) => Task.Run(async () =>
    {
        try
        {
            var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
            var inputs = new LicenseInputs(git, environment, home);
            using var handler = new SocketsHttpHandler();
            await new LicenseRefresher(handler, environment, home, TimeProvider.System, loggerFactory.CreateLogger<LicenseRefresher>())
                .RefreshAsync(inputs.FindLicenses(git.TopLevel(repoPath)), LicenseKeys.Trusted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or FormatException)
        {
            // git or the file system failed; Check reads the same inputs and says so.
        }
    });

    /// <summary>Checks the license for the repository at <paramref name="repoRoot"/>.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="loggerFactory">Creates the git client's logger.</param>
    /// <param name="environment">Reads environment variables.</param>
    /// <param name="home">The user's home directory.</param>
    /// <param name="refresh">The refresh <see cref="Refresh"/> started earlier; null starts one now and waits for it.</param>
    public static LicenseStatus Check(string repoRoot, ILoggerFactory loggerFactory, Func<string, string?> environment, string home, Task? refresh = null)
    {
        try
        {
            (refresh ?? Refresh(repoRoot, loggerFactory, environment, home)).GetAwaiter().GetResult();
            var inputs = new LicenseInputs(new GitClient(loggerFactory.CreateLogger<GitClient>()), environment, home);
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
