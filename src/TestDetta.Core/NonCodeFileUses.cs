using System.Collections.Frozen;
using System.Xml;
using System.Xml.Linq;

namespace TestDetta.Core;

/// <summary>
/// Tells whether a project's build uses one of its non-code files (docs/design/non-code-files.md).
/// MSBuild's evaluation says which files are <c>None</c> items the build does not copy and no known
/// build item type lists (<see cref="ProjectNode.InertItems"/>); this class adds what only the
/// repository's MSBuild files show: an item of another type whose pattern can match the file (a custom
/// item a target processes, a <c>Protobuf</c> item), any mention of the file's name outside pack
/// metadata (a target that reads it, a property function that does), and a target that walks every
/// <c>None</c> item. Every MSBuild file of the repository counts for every project, since an import may
/// be computed: a mention in another project's file costs only a broader run.
/// </summary>
/// <param name="repoRoot">Normalized repository root.</param>
internal sealed class NonCodeFileUses(string repoRoot)
{
    /// <summary>Extensions of the MSBuild files scanned.</summary>
    private static readonly FrozenSet<string> MsBuildExtensions =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".projitems", ".proj");

    /// <summary>Extensions of project files, whose relative paths start from their own folder.</summary>
    private static readonly FrozenSet<string> ProjectExtensions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".csproj", ".fsproj", ".vbproj");

    /// <summary>Directories that hold no MSBuild file of the repository.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".testdetta");

    /// <summary>
    /// Item types whose patterns are not scanned: those that name no file of the repository (packages,
    /// project references, usings, attributes) and those MSBuild's evaluation already reports with the
    /// files they include (<c>Compile</c>, <c>Content</c> and the other build types the evaluator lists).
    /// A property in one of their patterns would otherwise count as matching every file.
    /// </summary>
    private static readonly FrozenSet<string> UnscannedItems = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "None", "PackageReference", "PackageVersion", "GlobalPackageReference", "PackageDownload", "ProjectReference", "Reference",
        "COMReference", "Using", "InternalsVisibleTo", "FrameworkReference", "AssemblyAttribute", "AssemblyMetadata", "SupportedPlatform",
        "ProjectCapability", "Folder", "Service", "NuGetAuditSuppress", "TrimmerRootAssembly", "RuntimeHostConfigurationOption",
        "DotNetCliToolReference", "Analyzer", "Compile", "EmbeddedResource", "Content", "AdditionalFiles", "Resource", "Page",
        "ApplicationDefinition", "XamlAppDef", "Protobuf", "TypeScriptCompile", "ContentWithTargetPath", "EditorConfigFiles",
        "GlobalAnalyzerConfigFiles", "RazorComponent", "RazorGenerate", "AvaloniaResource", "AvaloniaXaml", "MauiImage", "MauiFont",
        "MauiAsset", "MauiXaml", "MauiCss", "MauiSplashScreen", "MauiIcon", "AndroidResource", "AndroidAsset", "BundleResource",
        "ImageAsset", "InterfaceDefinition", "EmbeddedFiles");

    /// <summary>The scan of the repository's MSBuild files, made on first use.</summary>
    private readonly Lazy<Scan> _scan = new(() => ScanRepository(repoRoot));

    /// <summary>
    /// Null when <paramref name="file"/> is inert in <paramref name="project"/>: the build neither
    /// compiles, embeds, copies, generates from nor reads it. Otherwise why it is a build input.
    /// </summary>
    /// <param name="project">The project that owns the file.</param>
    /// <param name="file">Normalized full path of the changed file.</param>
    public string? BuildUse(ProjectNode project, string file)
    {
        if (project.InertItems is not { } inert)
        {
            return "MSBuild did not report how the project uses its files";
        }

        if (!inert.Contains(file))
        {
            return "the project's build uses it (an item that is compiled, embedded, copied or given to a tool, or no item at all)";
        }

        var scan = _scan.Value;
        if (scan.Unreadable is { } unreadable)
        {
            return $"{unreadable} could not be read to see whether it uses the file";
        }

        var name = Path.GetFileName(file);
        if (scan.Mentions.FirstOrDefault(m => m.Names(file, name)) is { File: { } mentioning })
        {
            return $"{Relative(mentioning)} names it";
        }

        if (scan.Items.FirstOrDefault(i => i.Matches(file)) is { } item)
        {
            return $"the {item.Type} items of {Relative(item.File)} can include it";
        }

        return scan.WalksNone is { } walker ? $"a target in {Relative(walker)} processes every None item" : null;
    }

    /// <summary>A path relative to the repository root, for reasons.</summary>
    /// <param name="path">Normalized full path.</param>
    private string Relative(string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    /// <summary>Reads every MSBuild file of the repository.</summary>
    /// <param name="root">Repository root.</param>
    private static Scan ScanRepository(string root)
    {
        var scan = new Scan();
        foreach (var file in MsBuildFiles(root))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(file);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                scan.Unreadable ??= file;
                continue;
            }

            Add(scan, file, document);
        }

        return scan;
    }

    /// <summary>Adds what one MSBuild file can do with non-code files to <paramref name="scan"/>.</summary>
    /// <param name="scan">The scan so far.</param>
    /// <param name="file">The MSBuild file.</param>
    /// <param name="document">Its XML.</param>
    private static void Add(Scan scan, string file, XDocument document)
    {
        var directory = Path.GetDirectoryName(file)!;
        // Pack metadata and None items only describe the package or the file itself; everything else
        // that names a file may read it: targets, tasks, property functions, other item types.
        var mentions = new XDocument(document);
        mentions.Descendants().Where(static e => e.Name.LocalName == "None" || e.Name.LocalName.StartsWith("Package", StringComparison.Ordinal))
            .ToList()
            .ForEach(static e => e.Remove());
        mentions.DescendantNodes().OfType<XComment>().ToList().ForEach(static c => c.Remove());
        var values = mentions.Descendants().SelectMany(static e => e.Attributes().Select(static a => a.Value))
            .Concat(mentions.DescendantNodes().OfType<XText>().Select(static t => t.Value))
            .Where(static v => v.Length > 0)
            .ToList();
        scan.Mentions.Add(new Mention(file, values) { InProjectFile = ProjectExtensions.Contains(Path.GetExtension(file)) });

        foreach (var element in document.Descendants())
        {
            var local = element.Name.LocalName;
            if (local == "Target" && element.ToString(SaveOptions.DisableFormatting).Contains("@(None", StringComparison.OrdinalIgnoreCase))
            {
                scan.WalksNone ??= file;
            }

            if (element.Parent?.Name.LocalName != "ItemGroup" || UnscannedItems.Contains(local))
            {
                continue;
            }

            foreach (var attribute in (ReadOnlySpan<string>)["Include", "Update"])
            {
                if (element.Attribute(attribute)?.Value is { Length: > 0 } value)
                {
                    scan.Items.Add(new ItemPatterns(file, local, Patterns(directory, value)));
                }
            }
        }
    }

    /// <summary>
    /// The patterns of an item's <c>Include</c> or <c>Update</c>, as normalized full paths; one that
    /// uses a property, an item or metadata TestDetta cannot resolve becomes <c>**</c> under the root.
    /// </summary>
    /// <param name="directory">Directory of the MSBuild file, which relative patterns start from.</param>
    /// <param name="value">The attribute's value.</param>
    private static List<string> Patterns(string directory, string value)
    {
        var patterns = new List<string>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = part.Replace("$(MSBuildThisFileDirectory)", directory + "/", StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildProjectDirectory)", directory, StringComparison.OrdinalIgnoreCase)
                .Replace('\\', '/');
            patterns.Add(pattern.Contains("$(", StringComparison.Ordinal) || pattern.Contains("@(", StringComparison.Ordinal) || pattern.Contains("%(", StringComparison.Ordinal)
                ? "/**"
                : Paths.Normalize(Path.IsPathRooted(pattern) ? pattern : Path.Combine(directory, pattern)));
        }

        return patterns;
    }

    /// <summary>The MSBuild files under <paramref name="root"/>, outside build outputs and tool folders.</summary>
    /// <param name="root">Repository root.</param>
    private static IEnumerable<string> MsBuildFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> entries;
            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(directory)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                if (MsBuildExtensions.Contains(Path.GetExtension(entry)))
                {
                    yield return Paths.Normalize(entry);
                }
            }
        }
    }

    /// <summary>What the repository's MSBuild files can do with non-code files.</summary>
    private sealed class Scan
    {
        /// <summary>Each file's text without pack metadata and <c>None</c> items, to search for file names.</summary>
        public List<Mention> Mentions { get; } = [];

        /// <summary>Item elements of every other type, with their patterns.</summary>
        public List<ItemPatterns> Items { get; } = [];

        /// <summary>The first file with a target that uses <c>@(None)</c>, or null.</summary>
        public string? WalksNone { get; set; }

        /// <summary>The first MSBuild file that could not be read, or null.</summary>
        public string? Unreadable { get; set; }
    }

    /// <summary>The searchable text of an MSBuild file.</summary>
    /// <param name="File">The file.</param>
    /// <param name="Values">Its attribute values and texts, without comments, pack metadata and <c>None</c> items.</param>
    private sealed record Mention(string File, List<string> Values)
    {
        /// <summary>Characters that end a path inside an MSBuild value: lists, commands, property function arguments.</summary>
        private static readonly char[] Separators = [';', ' ', '\t', '\n', '\r', '"', '\'', ',', '(', ')', '=', '>', '<', '|'];

        /// <summary>
        /// True for a project file (<c>.csproj</c>, <c>.fsproj</c>, <c>.vbproj</c>), where a relative path is relative to the
        /// file itself. In an imported file it is relative to whichever project imports it, so there the name is enough.
        /// </summary>
        public bool InProjectFile { get; init; }

        /// <summary>
        /// True when a value can name <paramref name="file"/>. An imported file names it by its file name. A project file
        /// names it by a path that resolves to it from the project's folder, or by any path ending in its name that uses a
        /// property, item or metadata TestDetta cannot resolve (a README named in another project's file is another README).
        /// </summary>
        /// <param name="file">Normalized full path of the changed file.</param>
        /// <param name="name">Its file name.</param>
        public bool Names(string file, string name)
        {
            var directory = Path.GetDirectoryName(File)!;
            foreach (var value in Values)
            {
                if (!value.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!InProjectFile)
                {
                    return true;
                }

                var resolved = value.Replace("$(MSBuildThisFileDirectory)", directory + "/", StringComparison.OrdinalIgnoreCase)
                    .Replace("$(MSBuildProjectDirectory)", directory, StringComparison.OrdinalIgnoreCase);
                if (resolved.Contains("$(", StringComparison.Ordinal) || resolved.Contains("@(", StringComparison.Ordinal) || resolved.Contains("%(", StringComparison.Ordinal))
                {
                    return true;
                }

                foreach (var part in resolved.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = part.Replace('\\', '/');
                    if (path.EndsWith(name, StringComparison.OrdinalIgnoreCase)
                        && (path.Length == name.Length || path[^(name.Length + 1)] == '/')
                        && Paths.Comparer.Equals(Paths.Normalize(Path.IsPathRooted(path) ? path : Path.Combine(directory, path)), file))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>One item element's patterns.</summary>
    /// <param name="File">The MSBuild file it is in.</param>
    /// <param name="Type">The item type.</param>
    /// <param name="Patterns">Normalized full paths and patterns.</param>
    private sealed record ItemPatterns(string File, string Type, List<string> Patterns)
    {
        /// <summary>The patterns as a matcher, compiled on first use.</summary>
        private readonly Lazy<PathMatcher> _matcher = new(() => new PathMatcher(Patterns));

        /// <summary>True when the item can include <paramref name="file"/>.</summary>
        /// <param name="file">Normalized full path.</param>
        public bool Matches(string file) => Patterns.Contains("/**") || _matcher.Value.Matches(file);
    }
}
