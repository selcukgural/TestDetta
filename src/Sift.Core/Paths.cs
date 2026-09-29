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
