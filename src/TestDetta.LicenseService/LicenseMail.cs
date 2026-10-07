namespace TestDetta.LicenseService;

/// <summary>
/// The e-mail that hands a buyer their license string, as plain text and as a designed HTML version with
/// the same content (<see cref="LicenseMailHtml"/>). Clients that cannot show the design show the text.
/// </summary>
/// <param name="To">The buyer's address, from the payment provider's event; never stored.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Text">Plain-text body, with the license string on a line of its own.</param>
public sealed record LicenseMail(string To, string Subject, string Text)
{
    /// <summary>The designed body; null sends the plain text alone.</summary>
    public string? Html { get; init; }

    /// <summary>The e-mail for <paramref name="record"/> and its signed <paramref name="license"/>.</summary>
    /// <param name="record">The license record.</param>
    /// <param name="license">The signed license string.</param>
    /// <param name="to">The buyer's address.</param>
    public static LicenseMail For(LicenseRecord record, string license, string to)
    {
        var parts = LicenseMailParts.For(record, license);
        return new LicenseMail(to, $"Your TestDetta license {record.Id}", PlainText(parts)) { Html = LicenseMailHtml.Render(parts) };
    }

    /// <summary>The plain-text body: the same content as the designed one, in the same order.</summary>
    /// <param name="parts">What the mail says.</param>
    private static string PlainText(LicenseMailParts parts)
    {
        // Three dollar signs: the GitHub Actions expression below keeps its own double braces.
        var text = $$$"""
            Hello {{{parts.Licensee}}},

            Thank you for choosing TestDetta. Here is your license ({{{parts.Id}}}):

            {{{parts.License}}}

            Plan:          {{{parts.Plan}}}
            Seats:         {{{parts.Seats}}}
            Repositories:  {{{string.Join(", ", parts.Scopes)}}}
            Valid until:   {{{parts.ValidUntil}}}

            Copy the whole license. Spaces and line breaks picked up while copying do no harm.

            GitHub Actions: save it as the repository secret TESTDETTA_LICENSE and pass it to the action:
              license: ${{ secrets.TESTDETTA_LICENSE }}
            Other CI: expose the secret to the job as the TESTDETTA_LICENSE environment variable.
            A machine without CI secrets: td license install <license>
            To check it: td license status

            Setup guide: {{{LicenseMailParts.GuideUrl}}}

            When seats or repositories change, the new license comes the same way.
            Questions? Reply to this e-mail.
            """;
        return parts.Seller is null ? text : $"{text}\n\n{parts.Seller}";
    }
}
