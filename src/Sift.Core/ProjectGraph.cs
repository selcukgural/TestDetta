namespace Sift.Core;

/// <summary>
/// The projects of a repository and the reverse of their <c>ProjectReference</c> edges, so that
/// "who depends on this project?" is a dictionary lookup. Create it with <see cref="ProjectGraphLoader"/>.
/// </summary>
public sealed class ProjectGraph
{
    private readonly Dictionary<string, ProjectNode> _nodes;
    private readonly Dictionary<string, List<string>> _dependents;

    /// <summary>Builds the graph and its reverse edges from already parsed projects.</summary>
    /// <param name="nodes">Projects keyed by their normalized path.</param>
    internal ProjectGraph(Dictionary<string, ProjectNode> nodes)
    {
        _nodes = nodes;
        _dependents = new Dictionary<string, List<string>>(nodes.Count, Paths.Comparer);

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

    /// <summary>Projects that reference <paramref name="path"/> directly. Empty when nothing does.</summary>
    /// <param name="path">Normalized absolute path of a .csproj file.</param>
    public IReadOnlyList<string> DependentsOf(string path) =>
        _dependents.TryGetValue(path, out var list) ? list : [];
}
