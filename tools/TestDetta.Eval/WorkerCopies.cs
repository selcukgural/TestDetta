using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TestDetta.Core;

namespace TestDetta.Eval;

/// <summary>
/// Copies of the built clone, one per extra worker, so mutants running at once never share a file the
/// tests write: Scriban's <c>TestAot</c> publishes into a fixed folder in the repository, and two runs
/// at once removed it under each other (2026-10-02). A check run cannot prove that no such race
/// exists, since it shows up only sometimes; separate copies rule it out. Worker 0 uses the clone
/// itself. The copies are made after the schema is written in, so they hold the same binaries, and
/// are deleted on disposal. Every worker also gets its own temporary directory, since many suites
/// write fixed names under the system's one.
/// </summary>
internal sealed partial class WorkerCopies : IDisposable
{
    /// <summary>The copies, without the clone itself, and the temporary directories; all deleted on disposal.</summary>
    private readonly List<string> _copies;

    /// <summary>Receives the copies made and removed.</summary>
    private readonly ILogger _logger;

    /// <summary>Takes ownership of made copies.</summary>
    /// <param name="roots">Root of each worker; the first is the clone.</param>
    /// <param name="temporary">Temporary directory of each worker.</param>
    /// <param name="copies">Directories to delete on disposal, filled while they are made.</param>
    /// <param name="logger">Receives removal failures.</param>
    private WorkerCopies(IReadOnlyList<string> roots, IReadOnlyList<string> temporary, List<string> copies, ILogger logger)
    {
        Roots = roots;
        Temporary = temporary;
        _copies = copies;
        _logger = logger;
    }

    /// <summary>Root directory of each worker; the first is the clone itself.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>
    /// Temporary directory of each worker, in the order of <see cref="Roots"/>. Short paths under
    /// <c>/tmp</c> on Unix: .NET puts Unix domain sockets (named pipes of MSBuild and the test
    /// platform) there, and a socket path may not exceed 104 bytes on macOS. The name has no letter
    /// <c>i</c>: tests compare a temporary path with its <c>ToUpper()</c>, which under a Turkish culture
    /// turns <c>i</c> into <c>İ</c> (Fluid's IncludeTag_Caches_HandleFileSystemCasing then decides the file
    /// system is case-sensitive, unlike in the baseline, whose temporary path had no <c>i</c>).
    /// </summary>
    public IReadOnlyList<string> Temporary { get; }

    /// <summary>Environment of worker <paramref name="index"/>'s processes: its temporary directory, with NuGet's shared locks (<see cref="NuGetScratch"/>).</summary>
    /// <param name="index">Position in <see cref="Roots"/>.</param>
    public IReadOnlyDictionary<string, string> Environment(int index)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TMPDIR"] = Temporary[index] + Path.DirectorySeparatorChar,
            ["TMP"] = Temporary[index],
            ["TEMP"] = Temporary[index],
        };
        NuGetScratch.KeepShared(environment);
        return environment;
    }

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
        var temporaryRoot = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
        var made = new WorkerCopies(
            [repoRoot, .. Enumerable.Range(2, workers - 1).Select(k => $"{root}.worker-{k}")],
            [.. Enumerable.Range(1, workers).Select(k => Path.Combine(temporaryRoot, $"sft-{System.Environment.ProcessId}-w{k}"))],
            copies,
            logger);
        try
        {
            foreach (var temporary in made.Temporary)
            {
                Remove(temporary);
                copies.Add(temporary);
                Directory.CreateDirectory(temporary);
            }

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

    /// <summary>Copies a directory tree, copy-on-write where the file system can (<see cref="TreeCopy.Copy"/>).</summary>
    /// <param name="source">Directory to copy.</param>
    /// <param name="destination">New directory; must not exist.</param>
    internal static void Copy(string source, string destination) => TreeCopy.Copy(source, destination);

    /// <summary>Deletes a copy, read-only files included (<see cref="TreeCopy.Remove"/>).</summary>
    /// <param name="copy">The copy.</param>
    internal static void Remove(string copy) => TreeCopy.Remove(copy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker copy {Copy} made in {Seconds:0.0}s")]
    private static partial void LogCopied(ILogger logger, string copy, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Test output {Output} lies outside the clone; a copy of the clone would share it, so mutants run one at a time")]
    private static partial void LogOutputOutside(ILogger logger, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Worker copy {Copy} could not be removed")]
    private static partial void LogNotRemoved(ILogger logger, string copy, Exception exception);
}
