using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService.Tests;

/// <summary>
/// The license service against a real Postgres (TESTDETTA_TEST_POSTGRES, default: the local server as the
/// current user), one fresh database per test, through the real HTTP pipeline.
/// </summary>
public sealed class LicenseServiceTests : IAsyncLifetime
{
    private static readonly string ServerConnection =
        Environment.GetEnvironmentVariable("TESTDETTA_TEST_POSTGRES") is { Length: > 0 } configured
            ? configured
            : $"Host=localhost;Username={Environment.UserName};Database=postgres";

    private readonly string _database = "testdetta_ls_" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Directory.CreateTempSubdirectory("testdetta-license-service-").FullName;
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

    private WebApplicationFactory<Program> Service(int perLicensePerHour = 600, int perAddressPerMinute = 60, string? trustedProxies = null, ILicenseMailer? mailer = null, int forwardedHops = 1) => new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    {
        host.UseSetting("Licenses:ForwardedHops", forwardedHops.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (mailer is not null)
        {
            host.ConfigureTestServices(services => services.AddSingleton(mailer));
        }

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
        string? scope = "github.com/Acme/*", string? endedAt = null, string? endsAt = null, string type = "subscription.updated",
        string? email = "billing@acme.com", string periodEnd = "2026-11-01T00:00:00Z") =>
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
                current_period_end = periodEnd,
                ends_at = endsAt,
                ended_at = endedAt,
                customer = new { name = "Acme Corp", email },
                product = new { name = "TestDetta Team" },
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
            request.Headers.Authorization = new AuthenticationHeaderValue("TestDetta", license);
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
        Assert.Equal(("Acme Corp", "TestDetta Team", 5, LicenseKind.Subscription, false), (record!.Licensee, record.Plan, record.Seats, record.Kind, record.Revoked));
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

    /// <summary>Keeps what would be e-mailed; <see cref="Next"/> decides the outcome of the coming sends.</summary>
    private sealed class FakeMailer : ILicenseMailer
    {
        public List<(LicenseMail Mail, string Key)> Sent { get; } = [];

        public Queue<Func<MailOutcome>> Next { get; } = [];

        public Task<MailOutcome> SendAsync(LicenseMail mail, string idempotencyKey, CancellationToken cancellationToken)
        {
            var outcome = Next.Count > 0 ? Next.Dequeue()() : MailOutcome.Sent;
            if (outcome == MailOutcome.Sent)
            {
                Sent.Add((mail, idempotencyKey));
            }

            return Task.FromResult(outcome);
        }
    }

    private string LicenseIn(LicenseMail mail) =>
        mail.Text.Split('\n').Select(static line => line.Trim()).Single(line => License.Verify(line, Trusted, out _) is not null);

    [Fact]
    public async Task A_new_subscription_emails_its_license_to_the_buyer_and_keeps_no_address()
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer: mailer);
        using var client = service.CreateClient();

        using var response = await Deliver(client, Subscription(type: "subscription.created"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (mail, _) = Assert.Single(mailer.Sent);
        Assert.Equal("billing@acme.com", mail.To);
        var claims = License.Verify(LicenseIn(mail), Trusted, out _)!;
        Assert.Equal(("P-sub-1", 5), (claims.Id, claims.Seats));
        Assert.Equal(["github.com/acme/*"], claims.Scopes);
        Assert.Contains("TESTDETTA_LICENSE", mail.Text, StringComparison.Ordinal);
        var audit = string.Join('\n', await Audit("P-sub-1"));
        Assert.Contains("deliver: license e-mailed: 5 seats, subscription", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("acme.com", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_change_in_what_the_license_grants_emails_it_again()
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer: mailer);
        using var client = service.CreateClient();
        (await Deliver(client, Subscription(type: "subscription.created"), "msg-1")).Dispose();

        // A renewal moves the period only: the refresh endpoint hands that out.
        (await Deliver(client, Subscription(modified: "2026-11-01T00:00:00Z", periodEnd: "2026-12-01T00:00:00Z"), "msg-2")).Dispose();
        (await Deliver(client, Subscription(modified: "2026-11-01T00:00:00Z", periodEnd: "2026-12-01T00:00:00Z"), "msg-3")).Dispose();
        Assert.Single(mailer.Sent);

        (await Deliver(client, Subscription(seats: 7, modified: "2026-11-02T00:00:00Z", periodEnd: "2026-12-01T00:00:00Z"), "msg-4")).Dispose();
        Assert.Equal(2, mailer.Sent.Count);
        Assert.Equal(7, License.Verify(LicenseIn(mailer.Sent[1].Mail), Trusted, out _)!.Seats);
        Assert.NotEqual(mailer.Sent[0].Key, mailer.Sent[1].Key);
    }

    [Fact]
    public async Task A_send_that_fails_for_now_leaves_the_event_to_the_providers_retry()
    {
        var mailer = new FakeMailer();
        mailer.Next.Enqueue(static () => throw new HttpRequestException("provider down"));
        using var service = Service(mailer: mailer);
        using var client = service.CreateClient();

        using var first = await Deliver(client, Subscription(type: "subscription.created"), "msg-1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.NotNull(await Record());
        Assert.Empty(mailer.Sent);

        using var retry = await Deliver(client, Subscription(type: "subscription.created"), "msg-1");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(mailer.Sent);
    }

    [Fact]
    public async Task A_refused_or_unaddressed_email_is_left_to_an_admin()
    {
        var mailer = new FakeMailer();
        mailer.Next.Enqueue(static () => MailOutcome.Rejected);
        using var service = Service(mailer: mailer);
        using var client = service.CreateClient();

        using var refused = await Deliver(client, Subscription(type: "subscription.created"), "msg-1");
        using var unaddressed = await Deliver(client, Subscription(email: "not an address", type: "subscription.created").Replace("sub-1", "sub-2", StringComparison.Ordinal), "msg-2");

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK], [refused.StatusCode, unaddressed.StatusCode]);
        Assert.Empty(mailer.Sent);
        Assert.Contains("attention: the e-mail provider refused the license e-mail", string.Join('\n', await Audit("P-sub-1")), StringComparison.Ordinal);
        Assert.Contains("attention: the license e-mail was not sent: the event carries no usable customer address", string.Join('\n', await Audit("P-sub-2")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoked_or_scopeless_licenses_are_not_emailed()
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer: mailer);
        using var client = service.CreateClient();

        (await Deliver(client, Subscription(scope: null, type: "subscription.created"), "msg-1")).Dispose();
        (await Deliver(client, Subscription(status: "canceled", endedAt: "2026-10-05T00:00:00Z", type: "subscription.revoked").Replace("sub-1", "sub-2", StringComparison.Ordinal), "msg-2")).Dispose();

        Assert.NotNull(await Record());
        Assert.True((await Record("P-sub-2"))!.Revoked);
        Assert.Empty(mailer.Sent);
    }

    /// <summary>Answers every request with one status and keeps the last request.</summary>
    private sealed class FixedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, MailOutcome.Sent)]
    [InlineData(HttpStatusCode.UnprocessableEntity, MailOutcome.Rejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    public async Task Resend_gets_a_plain_text_email_and_its_answer_decides_on_a_retry(HttpStatusCode status, MailOutcome? expected)
    {
        using var handler = new FixedHandler(status);
        using var http = new HttpClient(handler) { BaseAddress = ResendMailer.ApiBase };
        var settings = Settings() with { ResendApiKey = "re_test", MailFrom = "TestDetta <licenses@example.com>", MailReplyTo = "support@example.com" };
        var mailer = new ResendMailer(http, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResendMailer>.Instance);
        var mail = new LicenseMail("buyer@acme.com", "Your TestDetta license", "Hello \"Acme\" <b>");

        if (expected is null)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => mailer.SendAsync(mail, "P-1/abc", CancellationToken.None));
        }
        else
        {
            Assert.Equal(expected, await mailer.SendAsync(mail, "P-1/abc", CancellationToken.None));
        }

        Assert.Equal(new Uri("https://api.resend.com/emails"), handler.Request!.RequestUri);
        Assert.Equal("Bearer re_test", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("P-1/abc", Assert.Single(handler.Request.Headers.GetValues("Idempotency-Key")));
        using var body = System.Text.Json.JsonDocument.Parse(handler.Body);
        var root = body.RootElement;
        Assert.Equal(("TestDetta <licenses@example.com>", "buyer@acme.com", "support@example.com", "Hello \"Acme\" <b>"),
            (root.GetProperty("from").GetString(), root.GetProperty("to")[0].GetString(), root.GetProperty("reply_to").GetString(), root.GetProperty("text").GetString()));
        Assert.False(root.TryGetProperty("html", out _));
    }

    [Fact]
    public void An_email_provider_needs_a_sender()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licenses:ConnectionString"] = "Host=x",
            ["Licenses:KeyId"] = "k1",
            ["Licenses:KeyFile"] = "k.pem",
            ["Licenses:PublicBaseUrl"] = "https://license.example",
            ["Licenses:ResendApiKey"] = "re_test",
        }).Build();

        Assert.Contains("MailFrom", Assert.Throws<InvalidOperationException>(() => (object)ServiceSettings.From(configuration)).Message, StringComparison.Ordinal);
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
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Behind_two_proxies_the_client_is_the_second_address_from_the_right(int hops, bool separate)
    {
        using var service = Service(perAddressPerMinute: 1, trustedProxies: "*", forwardedHops: hops);
        using var client = service.CreateClient();
        async Task<HttpStatusCode> From(string client1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            // As Firebase Hosting in front of Cloud Run sends it: the client, then Hosting's own address.
            request.Headers.Add("X-Forwarded-For", $"{client1}, 192.0.2.50");
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.1"));
        Assert.Equal(separate ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, await From("203.0.113.2"));
    }

    [Fact]
    public void Secret_files_set_settings_and_win_over_other_sources()
    {
        var root = Directory.CreateTempSubdirectory("testdetta-secrets-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "db"));
            File.WriteAllText(Path.Combine(root, "db", "Licenses__ConnectionString"), "Host=from-file");
            File.WriteAllText(Path.Combine(root, "Licenses__ResendApiKey"), "re_file");
            var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Licenses:ConnectionString"] = "Host=from-env" });

            var configuration = TestDetta.LicenseService.SecretFiles.Add(builder, root).Build();

            Assert.Equal("Host=from-file", configuration["Licenses:ConnectionString"]);
            Assert.Equal("re_file", configuration["Licenses:ResendApiKey"]);
            Assert.Same(builder, TestDetta.LicenseService.SecretFiles.Add(builder, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
