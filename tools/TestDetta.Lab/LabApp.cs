using TestDetta.Cli;

namespace TestDetta.Lab;

/// <summary>
/// Entry point of the lab, the tool we answer development questions with (docs/lab.md): who ran or used something
/// according to a map, what a change in a working tree selects and why, and how two evaluation runs differ. Each
/// question used to be a script written on the spot; the lab answers it the same way every time.
/// </summary>
internal static class LabApp
{
    /// <summary>The usage text.</summary>
    public const string Usage = """
        TestDetta lab: development questions, answered from maps, working trees and evaluation runs.

        Usage:
          lab who <map.json | maps directory> <pattern>
              Who ran, read a value built by, loaded, scanned or read what the pattern names. The pattern is matched
              against Type.Member and Type of the map's methods and against the names of loaded outputs and read files;
              * stands for any text (NLog.Targets.*, Plugin).
          lab whatif <repository> [--base <ref>]
              What the working tree's change against the base (default HEAD) selects with the maps in the repository,
              and the evidence for every class.
          lab ab <run A> <run B>
              Compares two evaluation runs (folders with <repository>/synthetic.json) mutant by mutant: shares,
              narrower and wider changes, misses on either side, and mutants only one side has.
        """;

    /// <summary>Runs one command and returns its exit code: 0 on success, 2 for usage errors, 1 when the input cannot be read.</summary>
    /// <param name="args">Raw command line.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="error">Where usage and input errors go.</param>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var style = ConsoleStyle.For(new DisplaySettings(), Environment.GetEnvironmentVariable, Console.IsOutputRedirected ? StreamKind.Redirected : StreamKind.Terminal, output.Encoding, width: null);
        try
        {
            switch (args)
            {
                case ["who", var maps, var pattern]:
                    MapExplorer.Write(MapExplorer.Who(MapExplorer.Load(maps), pattern), output, style);
                    return 0;
                case ["whatif", var repository, .. var rest] when rest is [] or ["--base", _]:
                    WhatIf.Write(WhatIf.Run(repository, rest is [_, var baseRef] ? baseRef : "HEAD"), output, style);
                    return 0;
                case ["ab", var first, var second]:
                    RunComparison.Write(RunComparison.Compare(first, second), output, style);
                    return 0;
                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            error.WriteLine($"lab: {ex.Message}");
            return 1;
        }
    }
}
