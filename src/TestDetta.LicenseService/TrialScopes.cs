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

    /// <summary>
    /// Reads patterns separated by commas, spaces or new lines, as the checkout field takes them, normalised by
    /// <see cref="ScopePatterns"/> (a URL or SSH remote becomes <c>host/path</c>, a bare owner gets <c>/*</c>).
    /// Null when any pattern is malformed, names no owner, puts a wildcard in the host or owner, or there are
    /// none or too many: the form then asks for them again.
    /// </summary>
    /// <param name="text">What the visitor typed.</param>
    public static TrialScopes? Parse(string? text)
    {
        var parsed = ScopePatterns.Parse(text, ScopeRules.Customer);
        return parsed.Rejected.Count == 0 && parsed.Patterns.Count is > 0 and <= MaxPatterns
            ? new TrialScopes(parsed.Patterns, parsed.Owners)
            : null;
    }
}
