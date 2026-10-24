using System.Diagnostics;
using TestDetta.Core;

namespace TestDetta.Mutants;

/// <summary>
/// Copies and deletes whole directory trees for mutants that must not touch the original: the evaluator's worker
/// copies and <c>td audit</c>'s working copy of a team's repository (docs/design/audit.md).
/// </summary>
internal static class TreeCopy
{
    /// <summary>Longest a copy may take; a full copy of a large clone on a disk without copy-on-write takes minutes.</summary>
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Copies a directory tree with its symbolic links as links: copy-on-write clones where the file
    /// system has them (APFS through <c>cp -c</c>, Btrfs and XFS through <c>--reflink=auto</c>), which
    /// cost almost no time or space, and a plain copy otherwise.
    /// </summary>
    /// <param name="source">Directory to copy.</param>
    /// <param name="destination">New directory; must not exist.</param>
    public static void Copy(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            CopyTree(new DirectoryInfo(source), destination);
            return;
        }

        List<string> arguments = OperatingSystem.IsMacOS() ? ["-cpR", source, destination] : ["-a", "--reflink=auto", source, destination];
        var psi = new ProcessStartInfo("cp") { RedirectStandardError = true };
        arguments.ForEach(psi.ArgumentList.Add);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("cp could not be started.");
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CopyTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException($"Copying the clone to {destination} did not finish within {CopyTimeout.TotalMinutes:0} minutes.");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"Copying the clone to {destination} failed: {error.Result.Trim()}");
        }
    }

    /// <summary>Copies a directory tree file by file, recreating symbolic links instead of following them.</summary>
    /// <param name="source">Directory to copy.</param>
    /// <param name="destination">New directory.</param>
    private static void CopyTree(DirectoryInfo source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(target, link);
                }
                else
                {
                    File.CreateSymbolicLink(target, link);
                }
            }
            else if (entry is DirectoryInfo directory)
            {
                CopyTree(directory, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    /// <summary>Deletes a copy, read-only files included (git objects are read-only); a missing copy is left alone.</summary>
    /// <param name="copy">The copy.</param>
    public static void Remove(string copy)
    {
        if (!Directory.Exists(copy))
        {
            return;
        }

        Paths.DeleteTree(copy);
    }
}
