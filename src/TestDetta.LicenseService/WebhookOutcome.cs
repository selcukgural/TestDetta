namespace TestDetta.LicenseService;

/// <summary>
/// What handling one payment provider webhook did. It is kept with the handled webhook and logged, so a
/// delivery the provider shows as 200 can be traced to its effect without keeping the event's body, which
/// carries the customer's name and e-mail address.
/// </summary>
public enum WebhookOutcome
{
    /// <summary>The license record was written from the event's state.</summary>
    Saved,

    /// <summary>The record already holds a newer state of the subscription, so nothing was written.</summary>
    Stale,

    /// <summary>A checkout that never completed, for a license that does not exist yet: no record.</summary>
    Unpaid,

    /// <summary>An admin must look (no seat count, a refund); the audit says what (<c>admin attention</c>).</summary>
    NeedsAttention,

    /// <summary>Not an event the service acts on (neither <c>subscription.*</c> nor <c>order.refunded</c>).</summary>
    Ignored,
}
