using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sift.Core;

namespace Sift.Eval;

/// <summary>
/// Copies of the built clone, one per extra worker, so mutants running at once never share a file the
/// tests write: Scriban's <c>TestAot</c> publishes into a fixed folder in the repository, and two runs
/// at once removed it under each other (2026-10-02). A check run cannot prove that no such race
/// exists, since it shows up only sometimes; separate copies rule it out. Worker 0 uses the clone
/// itself. The copies are made after the schema is written in, so they hold the same binaries, and
/// are deleted on disposal.
/// </summary>
internal sealed partial class WorkerCopies : IDisposable
{
    /// <summary>Longest a copy may take; a full copy of a large clone on a disk without copy-on-write takes minutes.</summary>
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The copies, without the clone itself.</summary>
    private readonly List<string> _copies;

    /// <summary>Receives the copies made and removed.</summary>
    private readonly ILogger _logger;

    /// <summary>Takes ownership of made copies.</summary>
    /// <param name="roots">Root of each worker; the first is the clone.</param>
    /// <param name="copies">The copies among them.</param>
    /// <param name="logger">Receives removal failures.</param>
    private WorkerCopies(IReadOnlyList<string> roots, List<string> copies, ILogger logger)
    {
        Roots = roots;
        _copies = copies;
        _logger = logger;
    }

    /// <summary>Root directory of each worker; the first is the clone itself.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>
    /// Makes <paramref name="workers"/> - 1 copies of <paramref name="repoRoot"/> next to it, or returns
    /// null when a test output lies outside the clone: a copy would then share that output with the
    /// clone, so the runs would not be separate.
    /// </summary>
    /// <param name="repoRoot">The built clone.</param>
    /// <param name="workers">Number of workers, the clone's included.</param>
    /// <param name="outputs">Output directories of the test projects.</param>
    /// <param name="logger">Receives the copies made.</param>
    public static WorkerCopies? Create(string repoRoot, int workers, IEnumerable<string> outputs, ILogger logger)
    {
        var root = Paths.Normalize(repoRoot);
        if (outputs.FirstOrDefault(o => !Paths.IsUnder(Paths.Normalize(o), root)) is { } outside)
        {
            LogOutputOutside(logger, outside);
            return null;
        }

        var copies = new List<string>();
        var made = new WorkerCopies([repoRoot, .. Enumerable.Range(2, workers - 1).Select(k => $"{root}.worker-{k}")], copies, logger);
        try
        {
            foreach (var copy in made.Roots.Skip(1))
            {
                // A killed evaluation leaves its copies behind; an old copy would mix in stale files.
                Remove(copy);
                copies.Add(copy);
                var started = Stopwatch.GetTimestamp();
                Copy(root, copy);
                var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                LogCopied(logger, copy, seconds);
            }

            return made;
        }
        catch
        {
            made.Dispose();
            throw;
        }
    }

    /// <summary>Deletes the copies; a copy that cannot be deleted is logged and left behind.</summary>
    public void Dispose()
    {
        foreach (var copy in _copies)
        {
            try
            {
                Remove(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogNotRemoved(_logger, copy, ex);
            }
        }

        _copies.Clear();
    }

    /// <summary>
    /// Copies a directory tree with its symbolic links as links: copy-on-write clones where the file
    /// system has them (APFS through <c>cp -c</c>, Btrfs and XFS through <c>--reflink=auto</c>), which
    /// cost almost no time or space, and a plain copy otherwise.
    /// </summary>
    /// <param name="source">Directory to copy.</param>
    /// <param name="destination">New directory; must not exist.</param>
    private static void Copy(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            CopyTree(new DirectoryInfo(source), destination);
            return;
        }

        List<string> arguments = OperatingSystem.IsMacOS() ? ["-cpR", source, destination] : ["-a", "--reflink=auto", source, destination];
        var psi = new ProcessStartInfo("cp") { RedirectStandardError = true };
        arguments.ForEach(psi.ArgumentList.Add);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("cp could not be started.");
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CopyTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException($"Copying the clone to {destination} did not finish within {CopyTimeout.TotalMinutes:0} minutes.");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"Copying the clone to {destination} failed: {error.Result.Trim()}");
        }
    }

    /// <summary>Copies a directory tree file by file, recreating symbolic links instead of following them.</summary>
    /// <param name="source">Directory to copy.</param>
    /// <param name="destination">New directory.</param>
    private static void CopyTree(DirectoryInfo source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(target, link);
                }
                else
                {
                    File.CreateSymbolicLink(target, link);
                }
            }
            else if (entry is DirectoryInfo directory)
            {
                CopyTree(directory, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    /// <summary>Deletes a copy, read-only files included (git objects are read-only).</summary>
    /// <param name="copy">The copy.</param>
    private static void Remove(string copy)
    {
        if (!Directory.Exists(copy))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(copy, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(copy, recursive: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker copy {Copy} made in {Seconds:0.0}s")]
    private static partial void LogCopied(ILogger logger, string copy, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Test output {Output} lies outside the clone; a copy of the clone would share it, so mutants run one at a time")]
    private static partial void LogOutputOutside(ILogger logger, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Worker copy {Copy} could not be removed")]
    private static partial void LogNotRemoved(ILogger logger, string copy, Exception exception);
}
