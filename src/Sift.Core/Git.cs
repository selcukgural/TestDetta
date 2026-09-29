using System.Diagnostics;

namespace Sift.Core;

public static class Git
{
    /// <summary>
    /// Files changed between the merge base of <paramref name="baseRef"/> and HEAD, plus
    /// uncommitted and untracked files. Renames are reported as a delete and an add so both
    /// the old and the new location count as changed.
    /// </summary>
    public static IReadOnlyList<string> ChangedFiles(string repoRoot, string baseRef)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in Run(repoRoot, "diff", "--name-only", "--no-renames", $"{baseRef}...HEAD"))
            files.Add(line);
        foreach (var line in Run(repoRoot, "diff", "--name-only", "--no-renames", "HEAD"))
            files.Add(line);
        foreach (var line in Run(repoRoot, "ls-files", "--others", "--exclude-standard"))
            files.Add(line);
        return files.ToList();
    }

    public static string TopLevel(string path) => Run(path, "rev-parse", "--show-toplevel").Single();

    private static List<string> Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // "-c core.quotepath=off" keeps non-ASCII file names readable instead of octal-escaped.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
            if (line.Length > 0) lines.Add(line);
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderrTask.Result.Trim()}");
        return lines;
    }
}
