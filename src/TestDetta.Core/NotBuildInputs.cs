using System.Collections.Frozen;

namespace TestDetta.Core;

/// <summary>
/// Changed non-code files that the scan of the repository's MSBuild files does not take as build inputs on a guess
/// (docs/design/non-code-files.md, "Documentation files"): documentation by its well-known name, and the files the team
/// names with <c>--not-build-inputs</c>. Only the scan's guesses are skipped, an item pattern that could include the
/// file or a target that walks every <c>None</c> item; MSBuild's evaluation building, embedding or copying the file,
/// a target naming it, and a test reading it at run time still count.
/// </summary>
/// <param name="repoRoot">Normalized repository root.</param>
/// <param name="patterns">The team's patterns (<c>--not-build-inputs</c>), repository-relative; MSBuild-style wildcards.</param>
internal sealed class NotBuildInputs(string repoRoot, IReadOnlyList<string> patterns)
{
    /// <summary>
    /// File names, without the extension, that are documentation in practically every repository: OpenTelemetry's
    /// pull requests each add a <c>CHANGELOG.md</c> entry, and its targets hold items the scan cannot resolve.
    /// </summary>
    private static readonly FrozenSet<string> DocumentationNames = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "README", "CHANGELOG", "CHANGES", "HISTORY", "RELEASENOTES", "RELEASE-NOTES", "LICENSE", "LICENCE", "NOTICE",
        "THIRD-PARTY-NOTICES", "CONTRIBUTING", "SECURITY", "CODE_OF_CONDUCT", "CODEOWNERS", "SUPPORT", "AUTHORS", "CLAUDE", "AGENTS");

    /// <summary>Extensions a documentation name may carry; a file without an extension counts too (<c>LICENSE</c>).</summary>
    private static readonly FrozenSet<string> DocumentationExtensions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "", ".md", ".txt", ".rst", ".adoc");

    /// <summary>The team's patterns as given (repository-relative, '/' separators), each with its matcher.</summary>
    private readonly List<(string Pattern, PathMatcher Matcher)> _patterns = [.. patterns
        .Select(static p => p.Replace('\\', '/').TrimStart('/'))
        .Where(static p => p.Length > 0)
        .Select(p => (p, new PathMatcher([Paths.Normalize(Path.Combine(repoRoot, p))])))];

    /// <summary>
    /// Why the scan's guesses do not make <paramref name="file"/> a build input, or null when they do: a
    /// documentation name, or the team's pattern that matches it.
    /// </summary>
    /// <param name="file">Normalized full path of the changed file.</param>
    public string? Reason(string file)
    {
        var name = Path.GetFileName(file);
        if (DocumentationNames.Contains(Path.GetFileNameWithoutExtension(name)) && DocumentationExtensions.Contains(Path.GetExtension(name)))
        {
            return "a documentation file";
        }

        foreach (var (pattern, matcher) in _patterns)
        {
            if (matcher.Matches(file))
            {
                return $"named by --not-build-inputs {pattern}";
            }
        }

        return null;
    }
}
