namespace Sift.Core;

public enum ImpactReason
{
    /// <summary>A file inside the project's directory changed.</summary>
    DirectChange,
    /// <summary>A project this one references (transitively) was affected.</summary>
    Dependency,
    /// <summary>A build-wide file such as Directory.Build.props changed above the project.</summary>
    BuildConfiguration,
}

public sealed record AffectedProject(ProjectNode Project, ImpactReason Reason);

public sealed record ImpactResult(
    IReadOnlyList<AffectedProject> Affected,
    IReadOnlyList<string> UnmappedFiles)
{
    public IEnumerable<AffectedProject> Tests => Affected.Where(a => a.Project.IsTestProject);
}

/// <summary>
/// Maps changed files to the projects they can affect. Errs on the side of selecting too much:
/// a missed test is a bug, an extra test is only slower CI.
/// </summary>
public static class ImpactAnalyzer
{
    // Files that MSBuild imports implicitly into every project beneath their directory.
    private static readonly string[] DirectoryWideFiles =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "Directory.Solution.props", "Directory.Solution.targets",
        "global.json", "NuGet.config", ".editorconfig", "Directory.Build.rsp",
    ];

    public static ImpactResult Analyze(ProjectGraph graph, string repoRoot, IEnumerable<string> changedFiles)
    {
        var root = Paths.Normalize(repoRoot);
        var reasons = new Dictionary<string, ImpactReason>(ProjectGraph.PathComparer);
        var unmapped = new List<string>();

        // Deepest directory first, so nested projects win over their parents.
        var byDepth = graph.Projects.OrderByDescending(p => p.Directory.Length).ToList();

        foreach (var relative in changedFiles)
        {
            var file = Paths.Normalize(Path.Combine(root, relative));
            var fileName = Path.GetFileName(file);

            if (DirectoryWideFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                var dir = Paths.Normalize(Path.GetDirectoryName(file)!);
                foreach (var project in graph.Projects.Where(p => Paths.IsUnder(p.Directory, dir)))
                    Mark(project.Path, ImpactReason.BuildConfiguration);
                continue;
            }

            var owner = byDepth.FirstOrDefault(p => Paths.IsUnder(file, p.Directory));
            if (owner is null)
                unmapped.Add(relative);
            else
                Mark(owner.Path, ImpactReason.DirectChange);
        }

        // Propagate to everything that references an affected project.
        var queue = new Queue<string>(reasons.Keys);
        while (queue.Count > 0)
        {
            foreach (var dependent in graph.DependentsOf(queue.Dequeue()))
            {
                if (reasons.ContainsKey(dependent)) continue;
                reasons[dependent] = ImpactReason.Dependency;
                queue.Enqueue(dependent);
            }
        }

        var affected = reasons
            .Select(kv => new AffectedProject(graph[kv.Key], kv.Value))
            .OrderBy(a => a.Project.Path, ProjectGraph.PathComparer)
            .ToList();

        return new ImpactResult(affected, unmapped);

        void Mark(string path, ImpactReason reason)
        {
            // A direct or build-config reason is more informative than one inherited from a dependency.
            if (!reasons.ContainsKey(path)) reasons[path] = reason;
        }
    }
}
