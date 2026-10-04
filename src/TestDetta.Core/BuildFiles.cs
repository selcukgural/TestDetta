using System.Collections.Frozen;

namespace TestDetta.Core;

/// <summary>Build files MSBuild and the SDK pick up by name rather than through an import.</summary>
internal static class BuildFiles
{
    /// <summary>
    /// Files that apply implicitly to every project beneath their directory, so a change affects all
    /// of those projects (see <see cref="ImpactAnalyzer"/>).
    /// </summary>
    private static readonly FrozenSet<string> DirectoryWide = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp",
        "Directory.Packages.props", "Directory.Solution.props", "Directory.Solution.targets",
        "global.json", "NuGet.config", ".editorconfig");

    /// <summary>True when <paramref name="path"/> names one of the directory-wide build files.</summary>
    /// <param name="path">File path.</param>
    public static bool IsDirectoryWide(string path) => DirectoryWide.Contains(Path.GetFileName(path));
}
