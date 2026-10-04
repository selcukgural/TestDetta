using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TestDetta.LicenseService;

/// <summary>
/// Sends license e-mails through Resend's HTTP API (<c>POST /emails</c>). Resend signs the mail with the
/// sending domain's DKIM key, so it passes DMARC for that domain. The API key needs only "sending access"
/// for that domain, and is not the one any mailbox uses, so either can be revoked alone.
/// </summary>
/// <param name="http">Client whose base address is the Resend API.</param>
/// <param name="settings">Service settings: the API key and the sender.</param>
/// <param name="logger">Receives refused and failed sends, without the recipient's address.</param>
public sealed partial class ResendMailer(HttpClient http, ServiceSettings settings, ILogger<ResendMailer> logger) : ILicenseMailer
{
    /// <summary>The Resend API: the base address of the client this mailer is given.</summary>
    public static readonly Uri ApiBase = new("https://api.resend.com/");

    /// <inheritdoc />
    public async Task<MailOutcome> SendAsync(LicenseMail mail, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResendApiKey);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new ByteArrayContent(Body(mail, settings));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return MailOutcome.Sent;
        }

        // 429 and 5xx may pass; any other 4xx (an invalid address, an unverified domain) will not.
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
        {
            LogFailed(logger, status);
            throw new HttpRequestException($"Resend answered {status}", null, response.StatusCode);
        }

        LogRefused(logger, status);
        return MailOutcome.Rejected;
    }

    /// <summary>The JSON body of <c>POST /emails</c>.</summary>
    /// <param name="mail">The e-mail.</param>
    /// <param name="settings">Service settings, for the sender and the reply-to address.</param>
    internal static byte[] Body(LicenseMail mail, ServiceSettings settings)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("from"u8, settings.MailFrom);
            json.WriteStartArray("to"u8);
            json.WriteStringValue(mail.To);
            json.WriteEndArray();
            if (settings.MailReplyTo is { Length: > 0 } replyTo)
            {
                json.WriteString("reply_to"u8, replyTo);
            }

            json.WriteString("subject"u8, mail.Subject);
            json.WriteString("text"u8, mail.Text);
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resend failed with {Status}; the webhook is left to the provider's retries")]
    private static partial void LogFailed(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Resend refused a license e-mail with {Status}")]
    private static partial void LogRefused(ILogger logger, int status);
}
