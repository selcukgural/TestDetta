using System.Collections.Frozen;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Finds every project file (<c>.csproj</c>, <c>.fsproj</c>, <c>.vbproj</c>) under a repository root
/// and reads its references, its imports and the files it pulls in from outside its directory.
/// The files are read as plain XML: MSBuild is not evaluated, so conditions are ignored and only the
/// common path properties are resolved; anything else is marked unresolved.
/// </summary>
/// <param name="logger">Receives debug details about scanned directories and parsed projects.</param>
public sealed partial class ProjectGraphLoader(ILogger<ProjectGraphLoader> logger)
{
    /// <summary>Directories that never contain source projects and are expensive to walk.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", ".git", ".vs", ".idea", "node_modules");

    /// <summary>Project file extensions and their language.</summary>
    private static readonly FrozenDictionary<string, ProjectLanguage> ProjectExtensions = new Dictionary<string, ProjectLanguage>
    {
        [".csproj"] = ProjectLanguage.CSharp,
        [".fsproj"] = ProjectLanguage.FSharp,
        [".vbproj"] = ProjectLanguage.VisualBasic,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Item types whose files end up in the build: compiled, embedded, copied, or given to analyzers.</summary>
    private static readonly FrozenSet<string> BuildItems = FrozenSet.Create(
        StringComparer.Ordinal, "Compile", "EmbeddedResource", "Content", "None", "AdditionalFiles");

    /// <summary>MSBuild files every project below them imports implicitly.</summary>
    private static readonly string[] ImplicitImports = ["Directory.Build.props", "Directory.Build.targets"];

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
            var node = Parse(file, root);
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
            foreach (var file in Directory.EnumerateFiles(dir, "*.*proj"))
            {
                if (ProjectExtensions.ContainsKey(Path.GetExtension(file)))
                {
                    yield return Paths.Normalize(file);
                }
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

    /// <summary>
    /// Reads one project: references and test markers from the project file itself, imports and
    /// external items from the project file, the files it imports, and the implicit
    /// <c>Directory.Build.*</c> files above it.
    /// </summary>
    /// <param name="projectPath">Normalized path of the project file.</param>
    /// <param name="root">Normalized repository root; implicit imports are looked for up to here.</param>
    private ProjectNode Parse(string projectPath, string root)
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
        string? assemblyName = null;

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

                case "AssemblyName" when element.Value.Trim() is { Length: > 0 } name && !name.Contains('$'):
                    // A value built from properties ($(...)) cannot be known without evaluating MSBuild.
                    assemblyName = name;
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

        var inputs = new BuildInputs(projectDir);
        var visited = new HashSet<string>(Paths.Comparer);
        Walk(projectPath, inputs, visited, doc);
        foreach (var name in ImplicitImports)
        {
            if (FindAbove(projectDir, name, root) is { } implicitImport)
            {
                Walk(implicitImport, inputs, visited, document: null);
            }
        }

        LogParsed(projectPath, isTest, references.Count);
        LogBuildInputs(projectPath, inputs.Imports.Count, inputs.ExternalItems.Count, inputs.Unresolved);
        var node = new ProjectNode(projectPath, projectDir, isTest, references)
        {
            Language = ProjectExtensions[Path.GetExtension(projectPath)],
            Imports = inputs.Imports,
            ExternalItems = inputs.ExternalItems,
            HasUnresolvedInputs = inputs.Unresolved,
        };

        return assemblyName is null ? node : node with { AssemblyName = assemblyName };
    }

    /// <summary>What <see cref="Walk"/> collects for one project.</summary>
    /// <param name="projectDirectory">Normalized directory of the project; items inside it need no tracking.</param>
    private sealed class BuildInputs(string projectDirectory)
    {
        /// <summary>Normalized directory of the project.</summary>
        public string ProjectDirectory { get; } = projectDirectory;

        /// <summary>Imported MSBuild files, existing or not (an optional import may be added later).</summary>
        public List<string> Imports { get; } = [];

        /// <summary>Item paths or patterns outside the project directory.</summary>
        public List<string> ExternalItems { get; } = [];

        /// <summary>True when an import or item path could not be resolved.</summary>
        public bool Unresolved { get; set; }
    }

    /// <summary>
    /// Collects the imports and external items of <paramref name="file"/> and, recursively, of the
    /// files it imports. SDK imports are skipped; each file is read once per project.
    /// </summary>
    /// <param name="file">Normalized path of an MSBuild file.</param>
    /// <param name="inputs">Collected inputs; updated in place.</param>
    /// <param name="visited">Files already read for this project.</param>
    /// <param name="document">The already parsed file, or null to read it.</param>
    private void Walk(string file, BuildInputs inputs, HashSet<string> visited, XDocument? document)
    {
        if (!visited.Add(file))
        {
            return;
        }

        if (document is null)
        {
            try
            {
                document = XDocument.Load(file);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                LogUnreadableImport(file, ex.Message);
                inputs.Unresolved = true;
                return;
            }
        }

        var directory = Paths.Normalize(Path.GetDirectoryName(file)!);
        foreach (var element in document.Descendants())
        {
            var name = element.Name.LocalName;
            if (name == "Import" && element.Attribute("Sdk") is null && element.Attribute("Project")?.Value is { Length: > 0 } project)
            {
                // Imports are relative to the importing file.
                foreach (var resolved in Resolve(project, directory, inputs.ProjectDirectory, inputs))
                {
                    if (!inputs.Imports.Contains(resolved, Paths.Comparer))
                    {
                        inputs.Imports.Add(resolved);
                    }

                    if (!Glob.IsPattern(resolved) && File.Exists(resolved))
                    {
                        Walk(resolved, inputs, visited, document: null);
                    }
                }

                continue;
            }

            if (!BuildItems.Contains(name) || element.Attribute("Include")?.Value is not { Length: > 0 } include)
            {
                continue;
            }

            // Items are relative to the project, wherever they are declared.
            foreach (var part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var resolved in Resolve(part, inputs.ProjectDirectory, inputs.ProjectDirectory, inputs, directory))
                {
                    if (!Paths.IsUnder(resolved, inputs.ProjectDirectory) && !inputs.ExternalItems.Contains(resolved, Paths.Comparer))
                    {
                        inputs.ExternalItems.Add(resolved);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves an import or item path: backslashes become slashes, <c>$(MSBuildThisFileDirectory)</c>
    /// and <c>$(MSBuildProjectDirectory)</c> are substituted, <c>GetPathOfFileAbove</c> is searched,
    /// and relative paths are taken from <paramref name="baseDirectory"/>. Anything with other
    /// properties or item functions marks the inputs unresolved and yields nothing.
    /// </summary>
    /// <param name="value">Attribute value.</param>
    /// <param name="baseDirectory">Directory relative paths start from.</param>
    /// <param name="projectDirectory">Directory of the project.</param>
    /// <param name="inputs">Collected inputs, marked unresolved when needed.</param>
    /// <param name="thisFileDirectory">Directory of the declaring file, when it differs from <paramref name="baseDirectory"/>.</param>
    private static IEnumerable<string> Resolve(
        string value,
        string baseDirectory,
        string projectDirectory,
        BuildInputs inputs,
        string? thisFileDirectory = null)
    {
        var fileDirectory = thisFileDirectory ?? baseDirectory;
        var path = value.Trim().Replace('\\', '/')
            .Replace("$(MSBuildThisFileDirectory)", fileDirectory + "/", StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase);

        if (GetPathOfFileAbove().Match(path) is { Success: true } above)
        {
            var start = above.Groups[2].Success && above.Groups[2].Value.Length > 0
                ? Paths.Normalize(Path.Combine(fileDirectory, above.Groups[2].Value))
                : fileDirectory;
            var found = FindAbove(start, above.Groups[1].Value, root: null);
            if (found is null)
            {
                yield break;
            }

            path = path.Replace(above.Value, found, StringComparison.Ordinal);
        }

        if (path.Contains("$(", StringComparison.Ordinal) || path.Contains("@(", StringComparison.Ordinal)
            || path.Contains("%(", StringComparison.Ordinal))
        {
            inputs.Unresolved = true;
            yield break;
        }

        yield return Paths.Normalize(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    /// <summary>
    /// Matches <c>$([MSBuild]::GetPathOfFileAbove('name', 'start'))</c>, the usual way a
    /// <c>Directory.Build.props</c> imports the one above it; the start directory may be missing.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'([^']+)'\s*(?:,\s*'([^']*)'\s*)?\)\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GetPathOfFileAbove();

    /// <summary>
    /// The nearest <paramref name="name"/> at or above <paramref name="directory"/>, as MSBuild
    /// finds implicit imports; null when there is none up to <paramref name="root"/> (or the file
    /// system root when <paramref name="root"/> is null).
    /// </summary>
    /// <param name="directory">Normalized directory to start from.</param>
    /// <param name="name">File name to look for.</param>
    /// <param name="root">Normalized directory to stop at, inclusive, or null.</param>
    private static string? FindAbove(string directory, string name, string? root)
    {
        for (var current = directory; current.Length > 0; current = Path.GetDirectoryName(current)?.Replace('\\', '/') ?? string.Empty)
        {
            var candidate = Path.Combine(current, name);
            if (File.Exists(candidate))
            {
                return Paths.Normalize(candidate);
            }

            if (root is not null && Paths.Comparer.Equals(current, root))
            {
                return null;
            }
        }

        return null;
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

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{Project}: {ImportCount} import(s), {ItemCount} item path(s) outside its directory, unresolved inputs: {Unresolved}")]
    private partial void LogBuildInputs(string project, int importCount, int itemCount, bool unresolved);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read imported file {File}: {Error}; treating the project's inputs as unresolved")]
    private partial void LogUnreadableImport(string file, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Loaded {Count} project(s) in {ElapsedMs} ms")]
    private partial void LogLoaded(int count, long elapsedMs);
}
