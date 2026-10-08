namespace TestDetta.Coverage;

/// <summary>
/// The files in a test project's build output before a <c>--no-build</c> run, so the files the run leaves there can be
/// removed after it. Tests write into their output folder (it is the working directory and the app base), and a test
/// host that dies skips the cleanup in their <c>finally</c> blocks: NLog's
/// <c>LogFactoryTests.SecondaryLogFactoryDoesNotTakePrimaryLogFactoryLock</c> writes <c>NLog.config</c> there, a mutant
/// crashed the host before the file was deleted, and every later run of other classes auto-loaded that file and
/// failed with the mutant, which made a miss out of a polluted folder (final round, 2026-10-08). A run without a
/// build creates nothing there that a later run needs, so every new file goes. Changed files are left as they are:
/// their old content is gone, and the run that changed them is the one that shows it.
/// </summary>
internal sealed class OutputLeftovers
{
    /// <summary>Full paths of the files that were there before the run.</summary>
    private readonly HashSet<string> _before;

    /// <summary>Full paths of the folders under the output folder that were there before the run.</summary>
    private readonly HashSet<string> _beforeFolders;

    /// <summary>The output folder.</summary>
    private readonly string _folder;

    /// <summary>Takes the snapshot.</summary>
    /// <param name="folder">The output folder; a missing folder counts as empty.</param>
    private OutputLeftovers(string folder)
    {
        _folder = folder;
        _before = new HashSet<string>(Files(folder), StringComparer.Ordinal);
        _beforeFolders = new HashSet<string>(Folders(folder), StringComparer.Ordinal);
    }

    /// <summary>Lists the files in <paramref name="folder"/> before a run; null when there is no folder to watch.</summary>
    /// <param name="folder">The test project's output folder, or null when it is not known.</param>
    public static OutputLeftovers? Before(string? folder) =>
        folder is { Length: > 0 } && Directory.Exists(folder) ? new OutputLeftovers(folder) : null;

    /// <summary>
    /// Deletes the files that were not there before the run, then the folders that are left empty and were not there
    /// either. A file that cannot be deleted (a test host that still holds it) is kept and named.
    /// </summary>
    /// <returns>Paths relative to the folder of the files removed, and of the ones that could not be.</returns>
    public (List<string> Removed, List<string> Kept) RemoveNew()
    {
        var removed = new List<string>();
        var kept = new List<string>();
        foreach (var file in Files(_folder))
        {
            if (_before.Contains(file))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed.Add(Path.GetRelativePath(_folder, file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                kept.Add(Path.GetRelativePath(_folder, file));
            }
        }

        if (removed.Count > 0)
        {
            RemoveEmptyNewFolders();
        }

        return (removed, kept);
    }

    /// <summary>Removes folders the run created that hold nothing now, deepest first.</summary>
    private void RemoveEmptyNewFolders()
    {
        var folders = Folders(_folder).Where(f => !_beforeFolders.Contains(f)).OrderByDescending(static f => f.Length).ToList();
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder in use stays; its files were reported already.
            }
        }
    }

    /// <summary>Every file under <paramref name="folder"/>, without following links; none when it is gone.</summary>
    /// <param name="folder">The folder.</param>
    private static IEnumerable<string> Files(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", Walk) : [];

    /// <summary>Every folder under <paramref name="folder"/>, without following links; none when it is gone.</summary>
    /// <param name="folder">The folder.</param>
    private static IEnumerable<string> Folders(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, "*", Walk) : [];

    /// <summary>A walk of the whole tree that skips links and what cannot be read.</summary>
    private static readonly EnumerationOptions Walk = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
}
