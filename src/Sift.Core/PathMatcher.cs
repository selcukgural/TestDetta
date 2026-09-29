namespace Sift.Core;

/// <summary>Matches paths against a list of exact paths and wildcard patterns.</summary>
/// <param name="entries">Normalized paths or patterns.</param>
internal sealed class PathMatcher(IReadOnlyList<string> entries)
{
    /// <summary>Exact paths.</summary>
    private readonly HashSet<string> _paths = new(entries.Where(static e => !Glob.IsPattern(e)), Paths.Comparer);

    /// <summary>Compiled patterns.</summary>
    private readonly System.Text.RegularExpressions.Regex[] _patterns = [.. entries.Where(Glob.IsPattern).Select(Glob.Compile)];

    /// <summary>True when <paramref name="path"/> is one of the paths or matches one of the patterns.</summary>
    /// <param name="path">Normalized path.</param>
    public bool Matches(string path)
    {
        if (_paths.Contains(path))
        {
            return true;
        }

        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }
}
