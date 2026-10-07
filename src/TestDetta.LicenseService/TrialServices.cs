namespace TestDetta.LicenseService;

/// <summary>What the trial endpoints need from the service, bound from its services.</summary>
/// <param name="Settings">Service settings: whether trials are on, their limits.</param>
/// <param name="Challenge">Issues and checks proof-of-work tokens.</param>
/// <param name="Trials">Trial reservations.</param>
/// <param name="Store">License records.</param>
/// <param name="Delivery">E-mails the license.</param>
/// <param name="Clock">The current time.</param>
/// <param name="SigningKey">Derives the address HMAC key.</param>
/// <param name="Logger">Receives outcomes by license id only, never the address.</param>
public sealed record TrialServices(
    ServiceSettings Settings,
    TrialChallenge Challenge,
    TrialStore Trials,
    LicenseStore Store,
    LicenseDelivery Delivery,
    TimeProvider Clock,
    SigningKey SigningKey,
    ILogger<TrialStore> Logger);
