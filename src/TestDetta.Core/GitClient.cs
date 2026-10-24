using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>Runs the few git commands TestDetta needs by starting the <c>git</c> executable.</summary>
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

    /// <summary>Files git tracks in the working tree, relative to <paramref name="repoRoot"/> with '/' separators.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    public IReadOnlyList<string> TrackedFiles(string repoRoot) => Run(repoRoot, logOutput: false, "-c", "core.quotePath=false", "ls-files");

    /// <summary>Full hash of the commit HEAD points to.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <exception cref="InvalidOperationException">git failed, for example in a repository without commits.</exception>
    public string Head(string repoRoot) => Run(repoRoot, "rev-parse", "HEAD").Single();

    /// <summary>Returns the commit where HEAD's history branched off <paramref name="baseRef"/>.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="baseRef">Branch, tag or commit to compare against.</param>
    /// <exception cref="InvalidOperationException">git failed or the two histories share no commit.</exception>
    public string MergeBase(string repoRoot, string baseRef) => Run(repoRoot, "merge-base", baseRef, "HEAD").Single();

    /// <summary>
    /// Checks <paramref name="commit"/> out into a new, detached worktree at <paramref name="path"/>, beside the
    /// working tree and without touching it.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="path">Directory of the new worktree; must not exist.</param>
    /// <param name="commit">Commit to check out.</param>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    public void AddWorktree(string repoRoot, string path, string commit) => Run(repoRoot, "worktree", "add", "--detach", "--quiet", path, commit);

    /// <summary>
    /// <see cref="AddWorktree(string, string, string)"/> within a time limit and cancellable: a checkout of a large
    /// repository can take minutes, and shadow mode's classification budget covers it. When the limit passes or
    /// <paramref name="cancel"/> fires, git is killed and an exception thrown; what it left (the folder, its
    /// registration under <c>.git/worktrees</c>) is for the caller to remove with <see cref="RemoveWorktree"/> or
    /// <see cref="PruneWorktrees"/>.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="path">Directory of the new worktree; must not exist.</param>
    /// <param name="commit">Commit to check out.</param>
    /// <param name="limit">Longest the checkout may take; null for no limit.</param>
    /// <param name="cancel">Stops the checkout.</param>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    /// <exception cref="TimeoutException">The checkout ran past <paramref name="limit"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired.</exception>
    public void AddWorktree(string repoRoot, string path, string commit, TimeSpan? limit, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var psi = CreateStartInfo(repoRoot, ["worktree", "add", "--detach", "--quiet", path, commit]);
        var commandLine = string.Join(' ', psi.ArgumentList);
        LogRunning(commandLine, repoRoot);
        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        bool exited;
        using (cancel.Register(() => Kill(process)))
        {
            exited = process.WaitForExit(limit is { } time ? (time < TimeSpan.Zero ? TimeSpan.Zero : time) : Timeout.InfiniteTimeSpan);
        }

        if (!exited)
        {
            Kill(process);
            process.WaitForExit();
        }

        var stderr = stderrTask.GetAwaiter().GetResult().Trim();
        _ = stdoutTask.GetAwaiter().GetResult();
        LogFinished(commandLine, process.ExitCode, stopwatch.ElapsedMilliseconds, 0);
        cancel.ThrowIfCancellationRequested();
        if (!exited)
        {
            throw new TimeoutException($"git {commandLine} did not finish within {limit!.Value.TotalSeconds:0} s");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {commandLine} failed: {stderr}");
        }
    }

    /// <summary>
    /// Forgets worktrees whose folder is gone (<c>git worktree prune</c>), so one whose removal failed, or a
    /// checkout that was stopped half way, leaves no entry under <c>.git/worktrees</c> once its folder is deleted.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    public void PruneWorktrees(string repoRoot) => Run(repoRoot, "worktree", "prune");

    /// <summary>
    /// Deletes the registration git made for <paramref name="path"/> under <c>.git/worktrees</c> when the checkout was
    /// stopped before it was complete: an entry named after the folder (git adds a number when the name is taken)
    /// that has no <c>gitdir</c> file yet, or one that points to <paramref name="path"/>. Nothing else is touched.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="path">Directory of the worktree.</param>
    private void RemoveHalfMadeRegistration(string repoRoot, string path)
    {
        var common = Path.GetFullPath(Run(repoRoot, "rev-parse", "--git-common-dir").Single(), repoRoot);
        var registrations = Path.Combine(common, "worktrees");
        if (!Directory.Exists(registrations))
        {
            return;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var target = Paths.ResolveLinks(Path.Combine(path, ".git"));
        foreach (var entry in Directory.EnumerateDirectories(registrations).ToList())
        {
            var entryName = Path.GetFileName(entry);
            if (!entryName.StartsWith(name, StringComparison.Ordinal) || !entryName[name.Length..].All(char.IsAsciiDigit))
            {
                continue;
            }

            var gitdir = Path.Combine(entry, "gitdir");
            if (!File.Exists(gitdir) || Paths.ResolveLinks(File.ReadAllText(gitdir).Trim()) == target)
            {
                Directory.Delete(entry, recursive: true);
            }
        }
    }

    /// <summary>Kills a process with its children; one that has already exited is left alone.</summary>
    /// <param name="process">The process.</param>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It ended on its own in the meantime.
        }
    }

    /// <summary>
    /// Removes a worktree <see cref="AddWorktree(string, string, string)"/> made, with whatever its build left in it;
    /// forced twice, so a checkout that was stopped half way (git keeps it locked as "initializing") goes too.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="path">Directory of the worktree.</param>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    public void RemoveWorktree(string repoRoot, string path) => Run(repoRoot, "worktree", "remove", "--force", "--force", path);

    /// <summary>
    /// Removes a worktree whatever state it is in: <see cref="RemoveWorktree"/>, and when git cannot, its folder,
    /// every registration under <c>.git/worktrees</c> whose folder is gone (<see cref="PruneWorktrees"/>), and its own
    /// registration if git stopped before it was complete (no <c>gitdir</c> yet, so neither remove nor prune knows it,
    /// and locked as "initializing"). Never throws; true when nothing is left.
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="path">Directory of the worktree.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Clean-up after a failure or a cancellation: it must not throw over the error it follows.")]
    public bool DiscardWorktree(string repoRoot, string path)
    {
        try
        {
            RemoveWorktree(repoRoot, path);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogDiscardFailed(path, ex.Message);
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            PruneWorktrees(repoRoot);
            RemoveHalfMadeRegistration(repoRoot, path);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogDiscardFailed(path, ex.Message);
            return false;
        }
    }

    /// <summary>Files that differ between two commits; renames count as a delete and an add.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="from">Older commit.</param>
    /// <param name="to">Newer commit.</param>
    /// <returns>Paths relative to <paramref name="repoRoot"/> with '/' separators, sorted.</returns>
    /// <exception cref="InvalidOperationException">git failed, for example because a commit does not exist.</exception>
    public IReadOnlyList<string> ChangedBetween(string repoRoot, string from, string to) =>
        [.. new SortedSet<string>(Run(repoRoot, "diff", "--name-only", "--no-renames", from, to), StringComparer.Ordinal)];

    /// <summary>
    /// How many first-parent commits <paramref name="descendant"/> is ahead of <paramref name="ancestor"/>;
    /// null when <paramref name="ancestor"/> is not an ancestor of it or is not in the clone (a shallow
    /// checkout, a commit from another history).
    /// </summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="ancestor">Commit expected to be an ancestor, such as the commit a coverage map was recorded at.</param>
    /// <param name="descendant">Newer commit, such as the merge base.</param>
    public int? FirstParentDistance(string repoRoot, string ancestor, string descendant)
    {
        List<string> mergeBase;
        try
        {
            mergeBase = Run(repoRoot, "merge-base", ancestor, descendant);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var resolved = Run(repoRoot, "rev-parse", "--verify", "--quiet", ancestor + "^{commit}").Single();
        if (mergeBase is not [var commit] || !string.Equals(commit, resolved, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.Parse(Run(repoRoot, "rev-list", "--count", "--first-parent", $"{ancestor}..{descendant}").Single(), System.Globalization.CultureInfo.InvariantCulture);
    }

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

    /// <summary><paramref name="commit"/> and its first-parent ancestors, newest first, at most <paramref name="count"/>.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="commit">A commit or ref.</param>
    /// <param name="count">How many commits to list.</param>
    public IReadOnlyList<string> FirstParentAncestors(string repoRoot, string commit, int count) =>
        Run(repoRoot, "rev-list", "--first-parent", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), commit);

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
    private List<string> Run(string workingDirectory, params ReadOnlySpan<string> args) => Run(workingDirectory, logOutput: true, args);

    /// <summary>Runs git and returns its non-empty stdout lines.</summary>
    /// <param name="workingDirectory">Directory git runs in.</param>
    /// <param name="logOutput">False for output that may hold credentials or personal data.</param>
    /// <param name="args">git arguments, passed without shell quoting.</param>
    /// <exception cref="InvalidOperationException">git could not start or exited with a non-zero code.</exception>
    private List<string> Run(string workingDirectory, bool logOutput, params ReadOnlySpan<string> args)
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

            if (logOutput)
            {
                LogOutputLine(line);
            }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The worktree {Path} could not be removed: {Reason}")]
    private partial void LogDiscardFailed(string path, string reason);

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
