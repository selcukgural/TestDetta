using System.Globalization;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// The e-mail that hands a buyer their license string. Plain text only: the licensee name comes from the
/// checkout, and plain text cannot turn it into markup or links.
/// </summary>
/// <param name="To">The buyer's address, from the payment provider's event; never stored.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Text">Body, with the license string on a line of its own.</param>
public sealed record LicenseMail(string To, string Subject, string Text)
{
    /// <summary>The e-mail for <paramref name="record"/> and its signed <paramref name="license"/>.</summary>
    /// <param name="record">The license record.</param>
    /// <param name="license">The signed license string.</param>
    /// <param name="to">The buyer's address.</param>
    public static LicenseMail For(LicenseRecord record, string license, string to)
    {
        var validUntil = (record.PeriodEnd + LicenseIssuer.Slack(record.Kind)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var renewal = record.Kind == LicenseKind.Subscription
            ? $"While the subscription runs, TestDetta renews the license by itself before it ends ({validUntil})."
            : $"The license is valid until {validUntil}.";
        var text = $"""
            Hello {record.Licensee},

            Thank you for choosing TestDetta. Here is your license ({record.Id}):

            {license}

            It covers {record.Seats} active committers in {string.Join(", ", record.Scopes)}.
            {renewal}

            In CI: store it as a secret and expose it to TestDetta as the TESTDETTA_LICENSE environment variable.
            On a machine without CI secrets: td license install <license>
            To check it: td license status

            When seats or repositories change, you receive the new license the same way. Reply to this
            e-mail if anything does not work.
            """;
        return new LicenseMail(to, $"Your TestDetta license {record.Id}", text);
    }
}
