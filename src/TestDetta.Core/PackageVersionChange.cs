using System.Xml;
using System.Xml.Linq;

namespace TestDetta.Core;

/// <summary>
/// What changed in a <c>Directory.Packages.props</c> (central package management) between two versions.
/// </summary>
/// <param name="Path">File path relative to the repository root.</param>
/// <param name="Packages">
/// Packages whose central version was added, removed or changed, compared case-insensitively.
/// Null when the change cannot be narrowed to packages: anything besides <c>PackageVersion</c> items
/// changed, a <c>GlobalPackageReference</c> is involved, or transitive pinning is on (then a version
/// also reaches projects that only depend on the package indirectly, which only a restore could tell).
/// </param>
public sealed record PackageVersionChange(string Path, IReadOnlySet<string>? Packages)
{
    /// <summary>File name of the central package versions file.</summary>
    public const string FileName = "Directory.Packages.props";

    /// <summary>Compares two versions of a <c>Directory.Packages.props</c>.</summary>
    /// <param name="path">File path relative to the repository root.</param>
    /// <param name="oldContent">Content at the base, or null when the file was added.</param>
    /// <param name="newContent">Content in the working tree, or null when the file was deleted.</param>
    public static PackageVersionChange Compare(string path, string? oldContent, string? newContent)
    {
        if (Parse(oldContent) is not { } before || Parse(newContent) is not { } after)
        {
            return new PackageVersionChange(path, null);
        }

        if (before.Rest != after.Rest || before.TransitivePinning || after.TransitivePinning)
        {
            return new PackageVersionChange(path, null);
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, version) in before.Versions)
        {
            if (!after.Versions.TryGetValue(package, out var newVersion) || newVersion != version)
            {
                changed.Add(package);
            }
        }

        foreach (var package in after.Versions.Keys)
        {
            if (!before.Versions.ContainsKey(package))
            {
                changed.Add(package);
            }
        }

        return new PackageVersionChange(path, changed);
    }

    /// <summary>The parts of the file that matter for narrowing.</summary>
    /// <param name="Versions">Package to version (including its condition, if any).</param>
    /// <param name="Rest">Everything else, whitespace and comments removed, for an exact comparison.</param>
    /// <param name="TransitivePinning">True when the file turns on <c>CentralPackageTransitivePinningEnabled</c>.</param>
    private sealed record Parsed(Dictionary<string, string> Versions, string Rest, bool TransitivePinning);

    /// <summary>Reads a file; null when it is missing, not XML, or contains global package references.</summary>
    /// <param name="content">File content, or null.</param>
    private static Parsed? Parse(string? content)
    {
        if (content is null)
        {
            return null;
        }

        XDocument document;
        try
        {
            // Content read from git keeps a UTF-8 byte order mark, which XML parsing rejects.
            document = XDocument.Parse(content.TrimStart('\uFEFF'), LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }

        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Descendants().ToList())
        {
            switch (element.Name.LocalName)
            {
                case "GlobalPackageReference":
                    return null;

                case "PackageVersion" when element.Attribute("Include")?.Value is { Length: > 0 } package:
                    versions[package] = $"{element.Attribute("Version")?.Value}|{element.Attribute("Condition")?.Value}|{element.Attribute("VersionOverride")?.Value}";
                    element.Remove();
                    break;
            }
        }

        var pinning = document.Descendants().Any(static e =>
            e.Name.LocalName == "CentralPackageTransitivePinningEnabled"
            && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        foreach (var comment in document.DescendantNodes().OfType<XComment>().ToList())
        {
            comment.Remove();
        }

        return new Parsed(versions, document.ToString(SaveOptions.DisableFormatting), pinning);
    }
}
