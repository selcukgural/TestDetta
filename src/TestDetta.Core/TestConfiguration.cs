namespace TestDetta.Core;

/// <summary>
/// How a team configures the runs of a test project in its project files (docs/design/team-test-configuration.md).
/// <c>dotnet test</c> applies these to every run, but a filter or run settings file on the command line replaces
/// the project's: TestDetta, which adds its own, keeps these in every run it starts.
/// </summary>
/// <param name="CaseFilter">The project's <c>VSTestTestCaseFilter</c> (VSTest filter syntax), or null.</param>
/// <param name="RunSettings">Full path of the project's run settings file (<c>RunSettingsFilePath</c> or <c>VSTestSetting</c>), or null.</param>
/// <param name="PlatformArguments">The project's <c>TestingPlatformCommandLineArguments</c>, which the testing platform adds to every run, or null.</param>
public sealed record TestConfiguration(string? CaseFilter, string? RunSettings, string? PlatformArguments)
{
    /// <summary>True when nothing is set.</summary>
    public bool IsEmpty => CaseFilter is null && RunSettings is null && PlatformArguments is null;

    /// <summary>A configuration from the raw property values MSBuild reports; empty values count as not set.</summary>
    /// <param name="caseFilter">Value of <c>VSTestTestCaseFilter</c>.</param>
    /// <param name="projectDirectory">Directory of the project, which a relative run settings path starts from.</param>
    /// <param name="runSettings">Value of <c>RunSettingsFilePath</c>, else of <c>VSTestSetting</c>.</param>
    /// <param name="platformArguments">Value of <c>TestingPlatformCommandLineArguments</c>.</param>
    public static TestConfiguration From(string caseFilter, string projectDirectory, string runSettings, string platformArguments) => new(
        caseFilter.Trim() is { Length: > 0 } filter ? filter : null,
        runSettings.Trim() is { Length: > 0 } settings ? Paths.Normalize(Path.IsPathRooted(settings) ? settings : Path.Combine(projectDirectory, settings)) : null,
        platformArguments.Trim() is { Length: > 0 } arguments ? arguments : null);
}
