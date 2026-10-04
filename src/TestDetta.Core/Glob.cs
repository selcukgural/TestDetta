using System.Text;
using System.Text.RegularExpressions;

namespace TestDetta.Core;

/// <summary>
/// Matches paths against MSBuild item patterns: <c>*</c> within a directory, <c>**</c> across
/// directories, <c>?</c> for one character. Patterns and paths are normalized absolute paths.
/// </summary>
internal static class Glob
{
    /// <summary>True when <paramref name="pattern"/> contains a wildcard.</summary>
    /// <param name="pattern">Normalized path or pattern.</param>
    public static bool IsPattern(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>A regular expression that matches exactly the paths <paramref name="pattern"/> includes.</summary>
    /// <param name="pattern">Normalized path or pattern.</param>
    public static Regex Compile(string pattern)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // "**/" matches any number of directories, including none.
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                regex.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
                continue;
            }

            regex.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(c.ToString()),
            });
        }

        regex.Append('$');
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(regex.ToString(), options);
    }
}
