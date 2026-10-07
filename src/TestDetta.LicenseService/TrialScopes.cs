namespace TestDetta.LicenseService;

/// <summary>
/// The repositories a trial asks for, checked before anything is stored. A trial covers named owners only:
/// every pattern starts with a host and an owner without wildcards (<c>github.com/acme/*</c>,
/// <c>git.acme.corp/team/app</c>), so no trial can cover a whole host. The owner key
/// (<c>github.com/acme</c>) is what "one trial per scope" counts.
/// </summary>
/// <param name="Patterns">The patterns, lower-cased, in the order given, without repeats.</param>
/// <param name="Owners">The owner keys (host and owner) the patterns name, without repeats.</param>
public sealed record TrialScopes(IReadOnlyList<string> Patterns, IReadOnlyList<string> Owners)
{
    /// <summary>Most patterns one trial may name.</summary>
    public const int MaxPatterns = 10;

    /// <summary>Longest pattern accepted.</summary>
    public const int MaxPatternLength = 200;

    /// <summary>
    /// Reads patterns separated by commas, spaces or new lines, as the checkout field takes them. Null when
    /// any pattern is malformed, names no owner, puts a wildcard in the host or owner, or there are none or
    /// too many: the form then asks for them again.
    /// </summary>
    /// <param name="text">What the visitor typed.</param>
    public static TrialScopes? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var patterns = new List<string>();
        var owners = new List<string>();
        foreach (var raw in text.Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Trim().ToLowerInvariant();
            if (Owner(pattern) is not { } owner)
            {
                return null;
            }

            if (!patterns.Contains(pattern))
            {
                patterns.Add(pattern);
            }

            if (!owners.Contains(owner))
            {
                owners.Add(owner);
            }
        }

        return patterns.Count is > 0 and <= MaxPatterns ? new TrialScopes(patterns, owners) : null;
    }

    /// <summary>The owner key of <paramref name="pattern"/>, or null when it does not name one.</summary>
    /// <param name="pattern">A lower-cased pattern.</param>
    private static string? Owner(string pattern)
    {
        if (pattern.Length > MaxPatternLength || pattern.StartsWith('/') || pattern.Contains("//", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var c in pattern)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '~' or '/' or '*'))
            {
                return null;
            }
        }

        var segments = pattern.Split('/');
        if (segments.Length < 2 || segments[1].Length == 0)
        {
            return null;
        }

        var (host, owner) = (segments[0], segments[1]);
        var named = host.Contains('.', StringComparison.Ordinal) && !host.StartsWith('.') && !host.EndsWith('.')
                    && !host.Contains('*', StringComparison.Ordinal) && !owner.Contains('*', StringComparison.Ordinal);
        return named ? $"{host}/{owner}" : null;
    }
}
