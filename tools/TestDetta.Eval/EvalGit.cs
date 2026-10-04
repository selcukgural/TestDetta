using System.Diagnostics;

namespace TestDetta.Eval;

/// <summary>One commit of the evaluated history.</summary>
/// <param name="Sha">Full commit hash.</param>
/// <param name="Parent">Full hash of its first parent, the base the change is compared with.</param>
/// <param name="Subject">First line of the commit message.</param>
internal sealed record EvalCommit(string Sha, string Parent, string Subject);

/// <summary>The git commands the evaluation needs, run in its own clone.</summary>
/// <param name="root">Clone directory.</param>
internal sealed class EvalGit(string root)
{
    /// <summary>Clones <paramref name="repository"/> into <paramref name="target"/>.</summary>
    /// <param name="repository">Path or URL.</param>
    /// <param name="target">Clone directory; must not exist.</param>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    public static EvalGit Clone(string repository, string target)
    {
        Run(Path.GetDirectoryName(target)!, "clone", "--quiet", "--no-hardlinks", repository, target);
        return new EvalGit(target);
    }

    /// <summary>The latest <paramref name="count"/> first-parent commits of <paramref name="branch"/>, oldest first.</summary>
    /// <param name="branch">Branch name, local or <c>origin/</c>.</param>
    /// <param name="count">Number of commits.</param>
    public IReadOnlyList<EvalCommit> History(string branch, int count)
    {
        var reference = Reference(branch);
        var lines = Run(root, "log", "--first-parent", $"-{count}", "--format=%H%x09%P%x09%s", reference);

        var commits = new List<EvalCommit>(lines.Count);
        foreach (var line in lines)
        {
            var parts = line.Split('\t', 3);
            var parents = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length == 0)
            {
                continue;
            }

            commits.Add(new EvalCommit(parts[0], parents[0], parts.Length > 2 ? parts[2] : string.Empty));
        }

        commits.Reverse();
        return commits;
    }

    /// <summary>The commit at the tip of <paramref name="branch"/>, which may have no parent.</summary>
    /// <param name="branch">Branch name, local or <c>origin/</c>.</param>
    public string Head(string branch) => Run(root, "rev-parse", Reference(branch) + "^{commit}")[0];

    /// <summary>The commit <paramref name="count"/> first parents before <paramref name="sha"/>.</summary>
    /// <param name="sha">Starting commit.</param>
    /// <param name="count">First-parent steps back.</param>
    /// <exception cref="InvalidOperationException">The history is shorter.</exception>
    public string FirstParentAncestor(string sha, int count) => Run(root, "rev-parse", $"{sha}~{count}^{{commit}}")[0];

    /// <summary>The branch itself when it exists locally, otherwise its <c>origin/</c> counterpart.</summary>
    /// <param name="branch">Branch name.</param>
    private string Reference(string branch) =>
        Run(root, "rev-parse", "--verify", "--quiet", branch).Count > 0 ? branch : $"origin/{branch}";

    /// <summary>Checks out <paramref name="sha"/> (detached), discarding changes to tracked files.</summary>
    /// <param name="sha">Commit to check out.</param>
    public void Checkout(string sha) => Run(root, "checkout", "--quiet", "--force", "--detach", sha);

    /// <summary>Untracked files git does not ignore, relative to the root with '/' separators.</summary>
    public HashSet<string> Untracked() =>
        [.. Run(root, "-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard")];

    /// <summary>
    /// Deletes the untracked, not ignored files that are not in <paramref name="keep"/>: what test runs
    /// wrote into the clone (protobuf-net's <c>BuildToolsUnitTests</c> writes
    /// <c>Aot/Data/Extensible.output.txt</c>). Left there, the next analysis sees a changed non-code
    /// file and selects that project whole, which is a finding about the evaluation, not about TestDetta.
    /// </summary>
    /// <param name="keep">Untracked files from before the runs, such as what a setup command generated.</param>
    /// <returns>The deleted files.</returns>
    public List<string> RemoveUntracked(IReadOnlySet<string> keep)
    {
        var removed = new List<string>();
        foreach (var file in Untracked())
        {
            if (keep.Contains(file))
            {
                continue;
            }

            File.Delete(Path.Combine(root, file));
            removed.Add(file);
        }

        return removed;
    }

    /// <summary>Number of files that differ between two commits.</summary>
    /// <param name="from">Older commit.</param>
    /// <param name="to">Newer commit.</param>
    public int ChangedFileCount(string from, string to) => Run(root, "diff", "--name-only", from, to).Count;

    /// <summary>Runs git and returns its non-empty output lines.</summary>
    /// <param name="workingDirectory">Directory git runs in.</param>
    /// <param name="args">git arguments.</param>
    /// <exception cref="InvalidOperationException">git exited with a non-zero code, except for <c>rev-parse --quiet</c>.</exception>
    private static List<string> Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stderr = process.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        process.WaitForExit();
        var quietProbe = args is ["rev-parse", "--verify", "--quiet", ..];
        if (process.ExitCode != 0 && !quietProbe)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.GetAwaiter().GetResult().Trim()}");
        }

        return lines;
    }
}
