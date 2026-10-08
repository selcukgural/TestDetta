using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>Turns a <see cref="LicenseRecord"/> into a signed license string (docs/design/licensing.md, "The license").</summary>
/// <param name="key">The signing key.</param>
/// <param name="publicBaseUrl">Base URL of this service, for the refresh link licenses carry.</param>
/// <param name="clock">Gives the signing time; <see cref="TimeProvider.System"/> outside tests.</param>
public sealed class LicenseIssuer(SigningKey key, Uri publicBaseUrl, TimeProvider clock)
{
    /// <summary>An issuer on the system clock.</summary>
    /// <param name="key">The signing key.</param>
    /// <param name="publicBaseUrl">Base URL of this service.</param>
    public LicenseIssuer(SigningKey key, Uri publicBaseUrl)
        : this(key, publicBaseUrl, TimeProvider.System)
    {
    }

    /// <summary>
    /// Slack added to the paid period: a subscription keeps working for 7 days while a renewal is on its
    /// way, an annual (offline) license for 30 days while a new file is delivered; a trial has none.
    /// </summary>
    /// <param name="kind">License kind.</param>
    public static TimeSpan Slack(LicenseKind kind) => kind switch
    {
        LicenseKind.Subscription => TimeSpan.FromDays(7),
        LicenseKind.Annual => TimeSpan.FromDays(30),
        _ => TimeSpan.Zero,
    };

    /// <summary>
    /// Furthest a subscription license runs ahead of its signing. A yearly subscription's paid period would
    /// otherwise put a whole year into a string nobody can recall offline, so a refund or a revocation would
    /// leave the customer a working license for months. Capped, the string runs out within this time and the
    /// daily refresh (docs/design/licensing.md, "Refresh") keeps handing out a new one while the subscription
    /// runs. 40 days keeps a monthly subscription's full period and its 7 days of slack (at most 38 days).
    /// Annual (offline) licenses and trials are not capped: they never refresh, or end sooner anyway.
    /// </summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(40);

    /// <summary>The claims a license for <paramref name="record"/> carries.</summary>
    /// <param name="record">The license record.</param>
    public LicenseClaims Claims(LicenseRecord record)
    {
        var now = clock.GetUtcNow();
        var until = record.PeriodEnd + Slack(record.Kind);
        if (record.Kind == LicenseKind.Subscription && until > now + MaxAhead)
        {
            until = now + MaxAhead;
        }

        return new LicenseClaims(
            record.Id,
            key.KeyId,
            record.Plan,
            record.Kind,
            record.Licensee,
            record.Seats,
            record.Scopes,
            record.ValidFrom,
            until,
            new Uri(publicBaseUrl, $"v1/licenses/{Uri.EscapeDataString(record.Id)}"))
        {
            Issued = now,
        };
    }

    /// <summary>The signed license string for <paramref name="record"/>.</summary>
    /// <param name="record">The license record.</param>
    public string Issue(LicenseRecord record) => key.Sign(Claims(record));
}
