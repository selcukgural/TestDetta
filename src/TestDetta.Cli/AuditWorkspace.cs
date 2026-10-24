using System.Diagnostics;
using System.Text.Json;
using TestDetta.Core;
using TestDetta.Mutants;

namespace TestDetta.Cli;

/// <summary>
/// The work folder of one <c>td audit</c> (docs/design/audit.md): one copy of the working tree per worker, where every
/// mutant is written in, built and tested, plus backups, reports and logs. The team's own tree is only read. The
/// folder is readable by its owner only (the copy holds whatever the tree holds, ignored secret files included) and
/// carries an owner file, held locked while the audit runs; a later audit on the same machine deletes a folder whose
/// owner no longer runs (a crash, a <c>kill -9</c>), and touches nothing without that file, nothing another machine
/// or container made, and nothing whose owner file is still locked. Disposing deletes the folder.
/// </summary>
internal sealed class AuditWorkspace : IDisposable
{
    /// <summary>Prefix of every work folder's name, for the stale-folder sweep.</summary>
    public const string Prefix = "td-audit-";

    /// <summary>Name of the owner file in a work folder.</summary>
    public const string OwnerFile = "owner.json";

    /// <summary>
    /// Free space kept beyond what a copy needs, and the least free space with which the next change still starts: a
    /// full disk would fail builds and test runs in ways that look like a change's doing.
    /// </summary>
    internal const long Margin = 1L << 30;

    /// <summary>Free bytes on the disk of a path, or null when that cannot be told; tests replace it.</summary>
    internal static Func<string, long?> FreeSpace { get; set; } = static path =>
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    };

    /// <summary>The owner file, open and locked for as long as the audit runs, so no other audit's sweep deletes the folder.</summary>
    private FileStream? _owner;

    /// <summary>Creates the folder, readable by its owner only, and its owner file, kept locked.</summary>
    /// <param name="root">The team's repository root, as git names it.</param>
    /// <param name="folder">The new work folder.</param>
    private AuditWorkspace(string root, string folder)
    {
        Root = root;
        Folder = folder;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // FileShare.None is an exclusive lock on Unix too (flock), which a sweep in another process cannot take.
        _owner = new FileStream(Path.Combine(folder, OwnerFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var json = new Utf8JsonWriter(_owner))
        {
            json.WriteStartObject();
            json.WriteNumber("pid", Environment.ProcessId);
            json.WriteString("host", Environment.MachineName);
            json.WriteString("repository", root);
            json.WriteString("started", DateTimeOffset.UtcNow);
            json.WriteEndObject();
        }

        _owner.Flush();
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
    /// <exception cref="IOException">The disk has no room for the copy, or the copy failed.</exception>
    public string AddCopy(string source)
    {
        CheckSpace(source, Folder);
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

    /// <summary>
    /// Why the next change should not start for lack of disk space, or null: less than <see cref="Margin"/> free where
    /// the work folder lies. Checked between changes, since builds and test runs fill the disk as the audit goes.
    /// </summary>
    public string? LowSpace() =>
        FreeSpace(Folder) is { } free && free < Margin
            ? $"the disk of the work folder has {free / (1L << 20)} MB free, less than the 1 GB the audit keeps; free some space or give --work-dir on a larger disk"
            : null;

    /// <summary>
    /// Replaces <paramref name="file"/> with a copy of its own content that shares nothing with any other file: a
    /// symbolic link is replaced by the file it points to, a hard link by a new file. The mutant is written into the
    /// file in place, so through a link it would land in the link's target, possibly in the team's build output
    /// (<c>CreateSymbolicLinksForCopyLocalIfPossible</c>, <c>cp</c> keeping links), or in another file of the copy whose
    /// backup would then hold the mutant. A copy-on-write clone where the file system has one.
    /// </summary>
    /// <param name="file">An assembly or symbol file of a working copy that will be rewritten.</param>
    /// <exception cref="IOException">It could not be replaced.</exception>
    public static void Detach(string file)
    {
        var source = new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file;
        var temporary = file + ".td-detach";
        File.Copy(source, temporary, overwrite: true);
        File.Delete(file);
        File.Move(temporary, file);
    }

    /// <summary>
    /// Replaces every file link under <paramref name="folder"/> with a real file (<see cref="Detach"/>), before a build
    /// writes there: a build copies and writes files in place, and the copy keeps the links of the team's tree, which
    /// may point into the team's own build output. A folder link that leads outside the copy cannot be made safe and
    /// stops the audit. Folder links inside the copy are followed by the checks of the paths themselves.
    /// </summary>
    /// <param name="folder">A folder of a copy the build or a run writes; nothing happens when it does not exist.</param>
    /// <param name="copy">Root of the copy, as a real path.</param>
    /// <exception cref="IOException">A folder link leads outside the copy, or a link could not be replaced.</exception>
    public static void MaterializeLinks(string folder, string copy)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var pending = new Stack<string>([folder]);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory)
                {
                    if (directory.LinkTarget is null)
                    {
                        pending.Push(directory.FullName);
                    }
                    else if (!Paths.IsUnder(Paths.ResolveLinks(directory.FullName), Paths.Normalize(copy)))
                    {
                        throw new IOException($"{directory.FullName} is a link to {Paths.ResolveLinks(directory.FullName)}, outside the working copy; a build there would write your own files");
                    }

                    continue;
                }

                if (entry.LinkTarget is not null && File.Exists(entry.FullName))
                {
                    Detach(entry.FullName);
                }
            }
        }
    }

    /// <summary>Deletes the work folder; one that cannot be deleted is kept in <see cref="LeftBehind"/> for the report.</summary>
    public void Dispose()
    {
        _owner?.Dispose();
        _owner = null;
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
    /// Deletes every work folder under <paramref name="parent"/> whose owner file names a process of this machine that
    /// is no longer running and is not locked. A folder without an owner file, with one that cannot be read (a running
    /// audit holds it locked), one made on another machine or in a container (another host name: its process ids mean
    /// nothing here, as with a temp folder shared into a container), is not ours to delete.
    /// </summary>
    /// <param name="parent">Where work folders go.</param>
    /// <returns>The folders deleted.</returns>
    internal static List<string> RemoveStale(string parent)
    {
        var removed = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(parent, Prefix + "*"))
        {
            if (OwnerOf(folder) is not { } owner || owner.Host != Environment.MachineName || Running(owner.Pid) || Locked(Path.Combine(folder, OwnerFile)))
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

    /// <summary>The process id and host in a work folder's owner file, or null when it has none that can be read.</summary>
    /// <param name="folder">A work folder.</param>
    private static (int Pid, string? Host)? OwnerOf(string folder)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, OwnerFile)));
            var root = document.RootElement;
            return root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var id)
                ? (id, root.TryGetProperty("host", out var host) ? host.GetString() : null)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when another process holds the owner file locked: its audit still runs.</summary>
    /// <param name="ownerFile">A work folder's owner file.</param>
    private static bool Locked(string ownerFile)
    {
        try
        {
            using var stream = new FileStream(ownerFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
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
    /// Refuses to copy onto a disk without room, before every copy. Without copy-on-write a copy costs the tree's size;
    /// on APFS, Btrfs and XFS it is a clone that costs almost nothing until files change, but the baseline's build
    /// rewrites the build output, so the size of the <c>bin</c> and <c>obj</c> folders is kept free there. Either way
    /// with <see cref="Margin"/> to spare.
    /// </summary>
    /// <param name="source">Tree to copy.</param>
    /// <param name="parent">Where the copy goes.</param>
    /// <exception cref="IOException">Not enough free space.</exception>
    internal static void CheckSpace(string source, string parent)
    {
        if (FreeSpace(parent) is not { } free)
        {
            return;
        }

        var cloned = CopyOnWrite(parent);
        var needed = Size(source, buildOutputOnly: cloned);
        if (SpaceProblem(free, needed, cloned) is { } problem)
        {
            throw new IOException($"{parent}: {problem}");
        }
    }

    /// <summary>Why <paramref name="free"/> bytes are not enough for a copy that needs <paramref name="needed"/>, or null.</summary>
    /// <param name="free">Free bytes on the disk.</param>
    /// <param name="needed">Bytes the copy will write: the tree, or only its build output for a clone.</param>
    /// <param name="cloned">True on a file system with copy-on-write.</param>
    internal static string? SpaceProblem(long free, long needed, bool cloned) =>
        free >= needed + Margin
            ? null
            : $"{free / (1L << 20)} MB free; the working copy needs about {needed / (1L << 20)} MB " +
              (cloned ? "(a clone, but the build rewrites its bin and obj folders) " : string.Empty) +
              "and 1 GB to spare. Free some space or give --work-dir on a larger disk.";

    /// <summary>True when the file system of <paramref name="path"/> clones files (APFS, Btrfs, XFS).</summary>
    /// <param name="path">A folder on it.</param>
    private static bool CopyOnWrite(string path)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(path)).DriveFormat.ToLowerInvariant() is "apfs" or "btrfs" or "xfs";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The bytes of a tree's files, links not followed; with <paramref name="buildOutputOnly"/> only those under <c>bin</c> and <c>obj</c> folders.</summary>
    /// <param name="root">The tree.</param>
    /// <param name="buildOutputOnly">True to count only build output.</param>
    private static long Size(string root, bool buildOutputOnly)
    {
        var size = 0L;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            if (buildOutputOnly && !InBuildOutput(Path.GetRelativePath(root, file.FullName)))
            {
                continue;
            }

            size += file.Length;
        }

        return size;
    }

    /// <summary>True when a relative path runs through a <c>bin</c> or <c>obj</c> folder.</summary>
    /// <param name="relative">Path relative to a tree's root.</param>
    private static bool InBuildOutput(string relative)
    {
        foreach (var part in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
