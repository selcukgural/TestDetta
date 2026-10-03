using System.Collections.Frozen;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Finds every project file (<c>.csproj</c>, <c>.fsproj</c>, <c>.vbproj</c>) under a repository root
/// and reads its references, its imports and the files it pulls in from outside its directory.
/// The files are read as plain XML, where conditions are ignored and only the common path properties
/// are resolved; anything else is marked unresolved. With an <see cref="IProjectEvaluator"/>, MSBuild's
/// answers then replace the guesses for every project it could evaluate (references, items, test
/// flag, assembly name), and the XML reading stays for imports and for projects it could not.
/// </summary>
/// <param name="logger">Receives debug details about scanned directories and parsed projects.</param>
/// <param name="evaluator">Evaluates the projects with MSBuild; null to read them as XML only.</param>
public sealed partial class ProjectGraphLoader(ILogger<ProjectGraphLoader> logger, IProjectEvaluator? evaluator = null)
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

        if (evaluator is not null && nodes.Count > 0)
        {
            ApplyEvaluation(root, nodes, evaluator);
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
    /// Replaces the XML guesses of every project MSBuild evaluated with its answers. References,
    /// items and the assembly name come from MSBuild alone, and so the patterns that stood for
    /// references and items the XML reading could not resolve are dropped. The test flag is the union
    /// of both: without a restore, MSBuild does not see the props of test packages that set
    /// <c>IsTestProject</c>, and taking a library for a test project costs an empty run while the
    /// opposite loses its tests. Imports keep the XML reading.
    /// </summary>
    /// <param name="root">Normalized repository root.</param>
    /// <param name="nodes">Projects read as XML, replaced in place.</param>
    /// <param name="projectEvaluator">The evaluator.</param>
    private void ApplyEvaluation(string root, Dictionary<string, ProjectNode> nodes, IProjectEvaluator projectEvaluator)
    {
        var evaluated = projectEvaluator.Evaluate(root, nodes.Keys);
        if (evaluated is null)
        {
            LogNotEvaluated(nodes.Count);
            return;
        }

        foreach (var (path, node) in nodes.ToList())
        {
            if (!evaluated.TryGetValue(path, out var project))
            {
                LogProjectNotEvaluated(path);
                continue;
            }

            var packages = new HashSet<string>(node.PackageReferences, StringComparer.OrdinalIgnoreCase);
            packages.UnionWith(project.Packages);
            nodes[path] = node with
            {
                IsTestProject = node.IsTestProject || project.IsTestProject || project.Packages.Any(TestPackages.Contains),
                References = project.References,
                UnknownReferencePatterns = [],
                OpaqueReferences = project.OpaqueReferences,
                UnknownOpaqueReferencePatterns = [],
                UnknownItemPatterns = [],
                ExternalItems = [],
                PackageReferences = packages,
                AssemblyName = project.AssemblyName ?? node.AssemblyName,
                AssemblyNameUnresolved = project.AssemblyName is null && node.AssemblyNameUnresolved,
                EvaluatedItems = project.Items,
                SymbolSets = project.SymbolSets,
            };
        }

        LogEvaluated(evaluated.Count, nodes.Count);
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
        var opaque = new HashSet<string>(Paths.Comparer);
        var isTest = false;

        // Elements are matched by local name so legacy projects with the MSBuild XML namespace also work.
        foreach (var element in doc.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "ProjectReference" when element.Attribute("Include")?.Value is { Length: > 0 } include
                                             && !include.Contains("$(", StringComparison.Ordinal) && !include.Contains("@(", StringComparison.Ordinal):
                    var reference = Paths.Normalize(Path.Combine(projectDir, include.Replace('\\', '/')));
                    if (!references.Contains(reference, Paths.Comparer))
                    {
                        references.Add(reference);
                    }

                    if (IsOpaqueReference(element))
                    {
                        opaque.Add(reference);
                    }

                    break;

                case "IsTestProject" when bool.TryParse(element.Value.Trim(), out var value) && value:
                    isTest = true;
                    break;

                case "PackageReference" when element.Attribute("Include")?.Value is { } package && TestPackages.Contains(package):
                    isTest = true;
                    break;
            }
        }

        // <Project Sdk="MSTest.Sdk/3.8.0"> brings MSTest and its runner along, and so does a company
        // SDK for test projects such as Meziantou.NET.Sdk.Test.
        isTest |= doc.Root?.Attribute("Sdk")?.Value is { } sdk && IsTestSdk(sdk);

        // In MSBuild's order: Directory.Build.props, the project, Directory.Build.targets, so that a
        // property the project sets is known when the targets file imports a path built from it.
        var inputs = new BuildInputs(projectDir, Path.GetFileNameWithoutExtension(projectPath));
        var visited = new HashSet<string>(Paths.Comparer);
        if (FindAbove(projectDir, ImplicitImports[0], root) is { } props)
        {
            Walk(props, inputs, visited, document: null);
        }

        Walk(projectPath, inputs, visited, doc);
        if (FindAbove(projectDir, ImplicitImports[1], root) is { } targets)
        {
            Walk(targets, inputs, visited, document: null);
        }

        // Test packages are often added from Directory.Build.props or targets; those count when they
        // are added unconditionally. The naming convention catches the rest; a false positive only
        // runs an extra project, which reports that it has no tests.
        isTest |= inputs.MarksTestProject;
        if (!isTest && HasTestProjectName(projectPath))
        {
            LogTestByName(projectPath);
            isTest = true;
        }

        // References declared by imports, or through properties and items, are resolved like imports.
        // One that cannot be resolved becomes a pattern over project paths, its unknown parts matching
        // anything, and the project depends on every project the pattern matches.
        var unknownReferences = new List<string>();
        foreach (var (include, declaringDirectory) in ExpandItems(inputs.ProjectReferences, inputs))
        {
            if (ResolveReference(include, declaringDirectory) is not { } resolved)
            {
                unknownReferences.Add(PathPattern(include, projectDir, declaringDirectory));
                continue;
            }

            foreach (var reference in resolved.Where(r => !references.Contains(r, Paths.Comparer)))
            {
                references.Add(reference);
            }
        }

        // The same includes again, for the references the project does not compile against.
        var unknownOpaqueReferences = new List<string>();
        foreach (var (include, declaringDirectory) in ExpandItems(inputs.OpaqueProjectReferences, inputs))
        {
            if (ResolveReference(include, declaringDirectory) is not { } resolved)
            {
                unknownOpaqueReferences.Add(PathPattern(include, projectDir, declaringDirectory));
                continue;
            }

            opaque.UnionWith(resolved);
        }

        // A property expanded to a path that is not there was guessed wrong (conditions are not
        // evaluated), and one with an assignment only MSBuild can compute may have another value
        // altogether; a plain path that is not there names no project and is ignored as before.
        // Null when the include cannot be resolved.
        List<string>? ResolveReference(string include, string declaringDirectory)
        {
            var resolved = Resolve(include, projectDir, projectDir, inputs, declaringDirectory).ToList();
            var properties = PropertyReference().Matches(include).Select(static m => m.Groups[1].Value).Where(static p => !IsPathProperty(p)).ToList();
            return resolved.Count == 0 || properties.Any(inputs.IncompleteProperties.Contains) || properties.Count > 0 && resolved.Any(static r => !File.Exists(r))
                ? null
                : resolved;
        }

        if (unknownReferences.Count > 0 && logger.IsEnabled(LogLevel.Debug))
        {
            var patterns = string.Join(", ", unknownReferences);
            LogUnknownReferences(projectPath, patterns);
        }

        LogParsed(projectPath, isTest, references.Count);
        LogBuildInputs(projectPath, inputs.Imports.Count, inputs.ExternalItems.Count, inputs.Unresolved);
        var node = new ProjectNode(projectPath, projectDir, isTest, references)
        {
            Language = ProjectExtensions[Path.GetExtension(projectPath)],
            Imports = inputs.Imports,
            ExternalItems = inputs.ExternalItems,
            HasUnresolvedInputs = inputs.Unresolved,
            UnknownReferencePatterns = unknownReferences,
            OpaqueReferences = opaque,
            UnknownOpaqueReferencePatterns = unknownOpaqueReferences,
            UnknownItemPatterns = inputs.UnknownItems,
            PackageReferences = inputs.Packages,
        };

        return AssemblyNameOf(inputs) switch
        {
            null => node,
            { } name when name.Length == 0 => node with { AssemblyNameUnresolved = true },
            { } name => node with { AssemblyName = name },
        };
    }

    /// <summary>
    /// True for a <c>ProjectReference</c> the project does not compile against (see
    /// <see cref="ProjectNode.OpaqueReferences"/>): an <c>OutputItemType</c> or
    /// <c>ReferenceOutputAssembly="false"</c>, as an attribute or a child element. A value only MSBuild
    /// can compute counts as opaque. Metadata set elsewhere (<c>ItemDefinitionGroup</c>, <c>Update</c>)
    /// is seen only by the MSBuild evaluation.
    /// </summary>
    /// <param name="element">The <c>ProjectReference</c> element.</param>
    private static bool IsOpaqueReference(XElement element)
    {
        string? Metadata(string name) =>
            (element.Attribute(name)?.Value ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value)?.Trim();

        return Metadata("OutputItemType") is { Length: > 0 }
               || Metadata("ReferenceOutputAssembly") is { } reference
                  && (reference.Equals("false", StringComparison.OrdinalIgnoreCase) || reference.Contains("$(", StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>AssemblyName</c> set by the project or its imports: null when none sets it, empty when
    /// the value cannot be known without evaluating MSBuild (a property function, an unknown property,
    /// or several candidate values under conditions that are not evaluated here).
    /// </summary>
    /// <param name="inputs">Collected inputs of the project.</param>
    private static string? AssemblyNameOf(BuildInputs inputs)
    {
        if (inputs.AssemblyNameUnresolved)
        {
            return string.Empty;
        }

        if (!inputs.Properties.TryGetValue("AssemblyName", out var values))
        {
            return null;
        }

        return values.Count == 1 ? values.First() : string.Empty;
    }

    /// <summary>What <see cref="Walk"/> collects for one project.</summary>
    /// <param name="projectDirectory">Normalized directory of the project; items inside it need no tracking.</param>
    /// <param name="projectName">Project file name without extension, for <c>$(MSBuildProjectName)</c>.</param>
    private sealed class BuildInputs(string projectDirectory, string projectName)
    {
        /// <summary>Normalized directory of the project.</summary>
        public string ProjectDirectory { get; } = projectDirectory;

        /// <summary>Project file name without extension.</summary>
        public string ProjectName { get; } = projectName;

        /// <summary>
        /// Literal property values seen so far, by property name. Conditions are not evaluated, so a
        /// property may have several candidate values; a path built from it resolves to each.
        /// </summary>
        public Dictionary<string, HashSet<string>> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// <c>ProjectReference</c> includes that need resolving: those declared by an import, and those
        /// of the project itself that use a property or item. With the directory of the declaring file.
        /// </summary>
        public List<(string Include, string Directory)> ProjectReferences { get; } = [];

        /// <summary>The part of <see cref="ProjectReferences"/> that <see cref="IsOpaqueReference"/> accepts.</summary>
        public List<(string Include, string Directory)> OpaqueProjectReferences { get; } = [];

        /// <summary>Patterns for compile and content items whose path could not be resolved.</summary>
        public List<string> UnknownItems { get; } = [];

        /// <summary>
        /// Item includes by item name, with the directory of the declaring file, so that a reference
        /// written as <c>@(Name)</c> can be resolved. Conditions are not evaluated: every include counts.
        /// </summary>
        public Dictionary<string, List<(string Include, string Directory)>> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Properties with an assignment that could not be recorded (a property function, an unknown
        /// property): their candidate values in <see cref="Properties"/> may miss the real one.
        /// </summary>
        public HashSet<string> IncompleteProperties { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Imported MSBuild files, existing or not (an optional import may be added later).</summary>
        public List<string> Imports { get; } = [];

        /// <summary>Item paths or patterns outside the project directory.</summary>
        public List<string> ExternalItems { get; } = [];

        /// <summary>True when an import or item path could not be resolved.</summary>
        public bool Unresolved { get; set; }

        /// <summary>True when an <c>AssemblyName</c> property was set to a value that could not be expanded.</summary>
        public bool AssemblyNameUnresolved { get; set; }

        /// <summary>
        /// True when an imported file unconditionally references a test runner package or sets
        /// <c>IsTestProject</c> to true. Conditions are not evaluated, so conditional ones do not count.
        /// </summary>
        public bool MarksTestProject { get; set; }

        /// <summary>Packages named by <c>PackageReference</c> items.</summary>
        public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
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
            if (!IsConditional(element)
                && (name == "IsTestProject" && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                    || name == "PackageReference" && element.Attribute("Include")?.Value is { } testPackage && TestPackages.Contains(testPackage)))
            {
                inputs.MarksTestProject = true;
            }

            if (element.Parent?.Name.LocalName == "PropertyGroup" && !element.HasElements)
            {
                if (!RecordProperty(inputs, name, element.Value.Trim()))
                {
                    inputs.IncompleteProperties.Add(name);
                    inputs.AssemblyNameUnresolved |= name.Equals("AssemblyName", StringComparison.OrdinalIgnoreCase);
                }

                continue;
            }

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

            if (element.Parent?.Name.LocalName == "ItemGroup" && element.Attribute("Include")?.Value is { Length: > 0 } item)
            {
                if (!inputs.Items.TryGetValue(name, out var items))
                {
                    items = [];
                    inputs.Items[name] = items;
                }

                items.Add((item, directory));
            }

            if (name == "ProjectReference" && element.Attribute("Include")?.Value is { Length: > 0 } projectReference)
            {
                // The project's own plain references were read with the project; the rest wait for
                // the properties its imports set.
                var own = Paths.Comparer.Equals(file, Paths.Normalize(Path.Combine(inputs.ProjectDirectory, inputs.ProjectName + Path.GetExtension(file))));
                foreach (var part in projectReference.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!own || part.Contains("$(", StringComparison.Ordinal) || part.Contains("@(", StringComparison.Ordinal))
                    {
                        inputs.ProjectReferences.Add((part, directory));
                        if (IsOpaqueReference(element))
                        {
                            inputs.OpaqueProjectReferences.Add((part, directory));
                        }
                    }
                }

                continue;
            }

            if (name == "PackageReference" && (element.Attribute("Include") ?? element.Attribute("Update"))?.Value is { Length: > 0 } package)
            {
                inputs.Packages.Add(package);
                continue;
            }

            if (!BuildItems.Contains(name) || element.Attribute("Include")?.Value is not { Length: > 0 } include)
            {
                continue;
            }

            // Items are relative to the project, wherever they are declared.
            foreach (var part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var resolvedItems = Resolve(part, inputs.ProjectDirectory, inputs.ProjectDirectory, inputs, directory).ToList();
                if (resolvedItems.Count == 0 && name == "Compile" && IsSpecificUnresolvedPath(part))
                {
                    // A path only MSBuild can compute: the files it may name still belong to the project.
                    var pattern = PathPattern(part, inputs.ProjectDirectory, directory);
                    if (!inputs.UnknownItems.Contains(pattern))
                    {
                        inputs.UnknownItems.Add(pattern);
                    }
                }

                foreach (var resolved in resolvedItems)
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
    /// Records a property value that needs no evaluation: literal text, or text built only from
    /// properties already known. Values with item or metadata references are skipped.
    /// </summary>
    /// <param name="inputs">Collected inputs; updated in place.</param>
    /// <param name="name">Property name.</param>
    /// <param name="value">Property value as written.</param>
    /// <returns>False when the value was skipped because it needs evaluation.</returns>
    private static bool RecordProperty(BuildInputs inputs, string name, string value)
    {
        if (value.Length == 0 || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal)
            || value.Contains("$([", StringComparison.Ordinal))
        {
            return false;
        }

        var expanded = ExpandProperties(value, inputs);
        if (expanded.Count == 0)
        {
            return false;
        }

        foreach (var candidate in expanded)
        {
            if (!inputs.Properties.TryGetValue(name, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                inputs.Properties[name] = values;
            }

            values.Add(candidate);
        }

        return true;
    }

    /// <summary>
    /// Replaces every <c>$(Name)</c> with each known value of the property; nothing when a property
    /// is unknown. At most 16 combinations are produced, which is plenty for path properties.
    /// </summary>
    /// <param name="value">Text with property references.</param>
    /// <param name="inputs">Known properties.</param>
    private static List<string> ExpandProperties(string value, BuildInputs inputs)
    {
        List<string> results = [value];
        while (results.Count > 0 && PropertyReference().Match(results[0]) is { Success: true } reference)
        {
            var property = reference.Groups[1].Value;
            var values = property.Equals("MSBuildProjectName", StringComparison.OrdinalIgnoreCase)
                ? new HashSet<string>(StringComparer.Ordinal) { inputs.ProjectName }
                : inputs.Properties.GetValueOrDefault(property);
            if (values is null || values.Count == 0)
            {
                return [];
            }

            var next = new List<string>();
            foreach (var partial in results)
            {
                foreach (var candidate in values)
                {
                    if (next.Count < 16)
                    {
                        next.Add(partial.Replace(reference.Value, candidate, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            results = next;
        }

        // A reference left over is a property function such as $(Name.Substring(0, 3)), which only
        // MSBuild can evaluate.
        return results.Count > 0 && results[0].Contains("$(", StringComparison.Ordinal) ? [] : results;
    }

    /// <summary>
    /// Replaces a reference written as exactly <c>@(Name)</c> with the includes of the items of that
    /// name (Open-XML-SDK's <c>@(OoxGenerator)</c>); others, and items never declared, pass unchanged.
    /// </summary>
    /// <param name="references">Reference includes with their declaring directory.</param>
    /// <param name="inputs">Collected inputs with the declared items.</param>
    private static IEnumerable<(string Include, string Directory)> ExpandItems(List<(string Include, string Directory)> references, BuildInputs inputs)
    {
        foreach (var reference in references)
        {
            if (ItemReference().Match(reference.Include) is { Success: true } match
                && inputs.Items.TryGetValue(match.Groups[1].Value, out var items) && items.Count > 0)
            {
                foreach (var item in items)
                {
                    foreach (var part in item.Include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        yield return (part, item.Directory);
                    }
                }

                continue;
            }

            yield return reference;
        }
    }

    /// <summary>
    /// An anchored, case-insensitive pattern over normalized paths for a reference or item path that
    /// could not be resolved: the known directory properties are substituted, every other property,
    /// item or metadata reference matches anything, globs (<c>*</c>, <c>**</c>, <c>?</c>) keep their
    /// meaning, and a relative path starts at the project.
    /// </summary>
    /// <param name="include">Path as written.</param>
    /// <param name="projectDirectory">Directory of the project.</param>
    /// <param name="fileDirectory">Directory of the declaring file.</param>
    private static string PathPattern(string include, string projectDirectory, string fileDirectory)
    {
        const string unknown = "\u0001", anyDepth = "\u0002", anyName = "\u0003", anyChar = "\u0004";
        var path = include.Trim().Replace('\\', '/')
            .Replace("$(MSBuildThisFileDirectory)", fileDirectory + "/", StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase);
        path = UnknownPart().Replace(path, unknown);
        path = path.Replace("**/", anyDepth, StringComparison.Ordinal).Replace("**", unknown, StringComparison.Ordinal)
            .Replace("*", anyName, StringComparison.Ordinal).Replace("?", anyChar, StringComparison.Ordinal);
        if (!path.StartsWith(unknown, StringComparison.Ordinal))
        {
            path = Paths.Normalize(Path.IsPathRooted(path) ? path : Path.Combine(projectDirectory, path));
        }

        return "(?i)^" + System.Text.RegularExpressions.Regex.Escape(path)
            .Replace(unknown, ".*", StringComparison.Ordinal)
            .Replace(anyDepth, "(.*/)?", StringComparison.Ordinal)
            .Replace(anyName, "[^/]*", StringComparison.Ordinal)
            .Replace(anyChar, "[^/]", StringComparison.Ordinal) + "$";
    }

    /// <summary>
    /// True for a path with a part only MSBuild can compute that still names something specific once
    /// that part is unknown: at least two literal segments, or a literal file name
    /// (<c>$(RepoRoot)\test\Core.Tests\X.cs</c>). <c>@(Bundle)</c> or <c>$(Shared)**\*.cs</c> would
    /// match every file; such items keep the older rule (the project claims files nobody else does).
    /// </summary>
    /// <param name="path">Item path as written.</param>
    private static bool IsSpecificUnresolvedPath(string path)
    {
        if (!path.Contains("$(", StringComparison.Ordinal) && !path.Contains("@(", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = UnknownPart().Replace(path, "/").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var literal = segments.Count(static x => x.IndexOfAny(['*', '?']) < 0);
        var fileName = segments.Length > 0 && segments[^1].IndexOfAny(['*', '?']) < 0 && segments[^1].Contains('.', StringComparison.Ordinal);
        return literal >= 2 || fileName;
    }

    /// <summary>Matches a reference that is exactly one item list, such as <c>@(OoxGenerator)</c>.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*@\((\w+)\)\s*$")]
    private static partial System.Text.RegularExpressions.Regex ItemReference();

    /// <summary>Matches a property reference with any nesting of parentheses, an item list or a metadata reference.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\$\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)|@\([^)]*\)|%\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex UnknownPart();

    /// <summary>True for the properties <see cref="Resolve"/> substitutes exactly: the declaring file's and the project's directory.</summary>
    /// <param name="name">Property name.</param>
    private static bool IsPathProperty(string name) =>
        name.Equals("MSBuildThisFileDirectory", StringComparison.OrdinalIgnoreCase) || name.Equals("MSBuildProjectDirectory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Matches a plain property reference such as <c>$(ProjectType)</c>.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\$\((\w+)\)")]
    private static partial System.Text.RegularExpressions.Regex PropertyReference();

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

        if (path.Contains("@(", StringComparison.Ordinal) || path.Contains("%(", StringComparison.Ordinal)
            || path.Contains("$([", StringComparison.Ordinal))
        {
            inputs.Unresolved = true;
            yield break;
        }

        // Properties the project or its imports set literally, such as $(ProjectType).
        var expanded = path.Contains("$(", StringComparison.Ordinal) ? ExpandProperties(path, inputs) : [path];
        if (expanded.Count == 0)
        {
            inputs.Unresolved = true;
            yield break;
        }

        foreach (var candidate in expanded)
        {
            yield return Paths.Normalize(Path.IsPathRooted(candidate) ? candidate : Path.Combine(baseDirectory, candidate));
        }
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
        // A suffix after the test part: Lib.Test.roslyn5.9.csproj (one project per Roslyn version).
        foreach (var segment in name.ToString().Split('.'))
        {
            if (segment.Equals("Test", StringComparison.OrdinalIgnoreCase) || segment.Equals("Tests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Specs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Spec", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for an MSBuild SDK that makes a project a test project: <c>MSTest.Sdk</c>, or an SDK whose
    /// name has a <c>Test</c> or <c>Tests</c> part, such as <c>Meziantou.NET.Sdk.Test</c>. A false
    /// positive only runs an extra project.
    /// </summary>
    /// <param name="sdk">Value of the <c>Sdk</c> attribute, possibly several separated by <c>;</c>, each with an optional <c>/version</c>.</param>
    private static bool IsTestSdk(string sdk)
    {
        foreach (var part in sdk.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.Split('/')[0];
            if (name.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)
                || name.Split('.').Any(static s => s.Equals("Test", StringComparison.OrdinalIgnoreCase) || s.Equals("Tests", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Packages that make a project a test project: the VSTest host, and the frameworks that bring
    /// their own runner (xUnit v3, MSTest, NUnit's adapter, TUnit). Plain framework packages
    /// (<c>xunit.extensibility.core</c>, <c>NUnit</c>) are also used by libraries of test helpers.
    /// </summary>
    private static readonly FrozenSet<string> TestPackages = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Microsoft.NET.Test.Sdk", "xunit.v3", "xunit.v3.mtp-v1", "xunit.v3.mtp-v2", "xunit.v3.mtp-off",
        "MSTest", "MSTest.TestAdapter", "NUnit3TestAdapter", "TUnit", "TUnit.Engine");

    /// <summary>True when the element or one of its ancestors has a <c>Condition</c>.</summary>
    /// <param name="element">MSBuild element.</param>
    private static bool IsConditional(XElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.Attribute("Condition") is not null)
            {
                return true;
            }
        }

        return false;
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
        Message = "{Project} has project references that cannot be resolved; it depends on every project matching {Patterns}")]
    private partial void LogUnknownReferences(string project, string patterns);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "MSBuild could not evaluate the {Count} project(s); their references and files are read from the XML only")]
    private partial void LogNotEvaluated(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MSBuild could not evaluate {Project}; its references and files are read from the XML only")]
    private partial void LogProjectNotEvaluated(string project);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MSBuild evaluated {Evaluated} of {Count} project(s)")]
    private partial void LogEvaluated(int evaluated, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Loaded {Count} project(s) in {ElapsedMs} ms")]
    private partial void LogLoaded(int count, long elapsedMs);
}
