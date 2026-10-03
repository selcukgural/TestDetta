namespace Sift.Core.Licensing;

/// <summary>What a license check looks at, gathered by <see cref="LicenseInputs"/> or by a test.</summary>
/// <param name="LicenseText">The license string found, or null.</param>
/// <param name="Identity">The repository's normalised identity, or null when unknown.</param>
/// <param name="Authors">Commit authors of the last 60 days on HEAD.</param>
/// <param name="Now">The current time: the later of the clock and HEAD's commit time.</param>
/// <param name="IsPublic">True when the CI says the repository is public.</param>
public sealed record LicenseCheckInput(string? LicenseText, string? Identity, IReadOnlyList<CommitAuthor> Authors, DateTimeOffset Now, bool IsPublic)
{
    /// <summary>The customer's own bot patterns.</summary>
    public IReadOnlyList<string> ExtraBots { get; init; } = [];
}

/// <summary>
/// Decides whether a license covers this run (docs/design/licensing.md): genuine, for this
/// repository, current, and with active committers within the seats or their 30-day warning period.
/// </summary>
public static class LicenseCheck
{
    /// <summary>Days before expiry from which every run warns.</summary>
    public const int ExpiryWarningDays = 14;

    /// <summary>Days active committers may stay above the seats (tolerance included) before selection stops.</summary>
    public const int OverSeatsDays = 30;

    /// <summary>Checks <paramref name="input"/> against the public keys Sift trusts.</summary>
    /// <param name="input">What was found.</param>
    /// <param name="trustedKeys">Public keys by key id.</param>
    public static LicenseStatus Check(LicenseCheckInput input, IReadOnlyDictionary<string, byte[]> trustedKeys)
    {
        if (input.IsPublic)
        {
            return new LicenseStatus(LicenseState.PublicRepository, "public repository: no license needed", null, null);
        }

        if (input.LicenseText is null)
        {
            return new LicenseStatus(LicenseState.Missing, "no Sift license found (set SIFT_LICENSE or SIFT_LICENSE_FILE); every test runs", null, null);
        }

        if (License.Verify(input.LicenseText, trustedKeys, out var error) is not { } claims)
        {
            return new LicenseStatus(LicenseState.Invalid, $"the Sift license is not valid: {error}; every test runs", null, null);
        }

        if (input.Identity is null)
        {
            return new LicenseStatus(LicenseState.WrongScope, $"license {claims.Id}: the repository has no origin remote to match its scope (set SIFT_LICENSE_SCOPE); every test runs", claims, null);
        }

        if (!claims.Scopes.Any(scope => RepositoryIdentity.Matches(input.Identity, scope)))
        {
            return new LicenseStatus(LicenseState.WrongScope, $"license {claims.Id} does not cover {input.Identity}; every test runs", claims, null);
        }

        if (input.Now < claims.ValidFrom)
        {
            return new LicenseStatus(LicenseState.NotYetValid, $"license {claims.Id} starts on {claims.ValidFrom:yyyy-MM-dd}; every test runs", claims, null);
        }

        if (input.Now > claims.ValidUntil)
        {
            return new LicenseStatus(LicenseState.Expired, $"license {claims.Id} expired on {claims.ValidUntil:yyyy-MM-dd}; every test runs", claims, null);
        }

        var committers = new ActiveCommitters(input.Authors, input.ExtraBots);
        var active = committers.At(input.Now).Count;
        var allowed = claims.Seats + Math.Max(1, (int)Math.Ceiling(claims.Seats * 0.2));
        if (active > allowed)
        {
            var daysOver = committers.DaysOver(input.Now, allowed, OverSeatsDays + 1);
            var over = $"{active} active committers in the last 30 days; license {claims.Id} covers {claims.Seats}";
            return daysOver > OverSeatsDays
                ? new LicenseStatus(LicenseState.OverSeats, $"{over}, for more than {OverSeatsDays} days; every test runs until seats are added", claims, active)
                : new LicenseStatus(LicenseState.Warning, $"{over}; add seats within {OverSeatsDays - daysOver + 1} day(s) to keep test selection", claims, active);
        }

        var daysLeft = (int)Math.Ceiling((claims.ValidUntil - input.Now).TotalDays);
        if (daysLeft <= ExpiryWarningDays)
        {
            return new LicenseStatus(LicenseState.Warning, $"license {claims.Id} expires in {daysLeft} day(s)", claims, active);
        }

        return new LicenseStatus(LicenseState.Valid, $"license {claims.Id} ({claims.Licensee}, {claims.Seats} seats): {active} active committer(s)", claims, active);
    }
}
