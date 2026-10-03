using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sift.Core.Licensing;

namespace Sift.LicenseService.Tests;

/// <summary>
/// The license service against a real Postgres (SIFT_TEST_POSTGRES, default: the local server as the
/// current user), one fresh database per test, through the real HTTP pipeline.
/// </summary>
public sealed class LicenseServiceTests : IAsyncLifetime
{
    private static readonly string ServerConnection =
        Environment.GetEnvironmentVariable("SIFT_TEST_POSTGRES") is { Length: > 0 } configured
            ? configured
            : $"Host=localhost;Username={Environment.UserName};Database=postgres";

    private readonly string _database = "sift_ls_" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Directory.CreateTempSubdirectory("sift-license-service-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string _connection = "";
    private string _keyFile = "";

    public async Task InitializeAsync()
    {
        await using (var server = new NpgsqlConnection(ServerConnection))
        {
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {_database}", server);
            await create.ExecuteNonQueryAsync();
        }

        _connection = new NpgsqlConnectionStringBuilder(ServerConnection) { Database = _database }.ConnectionString;
        _keyFile = Path.Combine(_directory, "signing.pem");
        await File.WriteAllTextAsync(_keyFile, _key.ExportPkcs8PrivateKeyPem());
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using (var server = new NpgsqlConnection(ServerConnection))
        {
            await server.OpenAsync();
            await using var drop = new NpgsqlCommand($"drop database if exists {_database} with (force)", server);
            await drop.ExecuteNonQueryAsync();
        }

        _key.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private ServiceSettings Settings(int perLicensePerHour = 600) =>
        new(_connection, "k1", _keyFile, new Uri("https://license.example/")) { PerLicensePerHour = perLicensePerHour };

    private Dictionary<string, byte[]> Trusted => new() { ["k1"] = _key.ExportSubjectPublicKeyInfo() };

    private WebApplicationFactory<Program> Service(int perLicensePerHour = 600, int perAddressPerMinute = 60, string? trustedProxies = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    {
        host.UseSetting("Licenses:PerAddressPerMinute", perAddressPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (trustedProxies is not null)
        {
            host.UseSetting("Licenses:TrustedProxies", trustedProxies);
        }

        host.UseSetting("Licenses:ConnectionString", _connection);
        host.UseSetting("Licenses:KeyId", "k1");
        host.UseSetting("Licenses:KeyFile", _keyFile);
        host.UseSetting("Licenses:PublicBaseUrl", "https://license.example/");
        host.UseSetting("Licenses:PerLicensePerHour", perLicensePerHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
        host.UseSetting("Licenses:PolarWebhookSecret", WebhookSecret);
    });

    private static readonly string WebhookSecret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    private static string Subscription(string status = "active", int? seats = 5, string modified = "2026-10-03T10:00:00Z",
        string? scope = "github.com/Acme/*", string? endedAt = null, string? endsAt = null, string type = "subscription.updated") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type,
            timestamp = modified,
            api_version = "2026-10",
            data = new
            {
                id = "sub-1",
                status,
                seats,
                modified_at = modified,
                started_at = "2026-10-01T00:00:00Z",
                current_period_start = "2026-10-01T00:00:00Z",
                current_period_end = "2026-11-01T00:00:00Z",
                ends_at = endsAt,
                ended_at = endedAt,
                customer = new { name = "Acme Corp", email = "billing@acme.com" },
                product = new { name = "Sift Team" },
                custom_field_data = scope is null ? null : new Dictionary<string, string> { ["scope"] = scope },
            },
        });

    private static async Task<HttpResponseMessage> Deliver(HttpClient client, string body, string id = "msg-1", DateTimeOffset? at = null, string? secret = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = Convert.FromBase64String((secret ?? WebhookSecret)[6..]);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/polar") { Content = new StringContent(body) };
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", $"v1a,ignored v1,{signature}");
        return await client.SendAsync(request);
    }

    private async Task<LicenseRecord?> Record(string id = "P-sub-1")
    {
        await using var source = NpgsqlDataSource.Create(_connection);
        return await new LicenseStore(source).GetAsync(id, CancellationToken.None);
    }

    private async Task<(int Code, string Out, string Err)> Admin(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await AdminCommand.RunAsync(args, Settings(), stdout, stderr);
        return (code, stdout.ToString().Trim(), stderr.ToString());
    }

    private Task<(int Code, string Out, string Err)> IssueAcme(string id = "L-1", string until = "2027-10-01") =>
        Admin("issue", "--id", id, "--licensee", "Acme", "--seats", "5", "--scope", "github.com/acme/*", "--scope", "git.acme.corp/*",
            "--kind", "annual", "--from", "2026-10-01", "--until", until, "--note", "invoice 2026-101");

    private static async Task<HttpResponseMessage> Refresh(HttpClient client, string id, string? license)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/licenses/{id}");
        if (license is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Sift", license);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Issued_license_is_signed_by_the_service_and_carries_the_record()
    {
        var (code, license, _) = await IssueAcme();

        Assert.Equal(0, code);
        var claims = License.Verify(license, Trusted, out var error);
        Assert.Null(error);
        Assert.Equal(("L-1", "Acme", 5, LicenseKind.Annual), (claims!.Id, claims.Licensee, claims.Seats, claims.Kind));
        Assert.Equal(["github.com/acme/*", "git.acme.corp/*"], claims.Scopes);
        // An annual license keeps 30 days of slack after its term for the new file to arrive.
        Assert.Equal(new DateTimeOffset(2027, 10, 31, 0, 0, 0, TimeSpan.Zero), claims.ValidUntil);
        Assert.Equal(new Uri("https://license.example/v1/licenses/L-1"), claims.Refresh);
    }

    [Fact]
    public async Task Refresh_returns_the_current_license_after_seats_are_added()
    {
        var (_, license, _) = await IssueAcme();
        Assert.Equal(0, (await Admin("update", "--id", "L-1", "--seats", "7", "--note", "added two seats")).Code);
        using var service = Service();
        using var client = service.CreateClient();

        using var response = await Refresh(client, "L-1", license);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(7, License.Verify(await response.Content.ReadAsStringAsync(), Trusted, out _)!.Seats);
        Assert.Contains("update: added two seats (seats 5 -> 7, until 2027-10-01 -> 2027-10-01)", (await Admin("show", "--id", "L-1")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_license_may_ask_for_its_renewal()
    {
        var (_, expired, _) = await IssueAcme(until: "2025-01-01");
        await Admin("update", "--id", "L-1", "--until", "2027-01-01", "--note", "renewed");
        using var service = Service();
        using var client = service.CreateClient();

        using var response = await Refresh(client, "L-1", expired);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(License.Verify(await response.Content.ReadAsStringAsync(), Trusted, out _)!.ValidUntil.Year == 2027);
    }

    [Fact]
    public async Task Every_request_that_does_not_prove_the_license_gets_the_same_not_found()
    {
        var (_, license, _) = await IssueAcme();
        var (_, other, _) = await IssueAcme("L-2");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = License.Sign(License.Verify(license, Trusted, out _)! with { Seats = 500 }, stranger);
        using var service = Service();
        using var client = service.CreateClient();

        List<HttpResponseMessage> responses =
        [
            await Refresh(client, "L-1", null),
            await Refresh(client, "L-1", "garbage"),
            await Refresh(client, "L-1", forged),
            await Refresh(client, "L-1", other),
            await Refresh(client, "L-9", license),
        ];
        await Admin("revoke", "--id", "L-1", "--note", "refund");
        responses.Add(await Refresh(client, "L-1", license));

        Assert.All(responses, static r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single(responses.Select(static r => r.Content.ReadAsStringAsync().Result).Distinct());
        responses.ForEach(static r => r.Dispose());
    }

    [Fact]
    public async Task Refreshes_of_one_license_are_rate_limited()
    {
        var (_, license, _) = await IssueAcme();
        using var service = Service(perLicensePerHour: 2);
        using var client = service.CreateClient();

        using var first = await Refresh(client, "L-1", license);
        using var second = await Refresh(client, "L-1", license);
        using var third = await Refresh(client, "L-1", license);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], [first.StatusCode, second.StatusCode, third.StatusCode]);
    }

    [Fact]
    public void Signature_check_matches_the_standard_webhooks_test_vector()
    {
        // From the Standard Webhooks specification's reference libraries.
        var body = System.Text.Encoding.UTF8.GetBytes("""{"test": 2432232314}""");
        var at = DateTimeOffset.FromUnixTimeSeconds(1614265330);
        const string secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

        Assert.True(PolarWebhook.Verify(secret, "msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", body, at));
        Assert.False(PolarWebhook.Verify(secret, "msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", [.. body, (byte)' '], at));
        Assert.False(PolarWebhook.Verify(secret, "msg_p5jXN8AQM9LWM0D4loKWxJek", "1614265330", "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", body, at.AddMinutes(6)));
    }

    [Fact]
    public async Task A_subscription_becomes_a_license_without_the_customers_email()
    {
        using var service = Service();
        using var client = service.CreateClient();

        using var response = await Deliver(client, Subscription(type: "subscription.created"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await Record();
        Assert.Equal(("Acme Corp", "Sift Team", 5, LicenseKind.Subscription, false), (record!.Licensee, record.Plan, record.Seats, record.Kind, record.Revoked));
        Assert.Equal(["github.com/acme/*"], record.Scopes);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), record.PeriodEnd);
        var (_, show, _) = await Admin("show", "--id", "P-sub-1");
        Assert.DoesNotContain("billing@acme.com", show, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Late_and_repeated_deliveries_never_bring_an_older_state_back()
    {
        using var service = Service();
        using var client = service.CreateClient();

        (await Deliver(client, Subscription(seats: 7, modified: "2026-10-03T12:00:00Z"), "msg-2")).Dispose();
        (await Deliver(client, Subscription(seats: 5, modified: "2026-10-03T10:00:00Z"), "msg-1")).Dispose();
        Assert.Equal(7, (await Record())!.Seats);

        // The same delivery again, even with other content, is handled once.
        (await Deliver(client, Subscription(seats: 9, modified: "2026-10-03T13:00:00Z"), "msg-2")).Dispose();
        Assert.Equal(7, (await Record())!.Seats);
    }

    [Fact]
    public async Task Cancelled_runs_to_its_end_and_ended_stops_renewals()
    {
        using var service = Service();
        using var client = service.CreateClient();
        (await Deliver(client, Subscription(), "msg-1")).Dispose();

        (await Deliver(client, Subscription(status: "active", endsAt: "2026-11-01T00:00:00Z", modified: "2026-10-04T00:00:00Z", type: "subscription.canceled"), "msg-2")).Dispose();
        Assert.False((await Record())!.Revoked);

        (await Deliver(client, Subscription(status: "canceled", endedAt: "2026-10-05T00:00:00Z", modified: "2026-10-05T00:00:00Z", type: "subscription.revoked"), "msg-3")).Dispose();
        Assert.True((await Record())!.Revoked);
        var (_, show, _) = await Admin("show", "--id", "P-sub-1");
        using var refresh = await Refresh(client, "P-sub-1", show.Split('\n')[^1].Trim());
        Assert.Equal(HttpStatusCode.NotFound, refresh.StatusCode);
    }

    [Fact]
    public async Task Forged_stale_or_unconfigured_deliveries_store_nothing()
    {
        using (var service = Service())
        using (var client = service.CreateClient())
        {
            using var forged = await Deliver(client, Subscription(), secret: "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
            using var stale = await Deliver(client, Subscription(), "msg-2", DateTimeOffset.UtcNow.AddMinutes(-10));
            Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized], [forged.StatusCode, stale.StatusCode]);
        }

        using var unconfigured = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Licenses:ConnectionString", _connection);
            host.UseSetting("Licenses:KeyId", "k1");
            host.UseSetting("Licenses:KeyFile", _keyFile);
            host.UseSetting("Licenses:PublicBaseUrl", "https://license.example/");
        });
        using var unconfiguredClient = unconfigured.CreateClient();
        using var off = await Deliver(unconfiguredClient, Subscription(), "msg-3");
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Null(await Record());
    }

    [Fact]
    public async Task Subscriptions_without_seats_or_payment_make_no_license()
    {
        using var service = Service();
        using var client = service.CreateClient();

        (await Deliver(client, Subscription(seats: null), "msg-1")).Dispose();
        (await Deliver(client, Subscription(status: "incomplete") .Replace("sub-1", "sub-2", StringComparison.Ordinal), "msg-2")).Dispose();

        Assert.Null(await Record());
        Assert.Null(await Record("P-sub-2"));
        Assert.Contains("attention: polar subscription.updated: the subscription has no seat count", (await Admin("show", "--id", "P-sub-1")).Err + string.Join('\n', await Audit("P-sub-1")), StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<string>> Audit(string id)
    {
        await using var source = NpgsqlDataSource.Create(_connection);
        return await new LicenseStore(source).AuditAsync(id, CancellationToken.None);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("*", true)]
    public async Task Forwarded_addresses_count_only_behind_a_trusted_proxy(string? trustedProxies, bool separate)
    {
        using var service = Service(perAddressPerMinute: 1, trustedProxies: trustedProxies);
        using var client = service.CreateClient();
        async Task<HttpStatusCode> From(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            request.Headers.Add("X-Forwarded-For", address);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.1"));
        // Another address gets its own budget only when the header is believed.
        Assert.Equal(separate ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, await From("203.0.113.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await From("203.0.113.2"));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::9", "2001:db8:1:2::/64")]
    public void Ipv6_addresses_share_their_64_network(string address, string partition) =>
        Assert.Equal(partition, ClientPartition.Of(System.Net.IPAddress.Parse(address)));

    [Fact]
    public async Task Admin_refuses_incomplete_or_invalid_input()
    {
        Assert.Equal(2, (await Admin("issue", "--id", "L-1", "--licensee", "Acme")).Code);
        Assert.Equal(2, (await Admin("issue", "--id", "L-1", "--licensee", "Acme", "--seats", "0", "--scope", "x", "--kind", "annual",
            "--from", "2026-10-01", "--until", "2027-10-01", "--note", "n")).Code);
        Assert.Equal(1, (await Admin("revoke", "--id", "L-404", "--note", "n")).Code);
        Assert.Equal(2, (await Admin("frobnicate")).Code);
        var (code, publicKey, _) = await Admin("public-key");
        Assert.Equal(0, code);
        Assert.Equal($"k1 {Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())}", publicKey);
    }
}
