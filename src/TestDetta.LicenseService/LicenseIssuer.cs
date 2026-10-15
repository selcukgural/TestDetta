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
    /// Slack added to the paid period: a subscription keeps working for 15 days while a renewal is on its
    /// way, an annual (offline) license for 30 days while a new file is delivered; a trial has none. A
    /// subscription's slack stays above <see cref="LicenseCheck.ExpiryWarningDays"/>: the provider renews at
    /// the period's end, so with less every paying customer's job summaries would say the license expires
    /// during the last days of each period.
    /// </summary>
    /// <param name="kind">License kind.</param>
    public static TimeSpan Slack(LicenseKind kind) => kind switch
    {
        LicenseKind.Subscription => TimeSpan.FromDays(15),
        LicenseKind.Annual => TimeSpan.FromDays(30),
        _ => TimeSpan.Zero,
    };

    /// <summary>
    /// Furthest a subscription license runs ahead of its signing. A yearly subscription's paid period would
    /// otherwise put a whole year into a string nobody can recall offline, so a refund or a revocation would
    /// leave the customer a working license for months. Capped, the string runs out within this time and the
    /// daily refresh (docs/design/licensing.md, "Refresh") keeps handing out a new one while the subscription
    /// runs. Late in a monthly period the period's end plus its slack comes first, so the string never has
    /// fewer than 15 days left before the renewal.
    /// Annual (offline) licenses are not capped, since they carry no refresh link; trials end sooner anyway.
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
            Refresh(record))
        {
            Issued = now,
        };
    }

    /// <summary>
    /// Where TestDetta fetches a newer version of the license: none for an annual (offline) license, which
    /// is sold for networks that cannot or must not reach this service and is renewed by a new file, so
    /// TestDetta makes no call for it at all.
    /// </summary>
    /// <param name="record">The license record.</param>
    private Uri? Refresh(LicenseRecord record) =>
        record.Kind == LicenseKind.Annual ? null : new Uri(publicBaseUrl, $"v1/licenses/{Uri.EscapeDataString(record.Id)}");

    /// <summary>The signed license string for <paramref name="record"/>.</summary>
    /// <param name="record">The license record.</param>
    public string Issue(LicenseRecord record) => key.Sign(Claims(record));
}
