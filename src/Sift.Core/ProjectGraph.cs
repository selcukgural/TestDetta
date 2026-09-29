namespace Sift.Core;

/// <summary>
/// The projects of a repository and the reverse of their <c>ProjectReference</c> edges, so that
/// "who depends on this project?" is a dictionary lookup. Create it with <see cref="ProjectGraphLoader"/>.
/// </summary>
public sealed class ProjectGraph
{
    private readonly Dictionary<string, ProjectNode> _nodes;
    private readonly Dictionary<string, List<string>> _dependents;

    /// <summary>Projects ordered by directory length, deepest first, so nested projects own their files.</summary>
    private readonly ProjectNode[] _byDepth;

    /// <summary>Builds the graph and its reverse edges from already parsed projects.</summary>
    /// <param name="nodes">Projects keyed by their normalized path.</param>
    internal ProjectGraph(Dictionary<string, ProjectNode> nodes)
    {
        _nodes = nodes;
        _dependents = new Dictionary<string, List<string>>(nodes.Count, Paths.Comparer);
        _byDepth = [.. nodes.Values.OrderByDescending(static p => p.Directory.Length)];

        foreach (var node in nodes.Values)
        {
            foreach (var reference in node.References)
            {
                // References to projects outside the repository are not part of the graph.
                if (!nodes.ContainsKey(reference))
                {
                    continue;
                }

                if (!_dependents.TryGetValue(reference, out var list))
                {
                    list = [];
                    _dependents[reference] = list;
                }

                list.Add(node.Path);
            }
        }
    }

    /// <summary>All projects found in the repository.</summary>
    public IReadOnlyCollection<ProjectNode> Projects => _nodes.Values;

    /// <summary>Returns the project at <paramref name="path"/>.</summary>
    /// <param name="path">Normalized absolute path of a .csproj file that is part of the graph.</param>
    /// <exception cref="KeyNotFoundException">The path is not a project of this graph.</exception>
    public ProjectNode this[string path] => _nodes[path];

    /// <summary>
    /// The project whose directory contains <paramref name="file"/>. When projects are nested, the
    /// innermost one wins. Null when the file is outside every project directory.
    /// </summary>
    /// <param name="file">Normalized absolute file path.</param>
    public ProjectNode? FindOwner(string file) => Array.Find(_byDepth, p => Paths.IsUnder(file, p.Directory));

    /// <summary>
    /// <paramref name="projects"/> plus every project that references one of them, directly or
    /// transitively.
    /// </summary>
    /// <param name="projects">Normalized .csproj paths to start from.</param>
    public HashSet<string> WithDependents(IEnumerable<string> projects)
    {
        var result = new HashSet<string>(projects, Paths.Comparer);
        var queue = new Queue<string>(result);

        while (queue.TryDequeue(out var project))
        {
            foreach (var dependent in DependentsOf(project))
            {
                if (result.Add(dependent))
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        return result;
    }

    /// <summary>Projects that reference <paramref name="path"/> directly. Empty when nothing does.</summary>
    /// <param name="path">Normalized absolute path of a .csproj file.</param>
    public IReadOnlyList<string> DependentsOf(string path) =>
        _dependents.TryGetValue(path, out var list) ? list : [];
}
