using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace TestDetta.LicenseService;

/// <summary>
/// E-mails a buyer their license string after a payment provider's event (docs/design/licensing.md,
/// "Delivery by e-mail"). A license is sent when it is new and again whenever what it grants changes
/// (licensee, plan, kind, seats or scopes), never for a renewal alone: the refresh endpoint hands those
/// out. The address comes from the event and is never stored or logged; what is stored is which version
/// of a license went out, so a webhook retried after a failed send sends it, and a repeated one does not.
/// </summary>
/// <param name="store">License records and deliveries.</param>
/// <param name="issuer">Signs the license string.</param>
/// <param name="mailer">Sends the e-mail; null when no e-mail provider is configured, which turns delivery off.</param>
/// <param name="logger">Receives what was sent and what needs an admin, by license id only.</param>
public sealed partial class LicenseDelivery(LicenseStore store, LicenseIssuer issuer, ILicenseMailer? mailer, ILogger<LicenseDelivery> logger)
{
    /// <summary>Longest address accepted (RFC 5321's path limit).</summary>
    private const int MaxAddressLength = 254;

    /// <summary>
    /// Sends the current license of <paramref name="licenseId"/> to <paramref name="address"/> unless that
    /// version went out already. Nothing is sent for a missing or revoked record, or one without scopes
    /// (the webhook flagged it for an admin). Throws when the mailer fails in a way a retry may cure, so the
    /// caller leaves the webhook unhandled for the provider to deliver again.
    /// </summary>
    /// <param name="licenseId">License id.</param>
    /// <param name="address">The buyer's e-mail address from the event, or null when it carries none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task DeliverAsync(string licenseId, string? address, CancellationToken cancellationToken)
    {
        if (mailer is null)
        {
            LogDeliveryOff(logger, licenseId);
            return;
        }

        var record = await store.GetAsync(licenseId, cancellationToken);
        if (record is null || record.Revoked || record.Scopes.Count == 0)
        {
            return;
        }

        var contentKey = ContentKey(record);
        if (await store.DeliveredAsync(licenseId, contentKey, cancellationToken))
        {
            LogAlreadyDelivered(logger, licenseId);
            return;
        }

        if (!IsDeliverable(address))
        {
            LogNoAddress(logger, licenseId);
            await store.AuditAsync(licenseId, "attention", "the license e-mail was not sent: the event carries no usable customer address", cancellationToken);
            return;
        }

        var mail = LicenseMail.For(record, issuer.Issue(record), address);
        if (await mailer.SendAsync(mail, $"{licenseId}/{contentKey}", cancellationToken) == MailOutcome.Rejected)
        {
            await store.AuditAsync(licenseId, "attention", "the e-mail provider refused the license e-mail; send it by hand (admin show)", cancellationToken);
            return;
        }

        await store.MarkDeliveredAsync(licenseId, contentKey, $"license e-mailed: {record.Seats} seats, {record.Kind.ToString().ToLowerInvariant()}", cancellationToken);
        LogDelivered(logger, licenseId, record.Seats);
    }

    /// <summary>
    /// Stands for what a license grants: SHA-256 over licensee, plan, kind, seats and scopes, in hex. The
    /// period is left out on purpose, so a renewal alone sends no e-mail.
    /// </summary>
    /// <param name="record">The license record.</param>
    public static string ContentKey(LicenseRecord record)
    {
        var text = string.Join('\n', [record.Licensee, record.Plan, record.Kind.ToString(), record.Seats.ToString(System.Globalization.CultureInfo.InvariantCulture), .. record.Scopes.Order(StringComparer.Ordinal)]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>True for a single plain address; anything else (lists, display names, line breaks) is not sent to.</summary>
    /// <param name="address">The address from the event.</param>
    private static bool IsDeliverable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? address) =>
        address is { Length: > 0 and <= MaxAddressLength }
        && MailAddress.TryCreate(address, out var parsed)
        && parsed.Address == address;

    [LoggerMessage(Level = LogLevel.Debug, Message = "License {LicenseId}: no e-mail provider is configured, the license is not e-mailed")]
    private static partial void LogDeliveryOff(ILogger logger, string licenseId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "License {LicenseId}: this version was e-mailed before")]
    private static partial void LogAlreadyDelivered(ILogger logger, string licenseId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License {LicenseId} needs attention: the event carries no usable customer address")]
    private static partial void LogNoAddress(ILogger logger, string licenseId);

    [LoggerMessage(Level = LogLevel.Information, Message = "License {LicenseId} e-mailed to its buyer ({Seats} seats)")]
    private static partial void LogDelivered(ILogger logger, string licenseId, int seats);
}
