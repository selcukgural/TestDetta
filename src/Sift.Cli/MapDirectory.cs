using Microsoft.Extensions.Logging;
using Sift.Core;

namespace Sift.Cli;

/// <summary>
/// Coverage maps kept outside the repository, one folder per commit (<c>&lt;dir&gt;/&lt;sha&gt;/</c>), for CI
/// systems without GitHub's cache listing (docs/ci.md). <c>sift record --maps-dir</c> saves the maps it
/// recorded under HEAD; <c>sift test --maps-dir</c> restores the newest folder on the merge base's
/// first-parent history, within <c>--max-map-age</c>, into <c>.sift/coverage</c>. The customer syncs the
/// folder with whatever the CI offers: artifacts, a cache, a shared disk, object storage. No network here.
/// </summary>
internal static partial class MapDirectory
{
    /// <summary>
    /// Restores the maps of the nearest commit at or before the merge base that has a folder, replacing
    /// the maps in <c>.sift/coverage</c>. Returns that commit, or null when none is within reach.
    /// </summary>
    /// <param name="mapsDir">The folder of per-commit maps.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="baseRef">The ref the changes are compared with.</param>
    /// <param name="maxMapAge">How many first-parent commits before the merge base a map may be.</param>
    /// <param name="git">Runs git.</param>
    /// <param name="logger">Receives what was found.</param>
    public static string? Restore(string mapsDir, string repoRoot, string baseRef, int maxMapAge, GitClient git, ILogger logger)
    {
        if (!Directory.Exists(mapsDir))
        {
            LogMissing(logger, mapsDir);
            return null;
        }

        var mergeBase = git.MergeBase(repoRoot, baseRef);
        var ancestors = git.FirstParentAncestors(repoRoot, mergeBase, maxMapAge + 1);
        for (var age = 0; age < ancestors.Count; age++)
        {
            var folder = Path.Combine(mapsDir, ancestors[age]);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var target = Path.Combine(repoRoot, CoverageMapStore.Directory);
            Directory.CreateDirectory(target);
            // The maps of one commit form a set; mixing in older local ones would mislead selection.
            foreach (var stale in Directory.EnumerateFiles(target, "*.json"))
            {
                File.Delete(stale);
            }

            foreach (var map in Directory.EnumerateFiles(folder, "*.json"))
            {
                File.Copy(map, Path.Combine(target, Path.GetFileName(map)), overwrite: true);
            }

            LogRestored(logger, ancestors[age], age);
            return ancestors[age];
        }

        LogNone(logger, maxMapAge, mapsDir);
        return null;
    }

    /// <summary>
    /// Saves <paramref name="maps"/> as the folder of <paramref name="head"/>: written beside it and moved
    /// into place, so a reader never sees half a set. An existing folder for the commit is replaced.
    /// </summary>
    /// <param name="mapsDir">The folder of per-commit maps.</param>
    /// <param name="head">The commit the maps were recorded at.</param>
    /// <param name="maps">Paths of the map files recorded.</param>
    /// <param name="logger">Receives where they went.</param>
    public static void Save(string mapsDir, string head, IReadOnlyList<string> maps, ILogger logger)
    {
        Directory.CreateDirectory(mapsDir);
        var temporary = Path.Combine(mapsDir, $".{head}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporary);
        foreach (var map in maps)
        {
            File.Copy(map, Path.Combine(temporary, Path.GetFileName(map)));
        }

        var folder = Path.Combine(mapsDir, head);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.Move(temporary, folder);
        LogSaved(logger, maps.Count, folder);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "--maps-dir {Directory} does not exist; selecting without coverage maps")]
    private static partial void LogMissing(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Using the coverage maps of {Commit}, {Age} first-parent commit(s) before the merge base")]
    private static partial void LogRestored(ILogger logger, string commit, int age);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No coverage maps within {MaxAge} commits before the merge base in {Directory}; selecting by name")]
    private static partial void LogNone(ILogger logger, int maxAge, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved {Count} coverage map(s) to {Folder}")]
    private static partial void LogSaved(ILogger logger, int count, string folder);
}
