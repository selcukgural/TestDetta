using System.Net;

namespace TestDetta.LicenseService;

/// <summary>
/// The designed version of the license e-mail. Mail clients take tables and inline styles only, show no
/// SVG (Gmail) and run no script, so the layout is tables, the logo a PNG on the site and the key a box
/// that Apple Mail selects whole with one tap. Nothing is loaded but the logo: no web fonts, no tracking.
/// Every value comes from the checkout or the record and is HTML-encoded where it is placed, so a licensee
/// name cannot add markup or links to the mail.
/// </summary>
public static class LicenseMailHtml
{
    /// <summary>The logo, a 112 px PNG shown at 28 px (site/src/assets/email-logo.png).</summary>
    public const string LogoUrl = "https://testdetta.com/assets/email-logo.png";

    private const string Sans = "'IBM Plex Sans','Segoe UI',Helvetica,Arial,sans-serif";
    private const string Mono = "'IBM Plex Mono',Menlo,Consolas,monospace";

    /// <summary>The HTML body for <paramref name="parts"/>.</summary>
    /// <param name="parts">What the mail says.</param>
    public static string Render(LicenseMailParts parts)
    {
        var rows = string.Concat(
            Row("License", Code(parts.Id), last: false),
            Row("Plan", E(parts.Plan), last: false),
            Row("Seats", E(parts.Seats), last: false),
            Row("Repositories", Code(string.Join(", ", parts.Scopes)), last: false),
            Row("Valid until", E(parts.ValidUntil), last: true));
        var seller = parts.Seller is null
            ? ""
            : $"""<tr><td style="padding:14px 6px 0;font:12px/1.6 {Sans};color:#8a8f93;">TestDetta · testdetta.com · {E(parts.Seller)}</td></tr>""";
        return $$$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><title>Your TestDetta license</title></head>
            <body style="margin:0;padding:0;background:#f7f5f0;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#f7f5f0;border-collapse:collapse;">
              <tr><td align="center" style="padding:28px 16px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;border-collapse:collapse;">
                  <tr><td style="padding:0 4px 18px;">
                    <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;"><tr>
                      <td style="vertical-align:middle;padding-right:10px;"><img src="{{{LogoUrl}}}" width="28" height="28" alt="" style="display:block;border:0;"></td>
                      <td style="vertical-align:middle;font:600 17px/1 {{{Sans}}};color:#14171a;letter-spacing:-0.01em;">TestDetta</td>
                    </tr></table>
                  </td></tr>
                  <tr><td style="background:#fffdf9;border:1px solid #e3ded3;border-radius:10px;padding:32px 28px;font:15px/1.6 {{{Sans}}};color:#14171a;">
                    <div style="font:500 11px/1 {{{Mono}}};letter-spacing:0.08em;text-transform:uppercase;color:#0b6b4f;">License {{{E(parts.Id)}}}</div>
                    <h1 style="margin:10px 0 14px;font:600 24px/1.25 {{{Sans}}};letter-spacing:-0.015em;color:#14171a;">Your license is ready</h1>
                    <p style="margin:0 0 22px;color:#353b40;">Hello {{{E(parts.Licensee)}}}, thank you for choosing TestDetta. Below is your license and how to give it to TestDetta in CI.</p>
                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;font-size:14px;margin:0 0 24px;">{{{rows}}}</table>
                    <div style="font:500 11px/1 {{{Mono}}};letter-spacing:0.08em;text-transform:uppercase;color:#535a60;margin:0 0 8px;">License key</div>
                    <div style="background:#f1eee7;border:1px solid #e3ded3;border-radius:6px;padding:14px 16px;font:12.5px/1.55 {{{Mono}}};color:#14171a;word-break:break-all;-webkit-user-select:all;user-select:all;">{{{E(parts.License)}}}</div>
                    <p style="margin:8px 0 26px;font-size:13px;color:#535a60;">Copy the whole block. Spaces and line breaks picked up while copying do no harm: TestDetta ignores them.</p>
                    <h2 style="margin:0 0 10px;font:600 16px/1.3 {{{Sans}}};color:#14171a;">Give it to TestDetta</h2>
                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;font-size:14px;">
                      <tr><td style="padding:6px 0 4px;"><b style="font-weight:600;">GitHub Actions.</b> Save it as the repository secret {{{Code("TESTDETTA_LICENSE")}}} and pass it to the action:</td></tr>
                      <tr><td style="padding:0 0 12px;"><div style="background:#15191c;color:#ecebe6;border-radius:6px;padding:10px 14px;font:12.5px/1.6 {{{Mono}}};word-break:break-all;">license: ${{ secrets.TESTDETTA_LICENSE }}</div></td></tr>
                      <tr><td style="padding:0 0 10px;"><b style="font-weight:600;">Other CI.</b> Expose the secret to the job as the {{{Code("TESTDETTA_LICENSE")}}} environment variable.</td></tr>
                      <tr><td style="padding:0 0 10px;"><b style="font-weight:600;">A machine without CI secrets.</b> Run {{{Code("td license install <license>")}}}.</td></tr>
                      <tr><td style="padding:0 0 22px;color:#535a60;">To check it: {{{Code("td license status")}}}</td></tr>
                    </table>
                    <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;"><tr>
                      <td style="background:#0b6b4f;border-radius:6px;"><a href="{{{LicenseMailParts.GuideUrl}}}" style="display:inline-block;padding:10px 18px;font:600 14px/1 {{{Sans}}};color:#ffffff;text-decoration:none;">Read the setup guide</a></td>
                    </tr></table>
                  </td></tr>
                  <tr><td style="padding:20px 6px 0;font:13px/1.6 {{{Sans}}};color:#535a60;">When seats or repositories change, the new license comes the same way. Questions? Reply to this e-mail.</td></tr>
                  {{{seller}}}
                </table>
              </td></tr>
            </table>
            </body></html>
            """;
    }

    /// <summary>One label and value row of the details table; the value is already encoded.</summary>
    /// <param name="label">The label, plain text.</param>
    /// <param name="html">The value as encoded HTML.</param>
    /// <param name="last">Whether a closing rule goes under the row.</param>
    private static string Row(string label, string html, bool last)
    {
        var rule = last ? "border-top:1px solid #e3ded3;border-bottom:1px solid #e3ded3;" : "border-top:1px solid #e3ded3;";
        return $"""<tr><td style="padding:8px 0;{rule}color:#535a60;width:132px;vertical-align:top;">{E(label)}</td><td style="padding:8px 0;{rule}">{html}</td></tr>""";
    }

    /// <summary>Text set in the monospace face, encoded.</summary>
    /// <param name="text">Plain text.</param>
    private static string Code(string text) =>
        $"""<span style="font-family:{Mono};font-size:13px;word-break:break-all;">{E(text)}</span>""";

    /// <summary>HTML-encodes <paramref name="text"/> for element content and quoted attributes.</summary>
    /// <param name="text">Plain text.</param>
    private static string E(string text) => WebUtility.HtmlEncode(text);
}
