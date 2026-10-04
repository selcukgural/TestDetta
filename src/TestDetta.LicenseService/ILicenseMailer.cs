namespace TestDetta.LicenseService;

/// <summary>
/// Sends the e-mail that hands a buyer their license string (docs/design/licensing.md, "Delivery by
/// e-mail"). An interface so tests can see what would be sent without an e-mail provider.
/// </summary>
public interface ILicenseMailer
{
    /// <summary>
    /// Sends <paramref name="mail"/>. Returns <see cref="MailOutcome.Rejected"/> when the provider refuses it
    /// for good (an invalid address, say), so the webhook is not retried for nothing; throws for failures a
    /// retry may cure (network, provider errors, rate limits), which leaves the webhook to the provider's retries.
    /// </summary>
    /// <param name="mail">What to send and to whom.</param>
    /// <param name="idempotencyKey">Same key, same e-mail: lets the provider drop a second send of one license version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<MailOutcome> SendAsync(LicenseMail mail, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>What became of a license e-mail.</summary>
public enum MailOutcome
{
    /// <summary>The provider accepted it.</summary>
    Sent,

    /// <summary>The provider refused it and a retry would not help; an admin must look.</summary>
    Rejected,
}
