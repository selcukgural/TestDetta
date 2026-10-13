namespace TestDetta.Core;

/// <summary>
/// Keeps Microsoft.Testing.Platform's sockets where they would be when processes get temporary directories of their own.
/// The platform opens Unix domain sockets in the temporary directory (<c>MONITORTOHOST_&lt;guid&gt;</c>, 46 characters),
/// whose path may not exceed 103 bytes on macOS. There the system's temporary directory is 49 characters, so a folder of
/// the process's own under it left no room: every shard of every Orleans test project ended at start with "The named
/// pipe path ... is 104 bytes long" (2026-10-12), and <c>Orleans.Core.Tests</c>, recorded again in one process, did not
/// finish within the limit. The platform takes its pipe folder from a variable of its own, so the processes keep their
/// own temporary directory and the sockets go to the system's, as in the suite's own runs; each has a name of its own.
/// </summary>
public static class TestingPlatformPipes
{
    /// <summary>The variable that moves the platform's pipe folder.</summary>
    public const string Variable = "TESTINGPLATFORM_PIPE_DIRECTORY";

    /// <summary>
    /// Sets <see cref="Variable"/> in <paramref name="environment"/> to the system's temporary directory, unless the
    /// environment or this process already names one. Windows pipes are no files, so nothing is set there.
    /// </summary>
    /// <param name="environment">Variables of the processes about to get their own temporary directory.</param>
    public static void KeepShared(IDictionary<string, string> environment)
    {
        if (OperatingSystem.IsWindows() || environment.ContainsKey(Variable) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            return;
        }

        environment[Variable] = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
    }
}
