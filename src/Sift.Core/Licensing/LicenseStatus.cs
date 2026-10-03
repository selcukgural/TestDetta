namespace Sift.Core.Licensing;

/// <summary>The outcome of a license check.</summary>
public enum LicenseState
{
    /// <summary>A valid license covers the repository.</summary>
    Valid,

    /// <summary>Valid, with something to act on soon: expiry within 14 days, or active committers above the seats.</summary>
    Warning,

    /// <summary>The CI says the repository is public, which needs no license.</summary>
    PublicRepository,

    /// <summary>No license was found.</summary>
    Missing,

    /// <summary>A license was found but is not genuine or cannot be read.</summary>
    Invalid,

    /// <summary>The license is for other repositories, or the repository's identity is unknown.</summary>
    WrongScope,

    /// <summary>The license starts later.</summary>
    NotYetValid,

    /// <summary>The license has run out.</summary>
    Expired,

    /// <summary>Active committers have stayed above the seats, tolerance included, for more than 30 days.</summary>
    OverSeats,
}

/// <summary>What a license check found, and whether tests may be selected.</summary>
/// <param name="State">The outcome.</param>
/// <param name="Message">One sentence for the log and the summaries: what was found and what to do.</param>
/// <param name="Claims">The verified license; null when none.</param>
/// <param name="ActiveCommitters">Active committers counted; null when not counted.</param>
public sealed record LicenseStatus(LicenseState State, string Message, LicenseClaims? Claims, int? ActiveCommitters)
{
    /// <summary>True when tests may be selected; otherwise every test runs (docs/design/licensing.md).</summary>
    public bool AllowsSelection => State is LicenseState.Valid or LicenseState.Warning or LicenseState.PublicRepository;
}
