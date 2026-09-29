using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Loads the base and working-tree versions of changed C# files and runs
/// <see cref="CodeChangeDetector"/> on each pair.
/// </summary>
/// <param name="git">Reads the base versions.</param>
/// <param name="detector">Compares the two versions.</param>
/// <param name="logger">Receives timing and file counts.</param>
public sealed partial class CodeChangeReader(GitClient git, CodeChangeDetector detector, ILogger<CodeChangeReader> logger)
{
    /// <summary>Detects code changes in the C# files among <paramref name="changedFiles"/>.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="baseRevision">Commit the working tree is compared with, normally the merge base.</param>
    /// <param name="changedFiles">All changed paths relative to <paramref name="repoRoot"/>; non-C# files are skipped.</param>
    /// <returns>One entry per changed .cs file, in the order of <paramref name="changedFiles"/>.</returns>
    public IReadOnlyList<CodeChange> Read(string repoRoot, string baseRevision, IReadOnlyList<string> changedFiles)
    {
        var csharpFiles = new List<string>();
        foreach (var file in changedFiles)
        {
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                csharpFiles.Add(file);
            }
        }

        if (csharpFiles.Count == 0)
        {
            return [];
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var oldContents = git.ReadFiles(repoRoot, baseRevision, csharpFiles);
        var changes = new List<CodeChange>(csharpFiles.Count);

        foreach (var file in csharpFiles)
        {
            var oldText = oldContents.TryGetValue(file, out var bytes)
                ? SourceText.From(bytes, bytes.Length)
                : null;

            changes.Add(detector.Detect(file, oldText, ReadWorkingTree(Path.Combine(repoRoot, file))));
        }

        LogRead(csharpFiles.Count, stopwatch.ElapsedMilliseconds);
        return changes;
    }

    /// <summary>Reads a file from the working tree; null when it was deleted.</summary>
    /// <param name="path">Absolute file path.</param>
    private static SourceText? ReadWorkingTree(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return SourceText.From(stream);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Compared {Count} C# file(s) in {ElapsedMs} ms")]
    private partial void LogRead(int count, long elapsedMs);
}
