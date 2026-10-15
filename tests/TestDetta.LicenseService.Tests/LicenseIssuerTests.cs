using System.Security.Cryptography;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService.Tests;

public sealed class LicenseIssuerTests : IDisposable
{
    private static readonly DateTimeOffset PeriodEnd = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly SigningKey _key;

    public LicenseIssuerTests() => _key = new SigningKey("k1", _ecdsa.ExportPkcs8PrivateKeyPem());

    public void Dispose()
    {
        _key.Dispose();
        _ecdsa.Dispose();
    }

    [Theory]
    [InlineData(-29)]
    [InlineData(-14)]
    [InlineData(-1)]
    [InlineData(0)]
    public void A_monthly_subscription_refreshed_on_time_never_warns_before_its_renewal(int daysToEnd)
    {
        var signed = PeriodEnd.AddDays(daysToEnd);
        var license = Issuer(signed).Issue(Record(LicenseKind.Subscription, signed.AddDays(-1)));

        // Checked up to a day after signing (refresh runs at most once a day), the renewal not yet arrived.
        var status = LicenseCheck.Check(new LicenseCheckInput([license], "github.com/acme/app", [], signed.AddHours(23), false), Trusted);

        Assert.Equal(LicenseState.Valid, status.State);
    }

    [Fact]
    public void An_annual_license_carries_no_refresh_link()
    {
        var claims = License.Verify(Issuer(PeriodEnd).Issue(Record(LicenseKind.Annual, PeriodEnd.AddYears(-1))), Trusted, out _)!;

        Assert.Null(claims.Refresh);
    }

    [Theory]
    [InlineData(LicenseKind.Subscription)]
    [InlineData(LicenseKind.Trial)]
    public void Subscriptions_and_trials_carry_their_refresh_link(LicenseKind kind)
    {
        var claims = License.Verify(Issuer(PeriodEnd).Issue(Record(kind, PeriodEnd.AddDays(-30))), Trusted, out _)!;

        Assert.Equal(new Uri("https://license.example/v1/licenses/L-1"), claims.Refresh);
    }

    private Dictionary<string, byte[]> Trusted => new() { ["k1"] = _ecdsa.ExportSubjectPublicKeyInfo() };

    private LicenseIssuer Issuer(DateTimeOffset now) => new(_key, new Uri("https://license.example/"), new FixedClock(now));

    private static LicenseRecord Record(LicenseKind kind, DateTimeOffset validFrom) =>
        new("L-1", "Acme", "Team", kind, 5, ["github.com/acme/*"], validFrom, PeriodEnd, false, "manual");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
