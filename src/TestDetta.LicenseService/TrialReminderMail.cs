using System.Globalization;
using System.Net;

namespace TestDetta.LicenseService;

/// <summary>
/// The one reminder a trial gets, a few days before it ends: when, and where to subscribe to keep going.
/// Plain text and a short designed version in the license mail's style. It names the owners the trial
/// covered (from the visitor's own form), nothing else.
/// </summary>
public static class TrialReminderMail
{
    /// <summary>Where a visitor subscribes.</summary>
    public const string PricingUrl = "https://testdetta.com/pricing/";

    /// <summary>The reminder for <paramref name="trial"/>.</summary>
    /// <param name="trial">The trial whose end is near.</param>
    public static LicenseMail For(TrialToRemind trial)
    {
        var ends = trial.EndsAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var owners = string.Join(", ", trial.Owners);
        var text = $"""
            Hello,

            Your TestDetta trial for {owners} ends on {ends}. After that day TestDetta runs every test again, as it did before.

            To keep running only the tests a change can affect, subscribe at {PricingUrl}
            Your license arrives by e-mail right after checkout. Put it in the TESTDETTA_LICENSE secret in place of the trial license.

            Questions? Reply to this e-mail.
            """;
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><title>Your TestDetta trial ends on {{E(ends)}}</title></head>
            <body style="margin:0;padding:0;background:#f7f5f0;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#f7f5f0;border-collapse:collapse;">
              <tr><td align="center" style="padding:28px 16px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;border-collapse:collapse;">
                  <tr><td style="padding:0 4px 18px;font:600 17px/1 'IBM Plex Sans','Segoe UI',Helvetica,Arial,sans-serif;color:#14171a;"><img src="{{LicenseMailHtml.LogoUrl}}" width="28" height="28" alt="" style="vertical-align:middle;border:0;margin-right:10px;">TestDetta</td></tr>
                  <tr><td style="background:#fffdf9;border:1px solid #e3ded3;border-radius:10px;padding:32px 28px;font:15px/1.6 'IBM Plex Sans','Segoe UI',Helvetica,Arial,sans-serif;color:#14171a;">
                    <h1 style="margin:0 0 14px;font:600 22px/1.3 'IBM Plex Sans','Segoe UI',Helvetica,Arial,sans-serif;color:#14171a;">Your trial ends on {{E(ends)}}</h1>
                    <p style="margin:0 0 14px;color:#353b40;">Your TestDetta trial for {{E(owners)}} ends on {{E(ends)}}. After that day TestDetta runs every test again, as it did before.</p>
                    <p style="margin:0 0 22px;color:#353b40;">To keep running only the tests a change can affect, subscribe. Your license arrives by e-mail right after checkout; put it in the <span style="font-family:'IBM Plex Mono',Menlo,Consolas,monospace;font-size:13px;">TESTDETTA_LICENSE</span> secret in place of the trial license.</p>
                    <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;"><tr>
                      <td style="background:#0b6b4f;border-radius:6px;"><a href="{{PricingUrl}}" style="display:inline-block;padding:10px 18px;font:600 14px/1 'IBM Plex Sans','Segoe UI',Helvetica,Arial,sans-serif;color:#ffffff;text-decoration:none;">See plans and subscribe</a></td>
                    </tr></table>
                  </td></tr>
                  <tr><td style="padding:20px 6px 0;font:13px/1.6 'IBM Plex Sans','Segoe UI',Helvetica,Arial,sans-serif;color:#535a60;">Questions? Reply to this e-mail.</td></tr>
                </table>
              </td></tr>
            </table>
            </body></html>
            """;
        return new LicenseMail(trial.Email, $"Your TestDetta trial ends on {ends}", text) { Html = html };
    }

    /// <summary>HTML-encodes <paramref name="text"/>.</summary>
    /// <param name="text">Plain text.</param>
    private static string E(string text) => WebUtility.HtmlEncode(text);
}
