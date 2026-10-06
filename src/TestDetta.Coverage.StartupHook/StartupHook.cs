using System;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// Reports a process a recorded test started, whoever started it (third-party code included), to the
/// recording: the runtime calls <see cref="Initialize"/> before the process's own code when
/// <c>DOTNET_STARTUP_HOOKS</c> names this assembly, which the tracker sets in the test host for the
/// processes it starts. One line goes to the file the recorder named: the bucket that was current in
/// the test host when the process started, and the process's entry assembly and command line.
/// Nothing here may disturb the process, so every failure is swallowed.
/// </summary>
public static class StartupHook
{
    /// <summary>Variable naming the file the lines go to; set by the recorder.</summary>
    public const string OutputVariable = "TESTDETTA_PROCESS_STARTS";

    /// <summary>Variable holding the bucket current in the test host; kept up to date by the tracker.</summary>
    public const string BucketVariable = "TESTDETTA_BUCKET";

    /// <summary>Appends one line for this process: <c>bucket \t entry assembly \t command line arguments</c>, tab separated.</summary>
    public static void Initialize()
    {
        try
        {
            var output = Environment.GetEnvironmentVariable(OutputVariable);
            var bucket = Environment.GetEnvironmentVariable(BucketVariable);
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(bucket))
            {
                return;
            }

            var line = new StringBuilder(Clean(bucket));
            line.Append('\t').Append(Clean(Assembly.GetEntryAssembly()?.Location));
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                line.Append('\t').Append(Clean(argument));
            }

            line.Append('\n');

            // One write of a short line in append mode: lines of processes started together do not interleave.
            var bytes = Encoding.UTF8.GetBytes(line.ToString());
            using var stream = new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception)
        {
            // A process the tests started must run as it would without the recording.
        }
    }

    /// <summary>A value without the tabs and line breaks that separate fields and lines.</summary>
    /// <param name="value">Value to write.</param>
    private static string Clean(string? value) =>
        (value ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
