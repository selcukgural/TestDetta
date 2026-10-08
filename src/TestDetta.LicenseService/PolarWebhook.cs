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
/// (events arrive late or at once) is ignored by a conditional write. <c>order.refunded</c> revokes the license
/// of a fully refunded subscription; other events are acknowledged and ignored.
/// </summary>
public static partial class PolarWebhook
{
    /// <summary>Largest body accepted: subscriptions carry their product, prices and meters.</summary>
    public const long MaxBodyBytes = 256 * 1024;

    /// <summary>How far a webhook's timestamp may be from now, as Standard Webhooks recommends.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>Name of the checkout custom field in which a customer gives the repositories to license.</summary>
    public const string ScopeField = "scope";

    /// <summary>Most scopes one checkout may give; more is flagged for an admin, who can set any number.</summary>
    public const int MaxScopes = 50;

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
            LogRepeated(logger, id);
            return Results.Ok();
        }

        string type;
        string? licenseId = null;
        var outcome = WebhookOutcome.Ignored;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            type = root.GetProperty("type").GetString() ?? "";
            if (!ForListedProduct(root, settings))
            {
                LogOtherProduct(logger, id, type);
            }
            else if (type == "order.refunded")
            {
                (licenseId, outcome) = await RefundAsync(root.GetProperty("data"), store, logger, context.RequestAborted);
            }
            else if (type.StartsWith("subscription.", StringComparison.Ordinal))
            {
                var subscription = root.GetProperty("data");
                licenseId = LicenseId(subscription);
                outcome = await ApplyAsync(root, store, logger, context.RequestAborted);

                // After the record is saved and before the webhook counts as handled: a send that fails for
                // now leaves the event to Polar's retries, which find the record saved and send again.
                try
                {
                    await delivery.DeliverAsync(licenseId, CustomerEmail(subscription), context.RequestAborted);
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
                {
                    LogDeliveryFailed(logger, licenseId);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException or FormatException or InvalidOperationException { Source: "System.Text.Json" })
        {
            // Signed by Polar but not the shape this code reads (a field missing or of another kind): retrying
            // cannot change that, so it is handled as ignored and logged for an admin instead of failing forever.
            LogMalformed(logger, id, e.GetType().Name);
            type = "malformed";
            outcome = WebhookOutcome.Ignored;
        }

        await store.MarkWebhookHandledAsync(webhookId, type, licenseId, outcome, context.RequestAborted);
        LogHandled(logger, id, type, licenseId ?? "-", outcome);
        return Results.Ok();
    }

    /// <summary>
    /// Checks a Standard Webhooks signature: HMAC-SHA256 over <c>{id}.{timestamp}.{body}</c>, compared in
    /// constant time with each <c>v1,</c> signature of the header, and a timestamp within
    /// <see cref="Tolerance"/> of <paramref name="now"/>. The key is the base64 after <c>whsec_</c> (Polar's
    /// secrets from 2026-09-08 on) or the secret's own UTF-8 bytes (older secrets); like Polar's SDKs, both
    /// are tried, since either requires the secret.
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

        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        byte[] signed = [.. prefix, .. body];
        foreach (var key in Keys(secret))
        {
            if (Matches(HMACSHA256.HashData(key, signed), signatures))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The keys a Polar secret may stand for: the base64 after <c>whsec_</c>, then the secret's bytes.</summary>
    /// <param name="secret">The endpoint's secret.</param>
    private static IEnumerable<byte[]> Keys(string secret)
    {
        if (secret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            byte[]? decoded = null;
            try
            {
                decoded = Convert.FromBase64String(secret[6..]);
            }
            catch (FormatException)
            {
                // Not base64 after all: only the secret's own bytes remain.
            }

            if (decoded is not null)
            {
                yield return decoded;
            }
        }

        yield return Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>True when one <c>v1,</c> signature of <paramref name="signatures"/> equals <paramref name="expected"/>.</summary>
    /// <param name="expected">The HMAC computed with one candidate key.</param>
    /// <param name="signatures">The <c>webhook-signature</c> header.</param>
    private static bool Matches(byte[] expected, string signatures)
    {
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

    /// <summary>Rebuilds the record of the event's subscription from its current state; returns what that did.</summary>
    /// <param name="event">A <c>subscription.*</c> event: its <c>type</c>, <c>timestamp</c> and the subscription as <c>data</c>.</param>
    /// <param name="store">License records.</param>
    /// <param name="logger">Receives records that need attention.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private static async Task<WebhookOutcome> ApplyAsync(JsonElement @event, LicenseStore store, ILogger logger, CancellationToken cancellationToken)
    {
        var subscription = @event.GetProperty("data");
        var type = @event.GetProperty("type").GetString() ?? "";
        var licenseId = LicenseId(subscription);
        var status = subscription.GetProperty("status").GetString();
        var existing = await store.GetAsync(licenseId, cancellationToken);

        // A checkout that never completed pays for nothing: no license unless one exists already.
        if (existing is null && status is "incomplete" or "incomplete_expired")
        {
            return WebhookOutcome.Unpaid;
        }

        var seats = subscription.TryGetProperty("seats", out var seatsValue) && seatsValue.ValueKind == JsonValueKind.Number ? seatsValue.GetInt32() : 0;
        if (seats <= 0)
        {
            // The Polar product must be seat-based; an admin fixes the product and the record.
            await NeedsAttentionAsync(store, logger, licenseId, $"polar {type}: the subscription has no seat count", cancellationToken);
            return WebhookOutcome.NeedsAttention;
        }

        // What the customer typed, normalised; a scope that matches no repository would make the paid license run
        // every test without anyone noticing, so anything unreadable is flagged for an admin (admin update --scope).
        var parsed = ScopePatterns.Parse(ScopeText(subscription), ScopeRules.Customer);
        var scopes = parsed.Patterns.Take(MaxScopes).ToList();
        // Once an admin set the scopes, they stay (LicenseStore.SaveAsync) and the checkout's no longer matter.
        if (existing is not { ScopesSetByAdmin: true })
        {
            if (parsed.Rejected.Count > 0 || parsed.Patterns.Count > MaxScopes)
            {
                await NeedsAttentionAsync(store, logger, licenseId, $"polar {type}: repository scopes not understood: {Quote(parsed.Rejected)}{(parsed.Patterns.Count > MaxScopes ? $"; more than {MaxScopes} given" : "")}; kept: {Quote(scopes)}", cancellationToken);
            }
            else if (scopes.Count == 0 && existing is not { Scopes.Count: > 0 })
            {
                await NeedsAttentionAsync(store, logger, licenseId, $"polar {type}: no repository scope was given at checkout", cancellationToken);
            }
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

        // The event's own time orders the states: Polar builds the payload when it emits the event, and leaves
        // modified_at null for a while (created, active and updated of one checkout all carried none, sandbox
        // 2026-10-07), so modified_at or created_at would make them equal and drop all but the first. A
        // redelivery repeats the payload with its time, so it can never pass a newer state.
        var stateAt = Date(@event, "timestamp") ?? Date(subscription, "modified_at") ?? Date(subscription, "created_at");
        return await store.SaveAsync(record, type, $"polar {type}: status {status}, {seats} seats, until {periodEnd:yyyy-MM-dd}", stateAt, cancellationToken)
            ? WebhookOutcome.Saved
            : WebhookOutcome.Stale;
    }

    /// <summary>
    /// <c>order.refunded</c>: an order of a subscription was refunded. A full refund revokes the subscription's
    /// license for good (<see cref="LicenseStore.SetRevokedAsync"/>), since the customer has their money back while
    /// Polar may keep the subscription running; the license string already handed out still runs to its
    /// <c>validUntil</c>, which <see cref="LicenseIssuer.MaxAhead"/> keeps near. Either way an admin is told,
    /// since a refund of an older order, or a partial one, may call for something else
    /// (<c>admin restore</c>, <c>admin update</c>).
    /// </summary>
    /// <param name="order">The refunded order.</param>
    /// <param name="store">License records.</param>
    /// <param name="logger">Receives what needs attention.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private static async Task<(string? LicenseId, WebhookOutcome Outcome)> RefundAsync(JsonElement order, LicenseStore store, ILogger logger, CancellationToken cancellationToken)
    {
        if (!order.TryGetProperty("subscription_id", out var subscriptionId) || subscriptionId.GetString() is not { Length: > 0 } subscription)
        {
            return (null, WebhookOutcome.Ignored);
        }

        var licenseId = "P-" + subscription;
        var status = order.TryGetProperty("status", out var value) ? value.GetString() : null;
        var amounts = $"{Cents(order, "refunded_amount")} of {Cents(order, "total_amount")} cents refunded";
        if (status != "refunded")
        {
            await NeedsAttentionAsync(store, logger, licenseId, $"polar order.refunded: partial refund ({amounts}); the license keeps running", cancellationToken);
            return (licenseId, WebhookOutcome.NeedsAttention);
        }

        var revoked = await store.SetRevokedAsync(licenseId, revoked: true, "refund", $"polar order.refunded: full refund ({amounts})", cancellationToken);
        await NeedsAttentionAsync(store, logger, licenseId, revoked
            ? $"polar order.refunded: full refund ({amounts}); the license is revoked (admin restore lifts it)"
            : $"polar order.refunded: full refund ({amounts}) of a subscription without a license", cancellationToken);
        return (licenseId, WebhookOutcome.NeedsAttention);
    }

    /// <summary>An amount in cents from an order, or "?" when it carries none.</summary>
    /// <param name="order">An order.</param>
    /// <param name="name">Property name.</param>
    private static string Cents(JsonElement order, string name) =>
        order.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64().ToString(CultureInfo.InvariantCulture) : "?";

    /// <summary>
    /// True when the event is about a product in <see cref="ServiceSettings.PolarProducts"/>, or the list is empty
    /// (every product). A subscription names its product as <c>product_id</c> (or <c>product.id</c>), an order as
    /// <c>product_id</c>; an event that names none passes.
    /// </summary>
    /// <param name="event">The event.</param>
    /// <param name="settings">Service settings.</param>
    private static bool ForListedProduct(JsonElement @event, ServiceSettings settings)
    {
        if (settings.PolarProducts.Count == 0 || !@event.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var product = data.TryGetProperty("product_id", out var productId) && productId.ValueKind == JsonValueKind.String ? productId.GetString()
            : data.TryGetProperty("product", out var nested) && nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty("id", out var nestedId) ? nestedId.GetString()
            : null;
        return product is null || settings.PolarProducts.Contains(product, StringComparer.Ordinal);
    }

    /// <summary>What the customer typed in the checkout field <see cref="ScopeField"/>; null when nothing.</summary>
    /// <param name="subscription">A Polar subscription.</param>
    private static string? ScopeText(JsonElement subscription) =>
        subscription.TryGetProperty("custom_field_data", out var fields) && fields.ValueKind == JsonValueKind.Object
        && fields.TryGetProperty(ScopeField, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Patterns for an audit entry, each quoted and cut short, so the entry stays readable whatever was typed.</summary>
    /// <param name="patterns">Patterns or rejected text.</param>
    private static string Quote(IEnumerable<string> patterns)
    {
        var quoted = string.Join(", ", patterns.Take(MaxScopes).Select(static p => $"'{(p.Length > 80 ? p[..80] + "…" : p)}'"));
        return quoted.Length == 0 ? "none" : quoted;
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Polar webhook {WebhookId} ({Type}) for license {LicenseId}: {Outcome}")]
    private static partial void LogHandled(ILogger logger, string webhookId, string type, string licenseId, WebhookOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Polar webhook {WebhookId} was handled before; nothing done")]
    private static partial void LogRepeated(ILogger logger, string webhookId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polar webhook rejected: signature or timestamp does not check out")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polar webhook {WebhookId} ({Type}) is for a product not in Licenses:PolarProducts; ignored")]
    private static partial void LogOtherProduct(ILogger logger, string webhookId, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polar webhook {WebhookId} is signed but not in the expected shape ({Error}); handled as ignored")]
    private static partial void LogMalformed(ILogger logger, string webhookId, string error);

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
