using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>
/// Loads the base and working-tree versions of changed C# and Visual Basic files and runs
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
    /// <param name="symbols">The symbols the compiler uses for a file (relative path), from its projects; null when unknown.</param>
    /// <param name="rootNamespaces">The root namespace of the project compiling a file (Visual Basic); null for none.</param>
    /// <param name="overlay">Working-tree text that stands in for a file on disk, by relative path (<see cref="AnalysisOptions.WorkingTreeOverlay"/>); null for none.</param>
    /// <returns>One entry per changed .cs or .vb file, in the order of <paramref name="changedFiles"/>.</returns>
    public IReadOnlyList<CodeChange> Read(
        string repoRoot,
        string baseRevision,
        IReadOnlyList<string> changedFiles,
        FileSymbols? symbols = null,
        Func<string, string>? rootNamespaces = null,
        IReadOnlyDictionary<string, string>? overlay = null)
    {
        var csharpFiles = new List<string>();
        foreach (var file in changedFiles)
        {
            if (SourceLanguage.IsSource(file))
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
            var current = overlay is not null && overlay.TryGetValue(file, out var text) ? SourceText.From(text) : ReadWorkingTree(Path.Combine(repoRoot, file));
            var old = Text(oldContents, file);
            var rootNamespace = rootNamespaces?.Invoke(file) ?? string.Empty;
            var change = detector.Detect(file, old, current, symbols?.All(file) ?? [], rootNamespace);
            if (symbols?.RunFramework?.Invoke(file) is { } runSets)
            {
                change = change with { SeenByRunFramework = CodeChangeDetector.SeenUnder(change, old, current, runSets, rootNamespace) };
            }

            changes.Add(change);
        }

        LogRead(csharpFiles.Count, stopwatch.ElapsedMilliseconds);
        return changes;
    }

    /// <summary>Detects code changes in the C# files among <paramref name="changedFiles"/> between two commits.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="from">Older commit.</param>
    /// <param name="to">Newer commit.</param>
    /// <param name="changedFiles">Paths changed between the two, relative to <paramref name="repoRoot"/>; non-C# files are skipped.</param>
    /// <param name="symbols">The symbols the compiler uses for a file (relative path); null when unknown.</param>
    /// <param name="rootNamespaces">The root namespace of the project compiling a file (Visual Basic); null for none.</param>
    /// <returns>One entry per changed .cs or .vb file, in the order of <paramref name="changedFiles"/>.</returns>
    public IReadOnlyList<CodeChange> ReadBetween(
        string repoRoot,
        string from,
        string to,
        IReadOnlyList<string> changedFiles,
        FileSymbols? symbols = null,
        Func<string, string>? rootNamespaces = null)
    {
        var csharpFiles = changedFiles.Where(SourceLanguage.IsSource).ToList();
        if (csharpFiles.Count == 0)
        {
            return [];
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var oldContents = git.ReadFiles(repoRoot, from, csharpFiles);
        var newContents = git.ReadFiles(repoRoot, to, csharpFiles);
        var changes = csharpFiles.ConvertAll(file => detector.Detect(file, Text(oldContents, file), Text(newContents, file), symbols?.All(file) ?? [], rootNamespaces?.Invoke(file) ?? string.Empty));
        LogRead(csharpFiles.Count, stopwatch.ElapsedMilliseconds);
        return changes;
    }

    /// <summary>The text of <paramref name="file"/> among files read from git; null when it did not exist.</summary>
    /// <param name="contents">Raw bytes by path.</param>
    /// <param name="file">Relative path.</param>
    private static SourceText? Text(IReadOnlyDictionary<string, byte[]> contents, string file) =>
        contents.TryGetValue(file, out var bytes) ? SourceText.From(bytes, bytes.Length) : null;

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

/// <summary>The preprocessor symbols the projects compiling a file use for it, by repository-relative path.</summary>
/// <param name="All">One set per target framework of every project compiling the file; empty when unknown.</param>
/// <param name="RunFramework">
/// The sets of the one framework the tests run for (<c>-f</c>), one per project compiling the file, or null when no
/// framework was given or a project compiling the file does not target it: the changes that framework's builds see
/// (<see cref="CodeChange.SeenByRunFramework"/>, docs/design/framework-only-code.md).
/// </param>
public sealed record FileSymbols(
    Func<string, IReadOnlyCollection<IReadOnlyList<string>>> All,
    Func<string, IReadOnlyCollection<IReadOnlyList<string>>?>? RunFramework = null);
