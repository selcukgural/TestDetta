using System.Diagnostics;
using System.Text;
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

    /// <summary>Returns the commit where HEAD's history branched off <paramref name="baseRef"/>.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="baseRef">Branch, tag or commit to compare against.</param>
    /// <exception cref="InvalidOperationException">git failed or the two histories share no commit.</exception>
    public string MergeBase(string repoRoot, string baseRef) => Run(repoRoot, "merge-base", baseRef, "HEAD").Single();

    /// <summary>
    /// Reads the content of <paramref name="paths"/> as of <paramref name="revision"/> through a single
    /// <c>git cat-file --batch</c> process, instead of starting one <c>git show</c> per file.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="revision">Commit to read from.</param>
    /// <param name="paths">Paths relative to <paramref name="repoRoot"/> with '/' separators.</param>
    /// <returns>Raw file bytes keyed by path. Paths that do not exist at the revision are absent.</returns>
    /// <exception cref="InvalidOperationException">git could not start, failed, or printed output that could not be parsed.</exception>
    public IReadOnlyDictionary<string, byte[]> ReadFiles(string repoRoot, string revision, IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, byte[]>(paths.Count, StringComparer.Ordinal);
        if (paths.Count == 0)
        {
            return result;
        }

        var psi = CreateStartInfo(repoRoot, ["cat-file", "--batch"]);
        psi.RedirectStandardInput = true;
        LogRunning("cat-file --batch", repoRoot);
        var stopwatch = Stopwatch.StartNew();

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Requests are written on another task: git answers while it reads, and writing everything
        // first would deadlock once its stdout pipe is full.
        var writerTask = Task.Run(() =>
        {
            using var stdin = process.StandardInput;
            foreach (var path in paths)
            {
                stdin.Write(revision);
                stdin.Write(':');
                stdin.Write(path);
                stdin.Write('\n');
            }
        });

        var stdout = process.StandardOutput.BaseStream;
        foreach (var path in paths)
        {
            // Header: "<sha> blob <size>" or "<revision>:<path> missing".
            var header = ReadLine(stdout)
                         ?? throw new InvalidOperationException($"git cat-file ended before answering for {path}.");

            if (header.EndsWith(" missing", StringComparison.Ordinal))
            {
                LogMissingAtRevision(path, revision);
                continue;
            }

            var sizeStart = header.LastIndexOf(' ') + 1;
            if (!header.Contains(" blob ", StringComparison.Ordinal)
                || !int.TryParse(header.AsSpan(sizeStart), out var size))
            {
                throw new InvalidOperationException($"Unexpected git cat-file header for {path}: {header}");
            }

            var content = new byte[size];
            stdout.ReadExactly(content);
            if (stdout.ReadByte() != '\n')
            {
                throw new InvalidOperationException($"git cat-file output for {path} is not followed by a newline.");
            }

            result[path] = content;
        }

        writerTask.GetAwaiter().GetResult();
        process.WaitForExit();
        var stderr = stderrTask.GetAwaiter().GetResult().Trim();
        LogFinished("cat-file --batch", process.ExitCode, stopwatch.ElapsedMilliseconds, result.Count);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git cat-file --batch failed: {stderr}");
        }

        return result;
    }

    /// <summary>Reads bytes up to the next '\n' and decodes them as UTF-8. Null at end of stream.</summary>
    /// <param name="stream">Raw stdout of git.</param>
    /// <remarks>Reads byte by byte because the binary content that follows must not be buffered away.</remarks>
    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            }

            if (b == '\n')
            {
                return Encoding.UTF8.GetString([.. bytes]);
            }

            bytes.Add((byte)b);
        }
    }

    /// <summary>Creates the start info shared by every git invocation.</summary>
    /// <param name="workingDirectory">Directory git runs in.</param>
    /// <param name="args">git arguments, passed without shell quoting.</param>
    private static ProcessStartInfo CreateStartInfo(string workingDirectory, ReadOnlySpan<string> args)
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

        return psi;
    }

    /// <summary>Runs git and returns its non-empty stdout lines.</summary>
    /// <param name="workingDirectory">Directory git runs in.</param>
    /// <param name="args">git arguments, passed without shell quoting.</param>
    /// <exception cref="InvalidOperationException">git could not start or exited with a non-zero code.</exception>
    private List<string> Run(string workingDirectory, params ReadOnlySpan<string> args)
    {
        var psi = CreateStartInfo(workingDirectory, args);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Path} does not exist at {Revision}")]
    private partial void LogMissingAtRevision(string path, string revision);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Count} changed file(s) compared with {BaseRef}")]
    private partial void LogChangedFiles(int count, string baseRef);
}
