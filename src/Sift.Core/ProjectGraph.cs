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

    /// <summary>Per project, its external items and imports as matchers, built once.</summary>
    private readonly List<(ProjectNode Project, PathMatcher Items, PathMatcher Imports)> _inputs;

    /// <summary>Projects whose inputs could not all be resolved.</summary>
    private readonly ProjectNode[] _unresolved;

    /// <summary>
    /// Extensions of files that may be an unresolved project's hidden import or source; other
    /// unclaimed files (documentation, scripts) stay unclaimed.
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenSet<string> BuildOrSourceExtensions =
        System.Collections.Frozen.FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".cs", ".fs", ".vb", ".props", ".targets", ".projitems");

    /// <summary>Builds the graph and its reverse edges from already parsed projects.</summary>
    /// <param name="nodes">Projects keyed by their normalized path.</param>
    internal ProjectGraph(Dictionary<string, ProjectNode> nodes)
    {
        _nodes = nodes;
        _dependents = new Dictionary<string, List<string>>(nodes.Count, Paths.Comparer);
        _byDepth = [.. nodes.Values.OrderByDescending(static p => p.Directory.Length)];
        _inputs = [.. nodes.Values
            .Where(static p => p.ExternalItems.Count > 0 || p.Imports.Count > 0)
            .Select(static p => (p, new PathMatcher(p.ExternalItems), new PathMatcher(p.Imports)))];
        _unresolved = [.. nodes.Values.Where(static p => p.HasUnresolvedInputs)];

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
    /// Every project a changed <paramref name="file"/> can affect directly, and how: the project whose
    /// directory contains it and projects that pull it in from outside their directory are
    /// <see cref="FileRole.Source"/>; projects that import it are <see cref="FileRole.BuildInput"/>.
    /// A build or source file no project claims is a build input of every project with unresolved
    /// inputs, since it may be what those resolve to.
    /// </summary>
    /// <param name="file">Normalized absolute file path.</param>
    public IReadOnlyList<FileOwner> ResolveFile(string file)
    {
        var owners = new List<FileOwner>();
        if (FindOwner(file) is { } owner)
        {
            owners.Add(new FileOwner(owner, FileRole.Source));
        }

        foreach (var (project, items, imports) in _inputs)
        {
            if (imports.Matches(file))
            {
                owners.Add(new FileOwner(project, FileRole.BuildInput));
            }
            else if (items.Matches(file) && !owners.Exists(o => ReferenceEquals(o.Project, project)))
            {
                owners.Add(new FileOwner(project, FileRole.Source));
            }
        }

        // Directory-wide files are matched by name elsewhere; they are not what an unresolved import hides.
        if (owners.Count == 0 && BuildOrSourceExtensions.Contains(Path.GetExtension(file)) && !BuildFiles.IsDirectoryWide(file))
        {
            foreach (var project in _unresolved)
            {
                owners.Add(new FileOwner(project, FileRole.BuildInput));
            }
        }

        return owners;
    }

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

/// <summary>How a changed file reaches a project.</summary>
public enum FileRole
{
    /// <summary>The project compiles or ships the file: inside its directory, or pulled in by an item.</summary>
    Source,

    /// <summary>The project imports the file into its build (a shared <c>.props</c>, <c>.targets</c> or <c>.projitems</c>).</summary>
    BuildInput,
}

/// <summary>A project a changed file affects directly, and how.</summary>
/// <param name="Project">The project.</param>
/// <param name="Role">How the file reaches it.</param>
public sealed record FileOwner(ProjectNode Project, FileRole Role);
