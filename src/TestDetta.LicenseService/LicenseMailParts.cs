using System.Globalization;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// What the license e-mail says, worked out once from the record so the designed and the plain-text
/// versions can never disagree. Values are plain text; the HTML version encodes them where it places them.
/// </summary>
/// <param name="Id">License id.</param>
/// <param name="Licensee">Who the license is for, from the checkout.</param>
/// <param name="Plan">Plan name, with "trial" added for a trial.</param>
/// <param name="Seats">Active committers covered, e.g. "10 active committers".</param>
/// <param name="Scopes">Repository patterns the license covers.</param>
/// <param name="ValidUntil">Last day the license works, with what happens after it.</param>
/// <param name="License">The signed license string.</param>
/// <param name="Seller">Who sold it and where the invoice comes from; null when the source names no seller.</param>
public sealed record LicenseMailParts(
    string Id,
    string Licensee,
    string Plan,
    string Seats,
    IReadOnlyList<string> Scopes,
    string ValidUntil,
    string License,
    string? Seller)
{
    /// <summary>Where the setup guide lives; the mail links to it.</summary>
    public const string GuideUrl = "https://testdetta.com/docs/github-actions/";

    /// <summary>
    /// The parts for <paramref name="record"/>. The seller line follows the record's source: a Polar purchase
    /// is sold and invoiced by Polar as merchant of record; a license issued by hand (a direct invoice) or
    /// from a provider not known here names no seller rather than a wrong one.
    /// </summary>
    /// <param name="record">The license record.</param>
    /// <param name="license">The signed license string.</param>
    public static LicenseMailParts For(LicenseRecord record, string license)
    {
        var until = (record.PeriodEnd + LicenseIssuer.Slack(record.Kind)).ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var validUntil = record.Kind switch
        {
            LicenseKind.Subscription => $"{until}, then renewed by itself while the subscription runs",
            LicenseKind.Trial => $"{until}, the end of the trial; a subscription brings a new license by e-mail",
            _ => until,
        };
        var seller = record.Source == "polar"
            ? "Sold by Polar Software, Inc., the merchant of record for your purchase. Your invoice comes from Polar."
            : null;
        return new LicenseMailParts(
            record.Id,
            record.Licensee,
            record.Kind == LicenseKind.Trial ? $"{record.Plan} · trial" : record.Plan,
            record.Seats == 1 ? "1 active committer" : $"{record.Seats} active committers",
            record.Scopes,
            validUntil,
            license,
            seller);
    }
}
