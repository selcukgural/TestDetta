namespace TestDetta.Core.Licensing;

/// <summary>How a license is renewed and how much slack its validity includes (docs/design/licensing.md).</summary>
public enum LicenseKind
{
    /// <summary>Renewed every billing period through the refresh endpoint; valid for the period plus 7 days.</summary>
    Subscription,

    /// <summary>For customers without outbound network; delivered as a string or file, valid for the term plus 30 days.</summary>
    Annual,

    /// <summary>A 30-day trial.</summary>
    Trial,
}

/// <summary>What a license grants, as signed by the license service.</summary>
/// <param name="Id">License id, such as <c>L-2026-0042</c>.</param>
/// <param name="KeyId">Id of the signing key, to find the public key that verifies it.</param>
/// <param name="Plan">Plan name, for display.</param>
/// <param name="Kind">How it is renewed.</param>
/// <param name="Licensee">Customer name, for display.</param>
/// <param name="Seats">Active committers it covers.</param>
/// <param name="Scopes">Patterns over normalised remote URLs (<see cref="RepositoryIdentity"/>) it applies to.</param>
/// <param name="ValidFrom">Start of validity.</param>
/// <param name="ValidUntil">End of validity, slack included.</param>
/// <param name="Refresh">Where a newer version can be fetched; null for none.</param>
public sealed record LicenseClaims(
    string Id,
    string KeyId,
    string Plan,
    LicenseKind Kind,
    string Licensee,
    int Seats,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    Uri? Refresh)
{
    /// <summary>
    /// When this version was signed. Of several genuine versions of the same license (the CI secret,
    /// a refreshed copy), the newest one counts: a seat reduction is a newer license with fewer seats.
    /// </summary>
    public DateTimeOffset Issued { get; init; } = ValidFrom;
}
