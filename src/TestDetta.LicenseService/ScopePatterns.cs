using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// Repository patterns as people type them (a checkout field, the trial form, the admin command), turned into
/// the scopes a license carries, which TestDetta matches against <c>host/path</c> identities
/// (<see cref="RepositoryIdentity.Matches"/>). People write URLs, SSH remotes, <c>.git</c> endings and bare
/// owners; each is reduced to the form that matches what they meant, since a scope that matches nothing
/// silently makes a paid license run every test:
/// <list type="bullet">
///   <item><c>https://github.com/Acme/App.git</c> and <c>git@github.com:acme/app.git</c> become <c>github.com/acme/app</c>;</item>
///   <item>a host and owner alone (<c>github.com/acme</c>) become <c>github.com/acme/*</c>, which also matches the
///   bare <c>github.com/acme</c>, so nothing the plain pattern matched is lost.</item>
/// </list>
/// </summary>
/// <param name="Patterns">The scopes, lower-cased, in the order given, without repeats.</param>
/// <param name="Owners">The owner keys (host and owner) the patterns name, without repeats; empty in <see cref="ScopeRules.Admin"/> mode when a pattern names a whole host.</param>
/// <param name="Rejected">What could not be read as a pattern, as typed.</param>
public sealed record ScopePatterns(IReadOnlyList<string> Patterns, IReadOnlyList<string> Owners, IReadOnlyList<string> Rejected)
{
    /// <summary>Longest pattern accepted.</summary>
    public const int MaxPatternLength = 200;

    /// <summary>
    /// Reads patterns separated by commas, spaces or new lines. Each one is normalised; one that cannot be read
    /// under <paramref name="rules"/> goes to <see cref="Rejected"/> and the others are kept.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <param name="rules">Which patterns are allowed.</param>
    public static ScopePatterns Parse(string? text, ScopeRules rules)
    {
        var patterns = new List<string>();
        var owners = new List<string>();
        var rejected = new List<string>();
        foreach (var raw in (text ?? "").Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Normalize(raw, rules) is not var (pattern, owner))
            {
                rejected.Add(raw);
                continue;
            }

            if (!patterns.Contains(pattern))
            {
                patterns.Add(pattern);
            }

            if (owner is not null && !owners.Contains(owner))
            {
                owners.Add(owner);
            }
        }

        return new ScopePatterns(patterns, owners, rejected);
    }

    /// <summary>The scope <paramref name="raw"/> stands for and its owner key, or null when it is not a pattern under <paramref name="rules"/>.</summary>
    /// <param name="raw">One pattern as typed.</param>
    /// <param name="rules">Which patterns are allowed.</param>
    private static (string Pattern, string? Owner)? Normalize(string raw, ScopeRules rules)
    {
        var text = raw.Trim();

        // A URL or an SSH remote: the identity TestDetta derives from the same remote, credentials dropped.
        // A wildcard survives (https://github.com/acme/* keeps its /*).
        if (text.Contains("://", StringComparison.Ordinal) || text.Contains('@', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal))
        {
            if (RepositoryIdentity.Normalize(text) is not { } identity)
            {
                return null;
            }

            text = identity;
        }

        text = text.ToLowerInvariant().TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.Ordinal))
        {
            text = text[..^4].TrimEnd('/');
        }

        if (text.Length is 0 or > MaxPatternLength || text.StartsWith('/') || text.Contains("//", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '~' or '/' or '*'))
            {
                return null;
            }
        }

        var segments = text.Split('/');
        var host = segments[0];
        if (host.Contains('*', StringComparison.Ordinal) || host.StartsWith('.') || host.EndsWith('.'))
        {
            return null;
        }

        if (rules == ScopeRules.Admin)
        {
            // An admin may license a whole internal git server (git.acme.corp/*) or a host without a dot.
            // A host alone means the whole server, as a host and owner alone mean the whole owner.
            var adminOwner = segments.Length >= 2 && segments[1].Length > 0 && !segments[1].Contains('*', StringComparison.Ordinal) ? $"{host}/{segments[1]}" : null;
            return segments.Length == 1 || (segments.Length == 2 && adminOwner is not null) ? ($"{text}/*", adminOwner) : (text, adminOwner);
        }

        // Customers name an owner: a host with a dot and an owner without wildcards, so no pattern covers a whole host.
        if (segments.Length < 2 || segments[1].Length == 0 || !host.Contains('.', StringComparison.Ordinal) || segments[1].Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        var owner = $"{host}/{segments[1]}";
        return segments.Length == 2 ? ($"{text}/*", owner) : (text, owner);
    }
}

/// <summary>Which repository patterns <see cref="ScopePatterns.Parse"/> allows.</summary>
public enum ScopeRules
{
    /// <summary>Patterns a customer types at checkout or in the trial form: a host with a dot and a named owner.</summary>
    Customer,

    /// <summary>Patterns an admin gives: any host, including a whole internal git server.</summary>
    Admin,
}
