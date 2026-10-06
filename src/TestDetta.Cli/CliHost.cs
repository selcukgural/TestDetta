namespace TestDetta.Cli;

/// <summary>
/// What the CLI reads from the machine for the license: environment variables (<c>TESTDETTA_LICENSE</c> and the
/// others in <see cref="TestDetta.Core.Licensing.LicenseInputs"/>) and the home directory with the installed one.
/// The process's own outside tests; a test passes its own to see which of them a command reads.
/// </summary>
/// <param name="Environment">Reads an environment variable; null when it is not set.</param>
/// <param name="Home">The user's home directory.</param>
internal sealed record CliHost(Func<string, string?> Environment, string Home)
{
    /// <summary>The process's environment and the user's home directory.</summary>
    public static CliHost Default { get; } = new(System.Environment.GetEnvironmentVariable, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
}
