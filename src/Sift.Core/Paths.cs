namespace Sift.Core;

/// <summary>
/// Path helpers. Every path Sift compares is absolute and uses '/' as the separator, so the same
/// comparison code works on Windows and Unix.
/// </summary>
internal static class Paths
{
    /// <summary>Comparison used for paths: case-insensitive on Windows, ordinal elsewhere.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Comparer matching <see cref="Comparison"/>, for dictionaries and sets keyed by path.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Returns the absolute form of <paramref name="path"/> with '/' separators and no trailing slash.</summary>
    /// <param name="path">A relative or absolute path. Relative paths resolve against the current directory.</param>
    public static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// <paramref name="path"/>, normalized, with every symbolic link along it replaced by its target.
    /// Compilers write resolved paths into PDBs (macOS's <c>/var</c> is a link to <c>/private/var</c>),
    /// so a path the user gave and one a tool wrote may differ only by links. Parts that do not exist
    /// or cannot be resolved are kept as they are.
    /// </summary>
    /// <param name="path">A relative or absolute path.</param>
    public static string ResolveLinks(string path) => ResolveLinks(path, depth: 0);

    /// <summary>See <see cref="ResolveLinks(string)"/>; a link's target may hold links of its own.</summary>
    /// <param name="path">A relative or absolute path.</param>
    /// <param name="depth">Links followed so far; a cycle stops at the limit Unix uses for one path (40).</param>
    private static string ResolveLinks(string path, int depth)
    {
        var normalized = Normalize(path);
        var root = Path.GetPathRoot(normalized) ?? "";
        var resolved = root;
        foreach (var part in normalized[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(resolved, part);
            try
            {
                var info = new DirectoryInfo(next);
                resolved = depth < 40 && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                    ? ResolveLinks(target.FullName, depth + 1)
                    : next;
            }
            catch (IOException)
            {
                resolved = next;
            }
        }

        return Normalize(resolved);
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="directory"/> or lies beneath it.</summary>
    /// <param name="path">Normalized file or directory path.</param>
    /// <param name="directory">Normalized directory path.</param>
    /// <remarks>
    /// Checks the separator after the prefix instead of concatenating <c>directory + "/"</c>,
    /// because this runs once per changed file per project.
    /// </remarks>
    public static bool IsUnder(string path, string directory)
    {
        if (!path.StartsWith(directory, Comparison))
        {
            return false;
        }

        return path.Length == directory.Length || path[directory.Length] == '/';
    }
}
