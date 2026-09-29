using System.Collections.Frozen;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Finds every .csproj file under a repository root and reads its project references.
/// The files are read as plain XML: MSBuild is not evaluated, so conditions and imports are ignored.
/// </summary>
/// <param name="logger">Receives debug details about scanned directories and parsed projects.</param>
public sealed partial class ProjectGraphLoader(ILogger<ProjectGraphLoader> logger)
{
    /// <summary>Directories that never contain source projects and are expensive to walk.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", ".git", ".vs", ".idea", "node_modules");

    /// <summary>Loads all projects beneath <paramref name="repoRoot"/>.</summary>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <exception cref="InvalidDataException">A .csproj file is not valid XML.</exception>
    public ProjectGraph Load(string repoRoot)
    {
        var root = Paths.Normalize(repoRoot);
        var stopwatch = Stopwatch.StartNew();
        LogScanning(root);

        var nodes = new Dictionary<string, ProjectNode>(Paths.Comparer);
        foreach (var file in EnumerateProjectFiles(root))
        {
            var node = Parse(file);
            nodes[node.Path] = node;
        }

        var graph = new ProjectGraph(nodes);
        WarnAboutMissingReferences(nodes);
        LogLoaded(nodes.Count, stopwatch.ElapsedMilliseconds);
        return graph;
    }

    /// <summary>Walks the directory tree depth first, skipping <see cref="SkippedDirectories"/>.</summary>
    /// <param name="root">Normalized directory to start from.</param>
    private IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.csproj"))
            {
                yield return Paths.Normalize(file);
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (SkippedDirectories.Contains(Path.GetFileName(sub)))
                {
                    LogSkippedDirectory(sub);
                    continue;
                }

                pending.Push(sub);
            }
        }
    }

    /// <summary>Reads references and the test-project marker from one .csproj file in a single pass.</summary>
    /// <param name="projectPath">Normalized path of the .csproj file.</param>
    private ProjectNode Parse(string projectPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Could not parse {projectPath}: {ex.Message}", ex);
        }

        var projectDir = Paths.Normalize(Path.GetDirectoryName(projectPath)!);
        var references = new List<string>();
        var isTest = false;

        // Elements are matched by local name so legacy projects with the MSBuild XML namespace also work.
        foreach (var element in doc.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "ProjectReference" when element.Attribute("Include")?.Value is { Length: > 0 } include:
                    var reference = Paths.Normalize(Path.Combine(projectDir, include.Replace('\\', '/')));
                    if (!references.Contains(reference, Paths.Comparer))
                    {
                        references.Add(reference);
                    }
                    break;

                case "IsTestProject" when bool.TryParse(element.Value.Trim(), out var value) && value:
                    isTest = true;
                    break;

                case "PackageReference"
                    when string.Equals(element.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk",
                        StringComparison.OrdinalIgnoreCase):
                    isTest = true;
                    break;
            }
        }

        // Test packages are often added from Directory.Build.props, which is not evaluated here.
        // The naming convention catches those; a false positive only runs an extra project.
        if (!isTest && HasTestProjectName(projectPath))
        {
            LogTestByName(projectPath);
            isTest = true;
        }

        LogParsed(projectPath, isTest, references.Count);
        return new ProjectNode(projectPath, projectDir, isTest, references);
    }

    /// <summary>True when the file name ends with <c>Test</c> or <c>Tests</c>, as in <c>App.Tests.csproj</c>.</summary>
    /// <param name="projectPath">Path of the .csproj file.</param>
    private static bool HasTestProjectName(string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath.AsSpan());
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logs references that point outside the repository or to a file that does not exist. They are
    /// dropped from the graph, so a change there can never select anything.
    /// </summary>
    /// <param name="nodes">All loaded projects.</param>
    private void WarnAboutMissingReferences(Dictionary<string, ProjectNode> nodes)
    {
        foreach (var node in nodes.Values)
        {
            foreach (var reference in node.References)
            {
                if (!nodes.ContainsKey(reference))
                {
                    LogUnknownReference(node.Path, reference);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scanning {Root} for .csproj files")]
    private partial void LogScanning(string root);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Skipping directory {Directory}")]
    private partial void LogSkippedDirectory(string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Parsed {Project} (test: {IsTest}, references: {ReferenceCount})")]
    private partial void LogParsed(string project, bool isTest, int referenceCount);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{Project} has no test markers in its own file; treated as a test project because of its name")]
    private partial void LogTestByName(string project);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Project} references {Reference}, which is not a project in this repository; it is ignored")]
    private partial void LogUnknownReference(string project, string reference);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Loaded {Count} project(s) in {ElapsedMs} ms")]
    private partial void LogLoaded(int count, long elapsedMs);
}
