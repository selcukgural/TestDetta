using System.Diagnostics;
using System.Text.Json;
using TestDetta.Core;
using TestDetta.Mutants;

namespace TestDetta.Cli;

/// <summary>
/// The work folder of one <c>td audit</c> (docs/design/audit.md): one copy of the working tree per worker, where every
/// mutant is written in, built and tested, plus backups, reports and logs. The team's own tree is only read. The
/// folder carries an owner file; a later audit deletes a folder whose owner no longer runs (a crash, a
/// <c>kill -9</c>), and touches nothing without that file. Disposing deletes the folder.
/// </summary>
internal sealed class AuditWorkspace : IDisposable
{
    /// <summary>Prefix of every work folder's name, for the stale-folder sweep.</summary>
    public const string Prefix = "td-audit-";

    /// <summary>Name of the owner file in a work folder.</summary>
    public const string OwnerFile = "owner.json";

    /// <summary>Free space kept beyond the copy's size on a file system without copy-on-write.</summary>
    private const long Margin = 1L << 30;

    /// <summary>Creates the folder and its owner file.</summary>
    /// <param name="root">The team's repository root, as git names it.</param>
    /// <param name="folder">The new work folder.</param>
    private AuditWorkspace(string root, string folder)
    {
        Root = root;
        Folder = folder;
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, OwnerFile));
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteNumber("pid", Environment.ProcessId);
        json.WriteString("repository", root);
        json.WriteString("started", DateTimeOffset.UtcNow);
        json.WriteEndObject();
    }

    /// <summary>The team's repository root.</summary>
    public string Root { get; }

    /// <summary>The work folder.</summary>
    public string Folder { get; }

    /// <summary>The working copy of each worker, in worker order.</summary>
    public List<string> Copies { get; } = [];

    /// <summary>A folder that could not be deleted on disposal, or null; the next audit removes it.</summary>
    public string? LeftBehind { get; private set; }

    /// <summary>
    /// The default parent of work folders: <c>/tmp</c> on Unix, whose short path keeps the Unix domain sockets of
    /// MSBuild and the test platform under the 104-byte limit of macOS, the temp folder on Windows.
    /// </summary>
    public static string DefaultParent => OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";

    /// <summary>
    /// Deletes the stale work folders under <paramref name="parent"/>, then makes a new one with the first working
    /// copy of <paramref name="root"/>, after checking the disk has room for it.
    /// </summary>
    /// <param name="root">The team's repository root.</param>
    /// <param name="parent">Where work folders go.</param>
    /// <exception cref="IOException">The work folder would lie inside the repository, the disk has no room for the copy, or the copy failed.</exception>
    public static AuditWorkspace Create(string root, string parent)
    {
        // MSBuild and git see the real path (macOS: /tmp is /private/tmp); the copy is checked against what they report.
        parent = RealPath(Path.GetFullPath(parent));
        if (Paths.IsUnder(Paths.Normalize(parent), Paths.Normalize(root)))
        {
            // The copy would contain itself, and the team's tree would hold the mutants.
            throw new IOException($"The work folder {parent} lies inside the repository; give --work-dir outside it.");
        }

        Directory.CreateDirectory(parent);
        RemoveStale(parent);
        CheckSpace(root, parent);
        var workspace = new AuditWorkspace(root, Path.Combine(Path.GetFullPath(parent), $"{Prefix}{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}"));
        try
        {
            workspace.AddCopy(root);
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Adds a working copy for another worker, copied from <paramref name="source"/> (the first copy, built by the
    /// baseline, so every worker has the same binaries).
    /// </summary>
    /// <param name="source">Tree to copy.</param>
    /// <returns>The new copy's root.</returns>
    public string AddCopy(string source)
    {
        var worker = Path.Combine(Folder, $"w{Copies.Count + 1}");
        Directory.CreateDirectory(worker);
        var copy = Path.Combine(worker, Path.GetFileName(Root.TrimEnd('/', '\\')));
        TreeCopy.Copy(source, copy);
        Copies.Add(Paths.Normalize(copy));
        return Copies[^1];
    }

    /// <summary>A path of the team's tree as it lies in a working copy.</summary>
    /// <param name="copy">Root of the copy.</param>
    /// <param name="path">Full path in the team's tree.</param>
    public string InCopy(string copy, string path) => Paths.Normalize(Path.Combine(copy, Path.GetRelativePath(Root, path)));

    /// <summary>A folder of the work folder for one purpose (backups, reports, a worker's temporary files), created.</summary>
    /// <param name="name">Its name.</param>
    public string Scratch(string name)
    {
        var path = Path.Combine(Folder, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes the work folder; one that cannot be deleted is kept in <see cref="LeftBehind"/> for the report.</summary>
    public void Dispose()
    {
        try
        {
            TreeCopy.Remove(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LeftBehind = Folder;
        }
    }

    /// <summary>
    /// Deletes every work folder under <paramref name="parent"/> whose owner file names a process that is no longer
    /// running. A folder without an owner file, or with one that cannot be read, is not ours to delete.
    /// </summary>
    /// <param name="parent">Where work folders go.</param>
    /// <returns>The folders deleted.</returns>
    internal static List<string> RemoveStale(string parent)
    {
        var removed = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(parent, Prefix + "*"))
        {
            if (OwnerOf(folder) is not { } pid || Running(pid))
            {
                continue;
            }

            try
            {
                TreeCopy.Remove(folder);
                removed.Add(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held (another user's files, a process of the old run): left for a later sweep.
            }
        }

        return removed;
    }

    /// <summary>The path with every symbolic link on the way resolved, as the operating system reports a working directory.</summary>
    /// <param name="path">A full path of an existing directory.</param>
    internal static string RealPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            var info = new DirectoryInfo(next);
            current = info.LinkTarget is { } target ? RealPath(Path.GetFullPath(target, current)) : next;
        }

        return current;
    }

    /// <summary>The process id in a work folder's owner file, or null when it has none that can be read.</summary>
    /// <param name="folder">A work folder.</param>
    private static int? OwnerOf(string folder)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, OwnerFile)));
            return document.RootElement.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var id) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>True when a process with this id runs (possibly another one that reuses the id: then the folder waits).</summary>
    /// <param name="pid">Process id.</param>
    private static bool Running(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refuses to copy onto a disk without room for the tree. On APFS and Btrfs the copy is a clone that costs almost
    /// nothing until files change; elsewhere it costs the tree's size, kept with a margin.
    /// </summary>
    /// <param name="root">Tree to copy.</param>
    /// <param name="parent">Where the copy goes.</param>
    /// <exception cref="IOException">Not enough free space.</exception>
    private static void CheckSpace(string root, string parent)
    {
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(Path.GetFullPath(parent));
            if (drive.DriveFormat.ToLowerInvariant() is "apfs" or "btrfs")
            {
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        var size = 0L;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            size += file.Length;
        }

        if (drive.AvailableFreeSpace < size + Margin)
        {
            throw new IOException(
                $"{parent} has {drive.AvailableFreeSpace / (1L << 20)} MB free; the working copy of the repository needs about {size / (1L << 20)} MB and 1 GB to spare. " +
                "Free some space or give --work-dir on a larger disk.");
        }
    }
}
