namespace Sift.Core;

internal static class Paths
{
    public static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or lies beneath it.</summary>
    public static bool IsUnder(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(directory, comparison) || path.StartsWith(directory + "/", comparison);
    }
}
