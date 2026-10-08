using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// What the service knows about one license: the source of truth from which every license string is
/// signed. Payment webhooks and the admin command change it; the refresh endpoint signs its current state.
/// </summary>
/// <param name="Id">License id, such as <c>L-2026-0042</c>.</param>
/// <param name="Licensee">Customer name.</param>
/// <param name="Plan">Plan name.</param>
/// <param name="Kind">How it is renewed; decides the slack added to <paramref name="PeriodEnd"/>.</param>
/// <param name="Seats">Active committers covered.</param>
/// <param name="Scopes">Repository scopes (docs/design/licensing.md).</param>
/// <param name="ValidFrom">Start of validity.</param>
/// <param name="PeriodEnd">End of the paid period or term, before slack.</param>
/// <param name="Revoked">True after a cancellation that ends it early, or a refund: no new license is issued.</param>
/// <param name="Source">Where it came from: <c>manual</c>, <c>polar</c> or <c>paddle</c>.</param>
public sealed record LicenseRecord(
    string Id,
    string Licensee,
    string Plan,
    LicenseKind Kind,
    int Seats,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ValidFrom,
    DateTimeOffset PeriodEnd,
    bool Revoked,
    string Source)
{
    /// <summary>
    /// True when an admin set <see cref="Scopes"/> (<c>admin update --scope</c>): a payment provider's later
    /// events keep them instead of the checkout's, which is how a mistyped checkout field is corrected for good.
    /// </summary>
    public bool ScopesSetByAdmin { get; init; }

    /// <summary>
    /// True when an admin revoked the license or a full refund did: a payment provider's later events cannot
    /// bring it back (only <c>admin restore</c> can).
    /// </summary>
    public bool RevokedByAdmin { get; init; }
}
