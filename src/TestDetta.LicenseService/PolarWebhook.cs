using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// <c>POST /webhooks/polar</c>: Polar subscription events update the license records
/// (docs/design/licensing.md, "The license service"). Polar signs with the Standard Webhooks scheme
/// (secrets made after 2026-09-08). A request whose signature or timestamp does not check out gets 401
/// and nothing of it is stored. A webhook id is handled once. Every subscription event carries the whole
/// subscription, so the record is rebuilt from its current state, and a state older than the stored one
/// (events arrive late or at once) is ignored by a conditional write.
/// </summary>
public static partial class PolarWebhook
{
    /// <summary>Largest body accepted: subscriptions carry their product, prices and meters.</summary>
    public const long MaxBodyBytes = 256 * 1024;

    /// <summary>How far a webhook's timestamp may be from now, as Standard Webhooks recommends.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>Name of the checkout custom field in which a customer gives the repositories to license.</summary>
    public const string ScopeField = "scope";

    /// <summary>Handles one delivery.</summary>
    /// <param name="context">The request.</param>
    /// <param name="services">What handling needs from the service.</param>
    public static async Task<IResult> HandleAsync(HttpContext context, [AsParameters] PolarWebhookServices services)
    {
        var (settings, store, delivery, clock, logger) = services;
        if (settings.PolarWebhookSecret is not { Length: > 0 } secret)
        {
            return Results.NotFound();
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = buffer.ToArray();
        }

        var headers = context.Request.Headers;
        var id = headers["webhook-id"].ToString();
        if (!Verify(secret, id, headers["webhook-timestamp"].ToString(), headers["webhook-signature"].ToString(), body, clock.GetUtcNow()))
        {
            LogRejected(logger);
            return Results.Unauthorized();
        }

        var webhookId = "polar:" + id;
        if (await store.WebhookHandledAsync(webhookId, context.RequestAborted))
        {
            return Results.Ok();
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "";
        if (type.StartsWith("subscription.", StringComparison.Ordinal))
        {
            var subscription = root.GetProperty("data");
            await ApplyAsync(subscription, type, store, logger, context.RequestAborted);

            // After the record is saved and before the webhook counts as handled: a send that fails for
            // now leaves the event to Polar's retries, which find the record saved and send again.
            try
            {
                await delivery.DeliverAsync(LicenseId(subscription), CustomerEmail(subscription), context.RequestAborted);
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                LogDeliveryFailed(logger, LicenseId(subscription));
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        await store.MarkWebhookHandledAsync(webhookId, context.RequestAborted);
        return Results.Ok();
    }

    /// <summary>
    /// Checks a Standard Webhooks signature: HMAC-SHA256 with the secret (base64 after <c>whsec_</c>) over
    /// <c>{id}.{timestamp}.{body}</c>, compared in constant time with each <c>v1,</c> signature of the
    /// header, and a timestamp within <see cref="Tolerance"/> of <paramref name="now"/>.
    /// </summary>
    /// <param name="secret">The endpoint's secret, <c>whsec_...</c>.</param>
    /// <param name="id">The <c>webhook-id</c> header.</param>
    /// <param name="timestamp">The <c>webhook-timestamp</c> header, Unix seconds.</param>
    /// <param name="signatures">The <c>webhook-signature</c> header: space-separated <c>v1,&lt;base64&gt;</c>.</param>
    /// <param name="body">The raw body.</param>
    /// <param name="now">The current time.</param>
    public static bool Verify(string secret, string id, string timestamp, string signatures, byte[] body, DateTimeOffset now)
    {
        if (id.Length == 0 || !long.TryParse(timestamp, CultureInfo.InvariantCulture, out var seconds)
            || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);
        }
        catch (FormatException)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        byte[] signed = [.. prefix, .. body];
        var expected = HMACSHA256.HashData(key, signed);
        foreach (var part in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("v1,", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(part[3..])))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // A malformed signature is simply not a match.
            }
        }

        return false;
    }

    /// <summary>Rebuilds the record of the subscription from its current state.</summary>
    /// <param name="subscription">The <c>data</c> of the event: a Polar subscription.</param>
    /// <param name="type">The event type, for the audit.</param>
    /// <param name="store">License records.</param>
    /// <param name="logger">Receives records that need attention.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private static async Task ApplyAsync(JsonElement subscription, string type, LicenseStore store, ILogger logger, CancellationToken cancellationToken)
    {
        var licenseId = LicenseId(subscription);
        var status = subscription.GetProperty("status").GetString();
        var existing = await store.GetAsync(licenseId, cancellationToken);

        // A checkout that never completed pays for nothing: no license unless one exists already.
        if (existing is null && status is "incomplete" or "incomplete_expired")
        {
            return;
        }

        var seats = subscription.TryGetProperty("seats", out var seatsValue) && seatsValue.ValueKind == JsonValueKind.Number ? seatsValue.GetInt32() : 0;
        if (seats <= 0)
        {
            // The Polar product must be seat-based; an admin fixes the product and the record.
            await NeedsAttentionAsync(store, logger, licenseId, $"polar {type}: the subscription has no seat count", cancellationToken);
            return;
        }

        var scopes = Scopes(subscription);
        if (scopes.Count == 0 && existing is not { Scopes.Count: > 0 })
        {
            await NeedsAttentionAsync(store, logger, licenseId, $"polar {type}: no repository scope was given at checkout", cancellationToken);
        }

        // Canceled at period end still runs to the end of what was paid; ended (revoked, refunded,
        // unpaid) stops renewals at once.
        var ended = subscription.TryGetProperty("ended_at", out var endedAt) && endedAt.ValueKind == JsonValueKind.String
                    || status is "incomplete_expired" or "unpaid";
        var periodEnd = Date(subscription, "ends_at") ?? Date(subscription, "current_period_end") ?? DateTimeOffset.UtcNow;
        var record = new LicenseRecord(
            licenseId,
            Licensee(subscription),
            subscription.TryGetProperty("product", out var product) && product.TryGetProperty("name", out var name) ? name.GetString() ?? "team" : "team",
            status == "trialing" ? LicenseKind.Trial : LicenseKind.Subscription,
            seats,
            scopes.Count > 0 ? scopes : existing?.Scopes ?? [],
            Date(subscription, "started_at") ?? Date(subscription, "current_period_start") ?? periodEnd,
            periodEnd,
            ended,
            "polar");

        await store.SaveAsync(record, type, $"polar {type}: status {status}, {seats} seats, until {periodEnd:yyyy-MM-dd}", Date(subscription, "modified_at"), cancellationToken);
    }

    /// <summary>
    /// The scopes a customer gave in the checkout field <see cref="ScopeField"/>: patterns separated by
    /// commas, spaces or new lines, lower-cased.
    /// </summary>
    /// <param name="subscription">A Polar subscription.</param>
    private static List<string> Scopes(JsonElement subscription) =>
        subscription.TryGetProperty("custom_field_data", out var fields) && fields.ValueKind == JsonValueKind.Object
        && fields.TryGetProperty(ScopeField, out var value) && value.ValueKind == JsonValueKind.String
            ? [.. value.GetString()!.Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(static s => s.Trim().ToLowerInvariant())]
            : [];

    /// <summary>The id of the license record a Polar subscription makes.</summary>
    /// <param name="subscription">A Polar subscription.</param>
    private static string LicenseId(JsonElement subscription) => "P-" + subscription.GetProperty("id").GetString();

    /// <summary>The customer's e-mail address, where the license is sent; null when the event carries none.</summary>
    /// <param name="subscription">A Polar subscription.</param>
    private static string? CustomerEmail(JsonElement subscription) =>
        subscription.TryGetProperty("customer", out var customer) && customer.TryGetProperty("email", out var email)
        && email.ValueKind == JsonValueKind.String
            ? email.GetString()
            : null;

    /// <summary>The customer's name, or billing name; never the email, which a license string would carry.</summary>
    /// <param name="subscription">A Polar subscription.</param>
    private static string Licensee(JsonElement subscription)
    {
        if (subscription.TryGetProperty("customer", out var customer))
        {
            foreach (var field in (string[])["name", "billing_name"])
            {
                if (customer.TryGetProperty(field, out var value) && value.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }

        return "Polar customer";
    }

    /// <summary>Logs and audits something about a license an admin must fix.</summary>
    /// <param name="store">License records.</param>
    /// <param name="logger">Receives the warning.</param>
    /// <param name="licenseId">License id.</param>
    /// <param name="reason">What needs fixing.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private static async Task NeedsAttentionAsync(LicenseStore store, ILogger logger, string licenseId, string reason, CancellationToken cancellationToken)
    {
        LogNeedsAttention(logger, licenseId, reason);
        await store.AuditAsync(licenseId, "attention", reason, cancellationToken);
    }

    /// <summary>A date-time property, or null when missing or null.</summary>
    /// <param name="element">An object.</param>
    /// <param name="name">Property name.</param>
    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetDateTimeOffset() : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polar webhook rejected: signature or timestamp does not check out")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License {LicenseId} saved but not e-mailed yet; Polar will deliver the event again")]
    private static partial void LogDeliveryFailed(ILogger logger, string licenseId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License {LicenseId} needs attention: {Reason}")]
    private static partial void LogNeedsAttention(ILogger logger, string licenseId, string reason);
}

/// <summary>What <see cref="PolarWebhook.HandleAsync"/> needs from the service, bound from its services.</summary>
/// <param name="Settings">Service settings, for the webhook secret.</param>
/// <param name="Store">License records.</param>
/// <param name="Delivery">E-mails the license to the buyer.</param>
/// <param name="Clock">The current time.</param>
/// <param name="Logger">Receives rejected deliveries (counted) and records that need attention.</param>
public sealed record PolarWebhookServices(ServiceSettings Settings, LicenseStore Store, LicenseDelivery Delivery, TimeProvider Clock, ILogger<LicenseStore> Logger);
