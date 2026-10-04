namespace TestDetta.Core.Licensing;

/// <summary>What a license check looks at, gathered by <see cref="LicenseInputs"/> or by a test.</summary>
/// <param name="Licenses">License strings found, in order of precedence (<see cref="LicenseInputs.FindLicenses"/>).</param>
/// <param name="Identity">The repository's normalised identity, or null when unknown.</param>
/// <param name="Authors">Commit authors of the last 60 days on HEAD.</param>
/// <param name="Now">The current time: the later of the clock and HEAD's commit time.</param>
/// <param name="IsPublic">True when the CI says the repository is public.</param>
public sealed record LicenseCheckInput(IReadOnlyList<string> Licenses, string? Identity, IReadOnlyList<CommitAuthor> Authors, DateTimeOffset Now, bool IsPublic)
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

    /// <summary>
    /// The license to use of those found: the first genuine one decides which license it is, and of
    /// its genuine versions (the CI secret, a refreshed or installed copy) the newest signed counts.
    /// Null, with the first one's reason, when none is genuine.
    /// </summary>
    /// <param name="licenses">License strings in order of precedence.</param>
    /// <param name="trustedKeys">Public keys by key id.</param>
    /// <param name="error">Why none is genuine; null when one is.</param>
    public static (string Text, LicenseClaims Claims)? Best(IReadOnlyList<string> licenses, IReadOnlyDictionary<string, byte[]> trustedKeys, out string? error)
    {
        error = null;
        var genuine = new List<(string Text, LicenseClaims Claims)>();
        foreach (var text in licenses)
        {
            if (License.Verify(text, trustedKeys, out var reason) is { } claims)
            {
                genuine.Add((text.Trim(), claims));
            }
            else
            {
                error ??= reason;
            }
        }

        if (genuine.Count == 0)
        {
            return null;
        }

        error = null;
        var id = genuine[0].Claims.Id;
        return genuine.Where(g => g.Claims.Id == id).MaxBy(static g => g.Claims.Issued);
    }

    /// <summary>Checks <paramref name="input"/> against the public keys TestDetta trusts.</summary>
    /// <param name="input">What was found.</param>
    /// <param name="trustedKeys">Public keys by key id.</param>
    public static LicenseStatus Check(LicenseCheckInput input, IReadOnlyDictionary<string, byte[]> trustedKeys)
    {
        if (input.IsPublic)
        {
            return new LicenseStatus(LicenseState.PublicRepository, "public repository: no license needed", null, null);
        }

        if (input.Licenses.Count == 0)
        {
            return new LicenseStatus(LicenseState.Missing, "no TestDetta license found (set TESTDETTA_LICENSE or TESTDETTA_LICENSE_FILE); every test runs", null, null);
        }

        if (Best(input.Licenses, trustedKeys, out var error) is not { } best)
        {
            return new LicenseStatus(LicenseState.Invalid, $"the TestDetta license is not valid: {error}; every test runs", null, null);
        }

        var claims = best.Claims;

        if (input.Identity is null)
        {
            return new LicenseStatus(LicenseState.WrongScope, $"license {claims.Id}: the repository has no origin remote to match its scope (set TESTDETTA_LICENSE_SCOPE); every test runs", claims, null);
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
