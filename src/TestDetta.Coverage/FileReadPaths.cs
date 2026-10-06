using System.Security.Cryptography;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Turns the full paths the tracker saw read into the repository paths a change names
/// (docs/design/runtime-file-reads.md). A path outside the repository is dropped, so a cached map holds
/// no user names, and so is <c>.testdetta/</c> and <c>.git/</c>. A file under a <c>bin</c> or <c>obj</c>
/// folder is a copy a build made: it stands for the tracked files with its name and content, or with its
/// name when no content matches (a build step that changed it), and for nothing when no tracked file has
/// its name (the build wrote it) or it does not exist (it was only looked for). A listed directory under <c>bin</c> or <c>obj</c> stands for the files
/// in it, as above, and for the repository directories they were copied from, where a new file would land.
/// </summary>
/// <param name="repoRoot">Repository root.</param>
/// <param name="tracked">Files git tracks, relative to <paramref name="repoRoot"/> with '/' separators.</param>
internal sealed class FileReadPaths(string repoRoot, IReadOnlyList<string> tracked)
{
    /// <summary>Most files of a listed output directory followed; a larger listing is followed that far.</summary>
    private const int ListedFilesLimit = 1000;

    /// <summary>Tracked files by file name.</summary>
    private readonly Lazy<ILookup<string, string>> _byName = new(() => tracked.ToLookup(static f => f[(f.LastIndexOf('/') + 1)..], StringComparer.Ordinal));

    /// <summary>Content hashes of tracked files, computed when first needed.</summary>
    private readonly Dictionary<string, byte[]?> _hashes = new(StringComparer.Ordinal);

    /// <summary>The repository root with its symbolic links resolved, as the paths are compared.</summary>
    private readonly Lazy<string> _root = new(() => Paths.ResolveLinks(repoRoot));

    /// <summary>Resolved paths, since a file is read by many classes.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _resolved = new(StringComparer.Ordinal);

    /// <summary>The repository paths a full path the tracker saw stands for; a directory ends in '/'.</summary>
    /// <param name="full">Full path with '/' separators; a listed directory ends in '/'.</param>
    public IReadOnlyList<string> Resolve(string full)
    {
        if (!_resolved.TryGetValue(full, out var paths))
        {
            paths = Compute(full);
            _resolved[full] = paths;
        }

        return paths;
    }

    /// <summary>Resolves every path of a set, sorted and without duplicates.</summary>
    /// <param name="fulls">Full paths.</param>
    public IReadOnlyList<string> ResolveAll(IEnumerable<string> fulls) =>
        [.. fulls.SelectMany(Resolve).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// True for a file under a <c>bin</c> or <c>obj</c> folder of the repository that stands for no
    /// tracked file: a build wrote it, or it was looked for (docs/design/runtime-file-reads.md).
    /// </summary>
    /// <param name="full">Full path with '/' separators.</param>
    public bool IsUntrackedBuildOutput(string full)
    {
        var path = Paths.ResolveLinks(full);
        if (!Paths.IsUnder(path, _root.Value))
        {
            return false;
        }

        var relative = Path.GetRelativePath(_root.Value, path).Replace('\\', '/');
        return !relative.StartsWith("../", StringComparison.Ordinal)
               && relative.Split('/').Any(static s => s is "bin" or "obj")
               && Resolve(full).Count == 0;
    }

    /// <summary>See <see cref="Resolve"/>.</summary>
    /// <param name="full">Full path.</param>
    private List<string> Compute(string full)
    {
        // The test may reach the repository through a link (macOS's /var is /private/var).
        var directory = full.EndsWith('/');
        var path = Paths.ResolveLinks(directory ? full.TrimEnd('/') : full);
        if (!Paths.IsUnder(path, _root.Value))
        {
            return [];
        }

        var relative = Path.GetRelativePath(_root.Value, path).Replace('\\', '/');
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || relative.StartsWith(".testdetta/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal))
        {
            return [];
        }

        var segments = relative.Split('/');
        if (!segments.Any(static s => s is "bin" or "obj"))
        {
            return [directory ? relative + "/" : relative];
        }

        if (!directory)
        {
            return Sources(path);
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(path).Take(ListedFilesLimit).SelectMany(Sources).ToList();
        return [.. files, .. files.Select(static f => f.Contains('/') ? f[..(f.LastIndexOf('/') + 1)] : string.Empty).Where(static d => d.Length > 0)];
    }

    /// <summary>The tracked files a file in a build output was copied from.</summary>
    /// <param name="copy">The copy's full path.</param>
    private List<string> Sources(string copy)
    {
        // A copy that is not there was looked for, not read: a source added for it changes its project.
        if (!File.Exists(copy))
        {
            return [];
        }

        var candidates = _byName.Value[Path.GetFileName(copy)].ToList();
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var hash = Hash(copy);
        var same = candidates.FindAll(c => _hashes.TryGetValue(c, out var known) ? known is not null && hash is not null && known.AsSpan().SequenceEqual(hash) : Matches(c, hash));
        return same.Count > 0 ? same : candidates;
    }

    /// <summary>True when the tracked file <paramref name="candidate"/> has the content hash <paramref name="hash"/>; remembers its hash.</summary>
    /// <param name="candidate">Tracked file, relative.</param>
    /// <param name="hash">Hash of the copy.</param>
    private bool Matches(string candidate, byte[]? hash)
    {
        var own = Hash(Path.Combine(repoRoot, candidate));
        _hashes[candidate] = own;
        return own is not null && hash is not null && own.AsSpan().SequenceEqual(hash);
    }

    /// <summary>SHA-256 of a file's content; null when it cannot be read.</summary>
    /// <param name="file">Full path.</param>
    private static byte[]? Hash(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return SHA256.HashData(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
