using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TestDetta.Core;

/// <summary>
/// Whether a project's build uses the output of a project it references without compiling against it
/// (<c>ReferenceOutputAssembly="false"</c>), read from its MSBuild files without building
/// (docs/design/runtime-file-reads.md). When it does not, the reference only orders the build or makes
/// the output available at run time, and the tests that started or loaded the output are the ones it
/// can affect. The check reads the consumer's project file and the repository files it imports
/// (<c>Directory.Build.props</c>/<c>.targets</c> above it and their <c>Import</c>s); an import it cannot
/// resolve makes it read every MSBuild file of the repository instead. It errs towards "uses": a
/// target that names the referenced project or iterates the project references, an
/// <c>OutputItemType</c> on the reference, or a compile input that names its output.
/// </summary>
internal static partial class BuildOutputUse
{
    /// <summary>Item types whose files the compiler reads: one that names the output puts it into the build.</summary>
    private static readonly HashSet<string> CompileItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "EmbeddedResource", "AdditionalFiles", "Analyzer", "Reference",
    };

    /// <summary>Extensions of the repository files an import may name.</summary>
    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".props", ".targets", ".proj", ".projitems", ".msbuild",
    };

    /// <summary>Directories that hold no MSBuild input.</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".testdetta",
    };

    /// <summary>
    /// Why the build of <paramref name="consumer"/> uses the output of <paramref name="referenced"/>,
    /// or null when it does not.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="consumer">Project that references <paramref name="referenced"/> without compiling against it.</param>
    /// <param name="referenced">The referenced project.</param>
    public static string? Find(string repoRoot, ProjectNode consumer, ProjectNode referenced)
    {
        var names = new[] { referenced.AssemblyName, Path.GetFileNameWithoutExtension(referenced.Path) }
            .Where(static n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (referenced.AssemblyNameUnresolved)
        {
            return $"the assembly name of {Path.GetFileName(referenced.Path)} is known only to MSBuild";
        }

        foreach (var file in Files(repoRoot, consumer, referenced))
        {
            if (Load(file) is not { } document)
            {
                return $"{Relative(repoRoot, file)} could not be read";
            }

            if (Use(document, names) is { } use)
            {
                return $"{Relative(repoRoot, file)} {use}";
            }
        }

        return null;
    }

    /// <summary>What in <paramref name="document"/> uses an output named <paramref name="names"/>, or null.</summary>
    /// <param name="document">An MSBuild file.</param>
    /// <param name="names">Assembly and project names of the referenced project.</param>
    private static string? Use(XDocument document, List<string> names)
    {
        foreach (var element in document.Descendants())
        {
            var local = element.Name.LocalName;

            // The output becomes items of that type, which some target consumes.
            if (local == "ProjectReference" && OutputItemType(element) is { Length: > 0 } type
                && !string.Equals(type, "Analyzer", StringComparison.OrdinalIgnoreCase)
                && (element.Parent?.Name.LocalName == "ItemDefinitionGroup" || Names(Value(element, "Include") + " " + Value(element, "Update"), names)))
            {
                return $"gives the reference OutputItemType=\"{type}\"";
            }

            if (local == "Target")
            {
                var text = string.Concat(element.DescendantsAndSelf().SelectMany(static e => e.Attributes().Select(static a => a.Value + " ").Prepend(e.Name.LocalName + " ")))
                           + element.Value;
                if (text.Contains("ProjectReference", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("_ResolvedProjectReferencePaths", StringComparison.OrdinalIgnoreCase))
                {
                    return $"has a target ({Value(element, "Name")}) that goes through the project references";
                }

                if (Names(text, names))
                {
                    return $"has a target ({Value(element, "Name")}) that names {names[0]}";
                }

                continue;
            }

            // Items outside targets: a compile input that names the output; packing and copying do not build with it.
            if (CompileItems.Contains(local) && element.Ancestors().All(static a => a.Name.LocalName != "Target")
                && Names(Value(element, "Include") + " " + Value(element, "Update"), names))
            {
                return $"compiles with {names[0]}'s output ({local})";
            }
        }

        return null;
    }

    /// <summary>The <c>OutputItemType</c> of a project reference, as an attribute or a child element.</summary>
    /// <param name="element">A <c>ProjectReference</c> item.</param>
    private static string? OutputItemType(XElement element) =>
        (element.Attribute("OutputItemType")?.Value
         ?? element.Elements().FirstOrDefault(static e => e.Name.LocalName == "OutputItemType")?.Value)?.Trim();

    /// <summary>An attribute's value, or an empty string.</summary>
    /// <param name="element">Element.</param>
    /// <param name="name">Attribute name.</param>
    private static string Value(XElement element, string name) => element.Attribute(name)?.Value ?? string.Empty;

    /// <summary>
    /// True when <paramref name="text"/> has one of <paramref name="names"/> as a name of its own: not
    /// inside a longer name (a letter, digit, '_', '-' or a '.' before it, or a letter, digit, '_' or '-'
    /// after it). A file name such as <c>Helper.dll</c> or <c>Helper.csproj</c> counts.
    /// </summary>
    /// <param name="text">Text to search.</param>
    /// <param name="names">Names to find.</param>
    private static bool Names(string text, IReadOnlyList<string> names) =>
        names.Any(name => Regex.IsMatch(text, $@"(?<![\w.-]){Regex.Escape(name)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>
    /// The consumer's project file and the repository files it imports, or, when an import cannot be
    /// resolved, every MSBuild file of the repository except other projects' own files and the referenced project's.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="consumer">Consumer project.</param>
    /// <param name="referenced">Referenced project, whose own file runs in its own build.</param>
    private static List<string> Files(string repoRoot, ProjectNode consumer, ProjectNode referenced)
    {
        var root = Paths.Normalize(Path.GetFullPath(repoRoot));
        var files = new List<string>();
        var seen = new HashSet<string>(Paths.Comparer);
        var queue = new Queue<string>();
        queue.Enqueue(consumer.Path);
        foreach (var implicitFile in (string[])["Directory.Build.props", "Directory.Build.targets"])
        {
            if (FileAbove(implicitFile, consumer.Directory, root) is { } found)
            {
                queue.Enqueue(found);
            }
        }

        while (queue.TryDequeue(out var file))
        {
            if (!seen.Add(file))
            {
                continue;
            }

            files.Add(file);
            if (Load(file) is not { } document)
            {
                continue;
            }

            foreach (var import in document.Descendants().Where(static e => e.Name.LocalName == "Import"))
            {
                var resolved = Resolve(Value(import, "Project"), file, consumer, root);
                if (resolved is null)
                {
                    return EveryFile(root, consumer, referenced);
                }

                foreach (var target in resolved.Where(f => Paths.IsUnder(f, root)))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return files;
    }

    /// <summary>
    /// The files an <c>Import</c>'s <c>Project</c> names; empty for a file outside the repository or one
    /// that does not exist (an SDK's, a package's), null when it holds a property this check cannot expand.
    /// </summary>
    /// <param name="project">The attribute's value.</param>
    /// <param name="importing">The importing file.</param>
    /// <param name="consumer">The project being evaluated.</param>
    /// <param name="root">Repository root.</param>
    private static List<string>? Resolve(string project, string importing, ProjectNode consumer, string root)
    {
        var directory = Path.GetDirectoryName(importing)! + Path.DirectorySeparatorChar;
        var text = project
            .Replace("$(MSBuildThisFileDirectory)", directory, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", consumer.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        text = FileAboveCall().Replace(text, m =>
        {
            var start = m.Groups["dir"].Value.Replace("$(MSBuildThisFileDirectory)", directory, StringComparison.OrdinalIgnoreCase);
            return FileAbove(m.Groups["name"].Value, Path.GetFullPath(start.Length > 0 ? start : directory), root) ?? string.Empty;
        });

        // An SDK import, or one by a property only MSBuild knows.
        if (text.Contains("$(", StringComparison.Ordinal))
        {
            return Sdk(text) ? [] : null;
        }

        if (text.Trim().Length == 0)
        {
            return [];
        }

        var full = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(directory, text.Replace('\\', Path.DirectorySeparatorChar)));
        if (full.Contains('*', StringComparison.Ordinal))
        {
            var folder = Path.GetDirectoryName(full)!;
            return Directory.Exists(folder) && !Path.GetDirectoryName(folder)!.Contains('*', StringComparison.Ordinal)
                ? [.. Directory.GetFiles(folder, Path.GetFileName(full)).Select(Paths.Normalize)]
                : null;
        }

        return File.Exists(full) ? [Paths.Normalize(full)] : [];
    }

    /// <summary>True for the SDK's and NuGet's own import paths, which hold no repository file.</summary>
    /// <param name="text">An import path with properties left.</param>
    private static bool Sdk(string text) =>
        text.Contains("$(MSBuildExtensionsPath", StringComparison.OrdinalIgnoreCase)
        || text.Contains("$(MSBuildSDKsPath", StringComparison.OrdinalIgnoreCase)
        || text.Contains("$(MSBuildToolsPath", StringComparison.OrdinalIgnoreCase)
        || text.Contains("$(MSBuildBinPath", StringComparison.OrdinalIgnoreCase)
        || text.Contains("$(NuGetPackageRoot", StringComparison.OrdinalIgnoreCase)
        || text.Contains("$(MSBuildProjectExtensionsPath", StringComparison.OrdinalIgnoreCase);

    /// <summary>The nearest <paramref name="name"/> in <paramref name="directory"/> or above it, within the repository.</summary>
    /// <param name="name">File name.</param>
    /// <param name="directory">Where to start.</param>
    /// <param name="root">Repository root.</param>
    private static string? FileAbove(string name, string directory, string root)
    {
        for (var current = Paths.Normalize(Path.GetFullPath(directory)); Paths.IsUnder(current, root); current = Paths.Normalize(Path.GetDirectoryName(current)!))
        {
            var candidate = Path.Combine(current, name);
            if (File.Exists(candidate))
            {
                return Paths.Normalize(candidate);
            }

            if (current.Length <= root.Length)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>Every MSBuild file of the repository that may be imported, and the consumer's own file.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="consumer">Consumer project.</param>
    /// <param name="referenced">Referenced project.</param>
    private static List<string> EveryFile(string root, ProjectNode consumer, ProjectNode referenced)
    {
        var files = new List<string> { consumer.Path };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var sub in Directory.EnumerateDirectories(directory).Where(d => !SkippedDirectories.Contains(Path.GetFileName(d))))
            {
                pending.Push(sub);
            }

            files.AddRange(Directory.EnumerateFiles(directory).Select(Paths.Normalize).Where(f =>
                ImportableExtensions.Contains(Path.GetExtension(f)) && !string.Equals(f, referenced.Path, Paths.Comparison)));
        }

        return files;
    }

    /// <summary>Parses an MSBuild file; null when it is not XML.</summary>
    /// <param name="file">Full path.</param>
    private static XDocument? Load(string file)
    {
        try
        {
            return XDocument.Load(file);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A path relative to the repository, with '/' separators.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="file">Full path.</param>
    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary><c>$([MSBuild]::GetPathOfFileAbove('name', 'dir'))</c>, the usual way to chain <c>Directory.Build</c> files.</summary>
    [GeneratedRegex(@"\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'?(?<name>[^',)]+)'?\s*(,\s*'?(?<dir>[^')]*)'?\s*)?\)\)", RegexOptions.IgnoreCase)]
    private static partial Regex FileAboveCall();
}
