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
    /// The genuine licenses found, one per license id in the order their id first appears, each the newest signed
    /// of its versions (the CI secret, a refreshed or installed copy). Empty, with the first one's reason, when none
    /// is genuine.
    /// </summary>
    /// <param name="licenses">License strings in order of precedence.</param>
    /// <param name="trustedKeys">Public keys by key id.</param>
    /// <param name="error">Why none is genuine; null when one is.</param>
    public static IReadOnlyList<(string Text, LicenseClaims Claims)> Candidates(IReadOnlyList<string> licenses, IReadOnlyDictionary<string, byte[]> trustedKeys, out string? error)
    {
        error = null;
        var newest = new List<(string Text, LicenseClaims Claims)>();
        foreach (var text in licenses)
        {
            if (License.Verify(text, trustedKeys, out var reason) is not { } claims)
            {
                error ??= reason;
                continue;
            }

            var index = newest.FindIndex(n => n.Claims.Id == claims.Id);
            if (index < 0)
            {
                newest.Add((License.Normalize(text), claims));
            }
            else if (claims.Issued > newest[index].Claims.Issued)
            {
                newest[index] = (License.Normalize(text), claims);
            }
        }

        if (newest.Count > 0)
        {
            error = null;
        }

        return newest;
    }

    /// <summary>
    /// The first of <see cref="Candidates"/>: the license the first genuine string names, in its newest version.
    /// Null, with the first one's reason, when none is genuine.
    /// </summary>
    /// <param name="licenses">License strings in order of precedence.</param>
    /// <param name="trustedKeys">Public keys by key id.</param>
    /// <param name="error">Why none is genuine; null when one is.</param>
    public static (string Text, LicenseClaims Claims)? Best(IReadOnlyList<string> licenses, IReadOnlyDictionary<string, byte[]> trustedKeys, out string? error) =>
        Candidates(licenses, trustedKeys, out error) is [var first, ..] ? first : null;

    /// <summary>
    /// What a message adds when the license does not cover the run: "; every test runs" once the
    /// license is enforced, nothing before (tests are selected whatever the outcome).
    /// </summary>
    /// <param name="enforced">Whether the license decides the run (<see cref="LicenseKeys.Enforced"/>).</param>
    public static string EveryTestRuns(bool enforced) => enforced ? "; every test runs" : "";

    /// <summary>Checks <paramref name="input"/> against the public keys TestDetta trusts.</summary>
    /// <param name="input">What was found.</param>
    /// <param name="trustedKeys">Public keys by key id.</param>
    /// <param name="enforced">Whether the license decides the run; the messages say "every test runs" only then.</param>
    public static LicenseStatus Check(LicenseCheckInput input, IReadOnlyDictionary<string, byte[]> trustedKeys, bool enforced = LicenseKeys.Enforced)
    {
        var runs = EveryTestRuns(enforced);
        if (input.IsPublic)
        {
            return new LicenseStatus(LicenseState.PublicRepository, "public repository: no license needed", null, null);
        }

        if (input.Licenses.Count == 0)
        {
            return new LicenseStatus(LicenseState.Missing, $"no TestDetta license found (set TESTDETTA_LICENSE or TESTDETTA_LICENSE_FILE){runs}", null, null);
        }

        var candidates = Candidates(input.Licenses, trustedKeys, out var error);
        if (candidates.Count == 0)
        {
            return new LicenseStatus(LicenseState.Invalid, $"the TestDetta license is not valid: {error}{runs}", null, null);
        }

        // An old license left in the CI secret must not hide a current one installed or refreshed next to it: the
        // first that covers the run counts; when none does, the first one's reason is reported.
        LicenseStatus? first = null;
        foreach (var candidate in candidates)
        {
            var status = Evaluate(input, candidate.Claims, enforced);
            if (status.AllowsSelection)
            {
                return status;
            }

            first ??= status;
        }

        return first!;
    }

    /// <summary>Checks one genuine license against the run: scope, dates and active committers.</summary>
    /// <param name="input">What was found.</param>
    /// <param name="claims">The license's verified claims.</param>
    /// <param name="enforced">Whether the license decides the run.</param>
    private static LicenseStatus Evaluate(LicenseCheckInput input, LicenseClaims claims, bool enforced)
    {
        var runs = EveryTestRuns(enforced);

        if (input.Identity is null)
        {
            return new LicenseStatus(LicenseState.WrongScope, $"license {claims.Id}: the repository has no origin remote to match its scope (set TESTDETTA_LICENSE_SCOPE){runs}", claims, null);
        }

        if (!claims.Scopes.Any(scope => RepositoryIdentity.Matches(input.Identity, scope)))
        {
            return new LicenseStatus(LicenseState.WrongScope, $"license {claims.Id} does not cover {input.Identity}{runs}", claims, null);
        }

        if (input.Now < claims.ValidFrom)
        {
            return new LicenseStatus(LicenseState.NotYetValid, $"license {claims.Id} starts on {claims.ValidFrom:yyyy-MM-dd}{runs}", claims, null);
        }

        if (input.Now > claims.ValidUntil)
        {
            return new LicenseStatus(LicenseState.Expired, $"license {claims.Id} expired on {claims.ValidUntil:yyyy-MM-dd}{runs}", claims, null);
        }

        var committers = new ActiveCommitters(input.Authors, input.ExtraBots);
        var active = committers.At(input.Now).Count;
        var allowed = claims.Seats + Math.Max(1, (int)Math.Ceiling(claims.Seats * 0.2));
        if (active > allowed)
        {
            var daysOver = committers.DaysOver(input.Now, allowed, OverSeatsDays + 1);
            var over = $"{active} active committers in the last 30 days; license {claims.Id} covers {claims.Seats}";
            return daysOver > OverSeatsDays
                ? new LicenseStatus(LicenseState.OverSeats, $"{over}, for more than {OverSeatsDays} days{(enforced ? "; every test runs until seats are added" : "")}", claims, active)
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
