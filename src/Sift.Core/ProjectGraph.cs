using System.Xml.Linq;

namespace Sift.Core;

public sealed record ProjectNode(string Path, string Directory, bool IsTestProject, IReadOnlyList<string> References);

/// <summary>
/// Static view of the .csproj files in a repository and their ProjectReference edges.
/// Paths are absolute and normalized with '/' separators.
/// </summary>
public sealed class ProjectGraph
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules"];

    private readonly Dictionary<string, ProjectNode> _nodes;
    private readonly Dictionary<string, List<string>> _dependents;

    private ProjectGraph(Dictionary<string, ProjectNode> nodes)
    {
        _nodes = nodes;
        _dependents = nodes.Keys.ToDictionary(k => k, _ => new List<string>(), PathComparer);
        foreach (var node in nodes.Values)
        foreach (var reference in node.References)
            if (_dependents.TryGetValue(reference, out var list))
                list.Add(node.Path);
    }

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyCollection<ProjectNode> Projects => _nodes.Values;

    public ProjectNode this[string path] => _nodes[path];

    public IReadOnlyList<string> DependentsOf(string path) =>
        _dependents.TryGetValue(path, out var list) ? list : [];

    public static ProjectGraph Load(string repoRoot)
    {
        var nodes = new Dictionary<string, ProjectNode>(PathComparer);
        foreach (var file in EnumerateProjectFiles(Paths.Normalize(repoRoot)))
        {
            var node = Parse(file);
            nodes[node.Path] = node;
        }
        return new ProjectGraph(nodes);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string dir)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.csproj"))
            yield return Paths.Normalize(file);

        foreach (var sub in System.IO.Directory.EnumerateDirectories(dir))
        {
            if (SkippedDirectories.Contains(System.IO.Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var file in EnumerateProjectFiles(sub))
                yield return file;
        }
    }

    private static ProjectNode Parse(string projectPath)
    {
        var doc = XDocument.Load(projectPath);
        var projectDir = Paths.Normalize(System.IO.Path.GetDirectoryName(projectPath)!);

        // Element names are matched locally so both SDK-style and legacy (namespaced) projects work.
        var elements = doc.Descendants().ToList();

        var references = elements
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Paths.Normalize(System.IO.Path.Combine(projectDir, v!.Replace('\\', '/'))))
            .Distinct(PathComparer)
            .ToList();

        var isTest =
            elements.Any(e => e.Name.LocalName == "IsTestProject" &&
                              string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) ||
            elements.Any(e => e.Name.LocalName == "PackageReference" &&
                              string.Equals(e.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk",
                                  StringComparison.OrdinalIgnoreCase));

        return new ProjectNode(projectPath, projectDir, isTest, references);
    }
}
