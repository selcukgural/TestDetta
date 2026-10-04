namespace TestDetta.Core;

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

    /// <summary>Per project, the projects that reference it without compiling against it (<see cref="ProjectNode.OpaqueReferences"/>).</summary>
    private readonly Dictionary<string, List<ProjectNode>> _opaqueConsumers = new(Paths.Comparer);

    /// <summary>Projects whose inputs could not all be resolved.</summary>
    private readonly ProjectNode[] _unresolved;

    /// <summary>Evaluated projects by each file of their build items (see <see cref="ProjectNode.EvaluatedItems"/>).</summary>
    private readonly Dictionary<string, List<ProjectNode>> _itemOwners = new(Paths.Comparer);

    /// <summary>Projects with item paths that could not be resolved, with their compiled patterns.</summary>
    private readonly (ProjectNode Project, System.Text.RegularExpressions.Regex[] Patterns)[] _unknownItems;

    /// <summary>Evaluated projects with items outside their directory, for files that no longer exist (<see cref="ProjectNode.ItemsOfDeletedFiles"/>).</summary>
    private readonly (ProjectNode Project, PathMatcher Items)[] _deletedFileItems;

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
        _deletedFileItems = [.. nodes.Values
            .Where(static p => p.ItemsOfDeletedFiles.Count > 0)
            .Select(static p => (p, new PathMatcher(p.ItemsOfDeletedFiles)))];
        _unknownItems = [.. nodes.Values
            .Where(static p => p.UnknownItemPatterns.Count > 0)
            .Select(static p => (p, p.UnknownItemPatterns.Select(static x => new System.Text.RegularExpressions.Regex(x, System.Text.RegularExpressions.RegexOptions.Compiled)).ToArray()))];

        foreach (var node in nodes.Values)
        {
            foreach (var item in node.EvaluatedItems ?? (IEnumerable<string>)[])
            {
                if (!_itemOwners.TryGetValue(item, out var owners))
                {
                    owners = [];
                    _itemOwners[item] = owners;
                }

                owners.Add(node);
            }

            foreach (var reference in node.OpaqueReferences)
            {
                AddOpaqueConsumer(reference, node);
            }

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

        // A project with a reference that could not be resolved may reference any project its pattern matches.
        foreach (var node in nodes.Values.Where(static p => p.HasUnknownReferences))
        {
            var patterns = node.UnknownReferencePatterns.Select(static p => new System.Text.RegularExpressions.Regex(p)).ToList();
            var opaquePatterns = node.UnknownOpaqueReferencePatterns.Select(static p => new System.Text.RegularExpressions.Regex(p)).ToList();
            foreach (var other in nodes.Keys.Where(k => !Paths.Comparer.Equals(k, node.Path) && patterns.Any(p => p.IsMatch(k))))
            {
                if (opaquePatterns.Exists(p => p.IsMatch(other)))
                {
                    AddOpaqueConsumer(other, node);
                }

                if (!_dependents.TryGetValue(other, out var list))
                {
                    list = [];
                    _dependents[other] = list;
                }

                if (!list.Contains(node.Path, Paths.Comparer))
                {
                    list.Add(node.Path);
                }
            }
        }
    }

    /// <summary>Records that <paramref name="consumer"/> references <paramref name="reference"/> without compiling against it.</summary>
    /// <param name="reference">Normalized path of the referenced project.</param>
    /// <param name="consumer">The referencing project.</param>
    private void AddOpaqueConsumer(string reference, ProjectNode consumer)
    {
        if (!_opaqueConsumers.TryGetValue(reference, out var consumers))
        {
            consumers = [];
            _opaqueConsumers[reference] = consumers;
        }

        if (!consumers.Contains(consumer))
        {
            consumers.Add(consumer);
        }
    }

    /// <summary>
    /// Projects that reference <paramref name="path"/> without compiling against it: as an analyzer or
    /// source generator, or with <c>ReferenceOutputAssembly="false"</c> (see <see cref="ProjectNode.OpaqueReferences"/>).
    /// </summary>
    /// <param name="path">Normalized project path.</param>
    public IReadOnlyList<ProjectNode> OpaqueConsumersOf(string path) =>
        _opaqueConsumers.TryGetValue(path, out var consumers) ? consumers : [];

    /// <summary>All projects found in the repository.</summary>
    public IReadOnlyCollection<ProjectNode> Projects => _nodes.Values;

    /// <summary>Returns the project at <paramref name="path"/>.</summary>
    /// <param name="path">Normalized absolute path of a .csproj file that is part of the graph.</param>
    /// <exception cref="KeyNotFoundException">The path is not a project of this graph.</exception>
    public ProjectNode this[string path] => _nodes[path];

    /// <summary>The project at <paramref name="path"/>, or null when the repository has none there.</summary>
    /// <param name="path">Normalized absolute project file path.</param>
    public ProjectNode? Find(string path) => _nodes.GetValueOrDefault(path);

    /// <summary>
    /// The project whose directory contains <paramref name="file"/>. When projects are nested, the
    /// innermost one wins. Null when the file is outside every project directory.
    /// </summary>
    /// <param name="file">Normalized absolute file path.</param>
    public ProjectNode? FindOwner(string file) => Array.Find(_byDepth, p => Paths.IsUnder(file, p.Directory));

    /// <summary>
    /// Every project a changed <paramref name="file"/> can affect directly, and how. As
    /// <see cref="FileRole.Source"/>: the evaluated projects whose build items include it; the projects
    /// of the innermost directory containing it (all of them when several share it) that were not
    /// evaluated, or all of them when no evaluated project includes the file, since a target may read
    /// a file that is no item; and projects that pull it in from outside their directory by a path
    /// the XML reading found. Projects that import it are <see cref="FileRole.BuildInput"/>. A build
    /// or source file no project claims is a build input of every project with unresolved inputs,
    /// since it may be what those resolve to.
    /// </summary>
    /// <param name="file">Normalized absolute file path.</param>
    public IReadOnlyList<FileOwner> ResolveFile(string file)
    {
        var owners = new List<FileOwner>();
        var included = _itemOwners.GetValueOrDefault(file);
        foreach (var project in included ?? [])
        {
            owners.Add(new FileOwner(project, FileRole.Source));
        }

        if (FindOwner(file) is { } owner)
        {
            // Projects that share the directory and were not evaluated compile the same files as far
            // as the XML tells (one project per Roslyn version in Meziantou.Analyzer); each owns it.
            foreach (var sibling in _byDepth)
            {
                if (Paths.Comparer.Equals(sibling.Directory, owner.Directory)
                    && (sibling.EvaluatedItems is null || included is null)
                    && !owners.Exists(o => ReferenceEquals(o.Project, sibling)))
                {
                    owners.Add(new FileOwner(sibling, FileRole.Source));
                }
            }
        }

        // Files named by an item path only MSBuild can compute (a linked file under $(RepoRoot)).
        foreach (var (project, patterns) in _unknownItems)
        {
            if (!owners.Exists(o => o.Project == project && o.Role == FileRole.Source) && Array.Exists(patterns, p => p.IsMatch(file)))
            {
                owners.Add(new FileOwner(project, FileRole.Source));
            }
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

        // MSBuild expanded the items on the files present; a deleted file an item glob named still
        // belongs to the project, which then runs as for any change to its non-C# files.
        if (_deletedFileItems.Length > 0 && !File.Exists(file))
        {
            foreach (var (project, items) in _deletedFileItems)
            {
                if (items.Matches(file) && !owners.Exists(o => ReferenceEquals(o.Project, project)))
                {
                    owners.Add(new FileOwner(project, FileRole.Source));
                }
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
