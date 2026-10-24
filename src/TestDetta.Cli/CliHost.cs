namespace TestDetta.Cli;

/// <summary>
/// What the CLI reads from the machine: the environment variables of the console style (<c>NO_COLOR</c>,
/// <c>FORCE_COLOR</c>, <c>TESTDETTA_THEME</c>, <c>COLUMNS</c>, <c>GITHUB_ACTIONS</c>). The process's own outside tests;
/// a test passes its own to see which of them a command reads.
/// </summary>
/// <param name="Environment">Reads an environment variable; null when it is not set.</param>
internal sealed record CliHost(Func<string, string?> Environment)
{
    /// <summary>The process's environment.</summary>
    public static CliHost Default { get; } = new(System.Environment.GetEnvironmentVariable);
}
