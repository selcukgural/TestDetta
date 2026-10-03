using System.Text.RegularExpressions;

namespace Sift.Core.Licensing;

/// <summary>
/// The identity a license scope is matched against: the <c>origin</c> remote URL reduced to
/// <c>host/path</c>, whatever CI or git server is used. <c>https://user:token@github.com/Acme/App.git</c>,
/// <c>git@github.com:Acme/App.git</c> and <c>ssh://git@github.com:22/Acme/App</c> all give
/// <c>github.com/acme/app</c>. Credentials are dropped first and never kept or logged.
/// </summary>
public static partial class RepositoryIdentity
{
    /// <summary>The normalised identity of a remote URL, or null when it is not one.</summary>
    /// <param name="remoteUrl">A git remote URL in any form git accepts.</param>
    public static string? Normalize(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var url = remoteUrl.Trim();
        string host;
        string path;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                return null;
            }

            // Uri keeps user info apart; it is simply not used. The port is not part of the identity.
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else if (ScpLike().Match(url) is { Success: true } scp)
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else
        {
            // A local path (a clone of a clone) has no host to license.
            return null;
        }

        path = Uri.UnescapeDataString(path).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return path.Length == 0 ? null : $"{host}/{path}".ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="identity"/> matches the scope <paramref name="pattern"/>: case-insensitive,
    /// <c>*</c> matches within one path segment and a trailing <c>/*</c> also matches everything below.
    /// <c>github.com/acme/*</c> matches <c>github.com/acme/app</c> and <c>github.com/acme/group/app</c>.
    /// </summary>
    /// <param name="identity">A normalised identity.</param>
    /// <param name="pattern">A scope from a license.</param>
    public static bool Matches(string identity, string pattern)
    {
        var normalized = pattern.Trim().Trim('/').ToLowerInvariant();
        var regex = Regex.Escape(normalized).Replace(@"\*", "[^/]*", StringComparison.Ordinal);
        if (regex.EndsWith("/[^/]*", StringComparison.Ordinal))
        {
            regex = regex[..^"/[^/]*".Length] + "(/.+)?";
        }

        return Regex.IsMatch(identity, $"^{regex}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary><c>[user@]host:path</c>, git's scp-like syntax for SSH remotes.</summary>
    [GeneratedRegex(@"^(?:[^@/]+@)?(?<host>[^:/]+):(?<path>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLike();
}
