using System.Text.RegularExpressions;

namespace TestDetta.Core.Licensing;

/// <summary>A commit's author as git reports it with <c>.mailmap</c> applied.</summary>
/// <param name="AuthorTime">Author date: when the work was written, kept by merges, rebases and cherry-picks.</param>
/// <param name="Email">Author email.</param>
/// <param name="Name">Author name.</param>
public sealed record CommitAuthor(DateTimeOffset AuthorTime, string Email, string Name);

/// <summary>
/// Counts active committers: distinct non-bot authors with a commit whose author date falls within
/// the last 30 days (docs/design/licensing.md). Old work merged now keeps its author dates, so people
/// who wrote it long ago do not count. Everything here runs locally; the identities never leave.
/// </summary>
/// <param name="authors">Authors of the commits reachable from the analysed HEAD, from at least the last 60 days.</param>
/// <param name="extraBots">The customer's own bot patterns (<c>*</c> wildcards), in addition to the built-in ones.</param>
public sealed class ActiveCommitters(IReadOnlyList<CommitAuthor> authors, IReadOnlyList<string> extraBots)
{
    /// <summary>Length of the window an author counts as active in.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Built-in bot patterns, matched against the name and the email's local part.</summary>
    public static readonly IReadOnlyList<string> BuiltInBots =
    [
        "*[bot]*", "dependabot*", "renovate*", "github-actions*", "project_*_bot*", "group_*_bot*",
        "jenkins*", "buildbot*", "gitlab-ci*", "azure-pipelines*", "teamcity*",
    ];

    /// <summary>The bot patterns in use, as regular expressions.</summary>
    private readonly Regex[] _bots = [.. BuiltInBots.Concat(extraBots).Select(ToRegex)];

    /// <summary>Distinct non-bot authors active in the window that ends at <paramref name="end"/>, by folded email.</summary>
    /// <param name="end">End of the window.</param>
    public IReadOnlySet<string> At(DateTimeOffset end)
    {
        var start = end - Window;
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var author in authors)
        {
            if (author.AuthorTime > start && author.AuthorTime <= end && !IsBot(author))
            {
                active.Add(author.Email.Trim());
            }
        }

        return active;
    }

    /// <summary>
    /// For how many whole days, ending at <paramref name="now"/>, the active count has been above
    /// <paramref name="allowed"/> without a break: 0 when it is not above now. Computed from the history
    /// alone, so every CI (ephemeral runners included) gets the same answer without stored state.
    /// Stops counting at <paramref name="limit"/> days.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="allowed">Active committers allowed, tolerance included.</param>
    /// <param name="limit">Most days to look back.</param>
    public int DaysOver(DateTimeOffset now, int allowed, int limit)
    {
        var days = 0;
        while (days < limit && At(now - TimeSpan.FromDays(days)).Count > allowed)
        {
            days++;
        }

        return days;
    }

    /// <summary>True for a bot: its name or the local part of its email matches a bot pattern.</summary>
    /// <param name="author">A commit author.</param>
    private bool IsBot(CommitAuthor author)
    {
        var local = author.Email.Split('@')[0];
        return Array.Exists(_bots, bot => bot.IsMatch(author.Name) || bot.IsMatch(local) || bot.IsMatch(author.Email));
    }

    /// <summary>A case-insensitive regular expression for a <c>*</c> wildcard pattern over the whole text.</summary>
    /// <param name="pattern">Pattern.</param>
    private static Regex ToRegex(string pattern) =>
        new($"^{Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
