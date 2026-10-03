using Sift.Core.Licensing;

namespace Sift.LicenseService;

/// <summary>Turns a <see cref="LicenseRecord"/> into a signed license string (docs/design/licensing.md, "The license").</summary>
/// <param name="key">The signing key.</param>
/// <param name="publicBaseUrl">Base URL of this service, for the refresh link licenses carry.</param>
public sealed class LicenseIssuer(SigningKey key, Uri publicBaseUrl)
{
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

    /// <summary>The claims a license for <paramref name="record"/> carries.</summary>
    /// <param name="record">The license record.</param>
    public LicenseClaims Claims(LicenseRecord record) => new(
        record.Id,
        key.KeyId,
        record.Plan,
        record.Kind,
        record.Licensee,
        record.Seats,
        record.Scopes,
        record.ValidFrom,
        record.PeriodEnd + Slack(record.Kind),
        new Uri(publicBaseUrl, $"v1/licenses/{Uri.EscapeDataString(record.Id)}"));

    /// <summary>The signed license string for <paramref name="record"/>.</summary>
    /// <param name="record">The license record.</param>
    public string Issue(LicenseRecord record) => key.Sign(Claims(record));
}
