using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>Runs the few git commands Sift needs by starting the <c>git</c> executable.</summary>
/// <param name="logger">Receives every git command line, its duration and, at trace level, its output.</param>
public sealed partial class GitClient(ILogger<GitClient> logger)
{
    /// <summary>Returns the root directory of the repository that contains <paramref name="path"/>.</summary>
    /// <param name="path">Any directory inside the repository.</param>
    /// <exception cref="InvalidOperationException">git failed, for example because the path is not in a repository.</exception>
    public string TopLevel(string path) => Run(path, "rev-parse", "--show-toplevel").Single();

    /// <summary>
    /// Files changed between the merge base of <paramref name="baseRef"/> and HEAD, plus uncommitted
    /// and untracked files. Renames are reported as a delete and an add, so both the old and the new
    /// location count as changed.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="baseRef">Branch, tag or commit to compare against, such as <c>origin/main</c>.</param>
    /// <returns>Paths relative to <paramref name="repoRoot"/> with '/' separators, sorted and without duplicates.</returns>
    /// <exception cref="InvalidOperationException">git failed, for example because <paramref name="baseRef"/> does not exist.</exception>
    public IReadOnlyList<string> ChangedFiles(string repoRoot, string baseRef)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        files.UnionWith(Run(repoRoot, "diff", "--name-only", "--no-renames", $"{baseRef}...HEAD"));
        files.UnionWith(Run(repoRoot, "diff", "--name-only", "--no-renames", "HEAD"));
        files.UnionWith(Run(repoRoot, "ls-files", "--others", "--exclude-standard"));

        LogChangedFiles(files.Count, baseRef);
        return [.. files];
    }

    /// <summary>Runs git and returns its non-empty stdout lines.</summary>
    /// <param name="workingDirectory">Directory git runs in.</param>
    /// <param name="args">git arguments, passed without shell quoting.</param>
    /// <exception cref="InvalidOperationException">git could not start or exited with a non-zero code.</exception>
    private List<string> Run(string workingDirectory, params ReadOnlySpan<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Without core.quotepath=off git prints non-ASCII file names as quoted octal escapes.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var commandLine = string.Join(' ', psi.ArgumentList);
        LogRunning(commandLine, workingDirectory);
        var stopwatch = Stopwatch.StartNew();

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");

        // stderr is drained concurrently; if git filled that pipe while we only read stdout, it would block.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            LogOutputLine(line);
            lines.Add(line);
        }

        process.WaitForExit();
        var stderr = stderrTask.GetAwaiter().GetResult().Trim();
        LogFinished(commandLine, process.ExitCode, stopwatch.ElapsedMilliseconds, lines.Count);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {commandLine} failed: {stderr}");
        }

        return lines;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running git {CommandLine} in {WorkingDirectory}")]
    private partial void LogRunning(string commandLine, string workingDirectory);

    [LoggerMessage(Level = LogLevel.Trace, Message = "git> {Line}")]
    private partial void LogOutputLine(string line);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "git {CommandLine} exited with {ExitCode} after {ElapsedMs} ms, {LineCount} line(s)")]
    private partial void LogFinished(string commandLine, int exitCode, long elapsedMs, int lineCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Count} changed file(s) compared with {BaseRef}")]
    private partial void LogChangedFiles(int count, string baseRef);
}
