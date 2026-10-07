using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService.Tests;

/// <summary>
/// The site's card-free trial against a real Postgres (TESTDETTA_TEST_POSTGRES, as in
/// <see cref="LicenseServiceTests"/>), one fresh database per test, through the real HTTP pipeline.
/// </summary>
public sealed class TrialTests : IAsyncLifetime
{
    private static readonly string ServerConnection =
        Environment.GetEnvironmentVariable("TESTDETTA_TEST_POSTGRES") is { Length: > 0 } configured
            ? configured
            : $"Host=localhost;Username={Environment.UserName};Database=postgres";

    private const string Origin = "https://site.example";

    private readonly string _database = "testdetta_tr_" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Directory.CreateTempSubdirectory("testdetta-trials-").FullName;
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

    private Dictionary<string, byte[]> Trusted => new() { ["k1"] = _key.ExportSubjectPublicKeyInfo() };

    private WebApplicationFactory<Program> Service(FakeMailer mailer, bool enabled = true, int perDay = 50) => new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    {
        host.UseSetting("Licenses:ConnectionString", _connection);
        host.UseSetting("Licenses:KeyId", "k1");
        host.UseSetting("Licenses:KeyFile", _keyFile);
        host.UseSetting("Licenses:PublicBaseUrl", "https://license.example/");
        host.UseSetting("Licenses:ResendApiKey", "re_test");
        host.UseSetting("Licenses:MailFrom", "TestDetta <licenses@example.com>");
        host.UseSetting("Licenses:TrialsEnabled", enabled ? "true" : "false");
        host.UseSetting("Licenses:TrialOrigins", Origin);
        host.UseSetting("Licenses:TrialsPerDay", perDay.ToString(System.Globalization.CultureInfo.InvariantCulture));
        host.UseSetting("Licenses:TrialProofBits", "6");
        host.UseSetting("Licenses:TrialMinimumResponseMs", "0");
        host.ConfigureTestServices(services => services.AddSingleton<ILicenseMailer>(mailer));
    });

    /// <summary>Asks for a challenge and solves it, as the site's form does.</summary>
    private static async Task<(string Challenge, string Nonce)> Solve(HttpClient client)
    {
        using var answer = JsonDocument.Parse(await client.GetStringAsync("/v1/trials/challenge"));
        var challenge = answer.RootElement.GetProperty("challenge").GetString()!;
        var bits = answer.RootElement.GetProperty("bits").GetInt32();
        for (var nonce = 0L; ; nonce++)
        {
            var text = nonce.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TrialChallenge.Proves(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge}.{text}")), bits))
            {
                return (challenge, text);
            }
        }
    }

    private static async Task<(HttpStatusCode Status, string Answer)> Ask(HttpClient client, string email, string repositories, (string Challenge, string Nonce)? proof = null)
    {
        var (challenge, nonce) = proof ?? await Solve(client);
        using var response = await client.PostAsJsonAsync("/v1/trials", new { email, repositories, challenge, nonce });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_trial_request_emails_a_trial_license_for_the_named_repositories()
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer);
        using var client = service.CreateClient();

        var (status, answer) = await Ask(client, "Dev@Acme.com", "github.com/Acme/*, github.com/acme/tools");

        Assert.Equal((HttpStatusCode.Accepted, """{"status":"accepted"}"""), (status, answer));
        var (mail, _) = Assert.Single(mailer.Sent);
        Assert.Equal("Dev@Acme.com", mail.To);
        var line = mail.Text.Split('\n').Select(static l => l.Trim()).Single(static l => l.StartsWith(License.Prefix, StringComparison.Ordinal));
        var claims = License.Verify(line, Trusted, out _)!;
        Assert.Equal((LicenseKind.Trial, 50, "Trial for github.com/acme"), (claims.Kind, claims.Seats, claims.Licensee));
        Assert.Equal(["github.com/acme/*", "github.com/acme/tools"], claims.Scopes);
        Assert.InRange(claims.ValidUntil - DateTimeOffset.UtcNow, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
        Assert.DoesNotContain("acme.com", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subscribe at testdetta.com/pricing", mail.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Polar", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_trial_per_owner_and_per_address_and_every_request_gets_the_same_answer()
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer);
        using var client = service.CreateClient();
        await Ask(client, "dev@acme.com", "github.com/acme/*");

        var sameOwner = await Ask(client, "other@acme.com", "github.com/ACME/app");
        var sameAddress = await Ask(client, "DEV@acme.com", "gitlab.com/elsewhere/*");
        var fresh = await Ask(client, "dev@beta.io", "github.com/beta/*");

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted], [sameOwner.Status, sameAddress.Status, fresh.Status]);
        Assert.Equal([sameOwner.Answer, sameAddress.Answer], [fresh.Answer, fresh.Answer]);
        Assert.Equal(["dev@acme.com", "dev@beta.io"], mailer.Sent.Select(static s => s.Mail.To));
    }

    [Theory]
    [InlineData("not an address", "github.com/acme/*", "email")]
    [InlineData("a@b.co, c@d.co", "github.com/acme/*", "email")]
    [InlineData("dev@acme.com", "github.com/*", "repositories")]
    [InlineData("dev@acme.com", "*/acme/*", "repositories")]
    [InlineData("dev@acme.com", "acme", "repositories")]
    [InlineData("dev@acme.com", "github.com/acme/<script>", "repositories")]
    [InlineData("dev@acme.com", "", "repositories")]
    public async Task Malformed_requests_name_the_field_to_fix_and_issue_nothing(string email, string repositories, string field)
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer);
        using var client = service.CreateClient();

        var (status, answer) = await Ask(client, email, repositories);

        Assert.Equal((HttpStatusCode.BadRequest, $$"""{"status":"invalid","field":"{{field}}"}"""), (status, answer));
        Assert.Empty(mailer.Sent);
    }

    [Fact]
    public async Task A_challenge_works_once_and_only_with_its_proof()
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer);
        using var client = service.CreateClient();
        var proof = await Solve(client);

        var wrong = await Ask(client, "dev@acme.com", "github.com/acme/*", (proof.Challenge, "x1"));
        var forged = await Ask(client, "dev@acme.com", "github.com/acme/*", (proof.Challenge[..^2] + "AA", proof.Nonce));
        var first = await Ask(client, "dev@acme.com", "github.com/acme/*", proof);
        var replay = await Ask(client, "dev@beta.io", "github.com/beta/*", proof);

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.Accepted, HttpStatusCode.BadRequest],
            [wrong.Status, forged.Status, first.Status, replay.Status]);
        Assert.Single(mailer.Sent);
    }

    [Fact]
    public void A_challenge_expires_and_its_proof_is_checked_bit_by_bit()
    {
        var challenge = new TrialChallenge(RandomNumberGenerator.GetBytes(32), 0);
        var issued = DateTimeOffset.UtcNow;
        var token = challenge.Issue(issued);

        Assert.True(challenge.Check(token, "0", issued.AddMinutes(14), out _));
        Assert.False(challenge.Check(token, "0", issued.AddMinutes(16), out _));
        Assert.False(new TrialChallenge(RandomNumberGenerator.GetBytes(32), 0).Check(token, "0", issued, out _));
        Assert.True(TrialChallenge.Proves([0x00, 0x0F, 0xFF], 12));
        Assert.False(TrialChallenge.Proves([0x00, 0x1F, 0xFF], 12));
    }

    [Fact]
    public async Task The_daily_ceiling_pauses_trials_and_says_so()
    {
        var mailer = new FakeMailer();
        using var service = Service(mailer, perDay: 1);
        using var client = service.CreateClient();
        await Ask(client, "dev@acme.com", "github.com/acme/*");

        var (status, answer) = await Ask(client, "dev@beta.io", "github.com/beta/*");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"status":"paused"}"""), (status, answer));
        Assert.Single(mailer.Sent);
    }

    [Fact]
    public async Task A_license_e_mail_that_does_not_go_out_leaves_the_trial_to_a_retry()
    {
        var mailer = new FakeMailer();
        mailer.Next.Enqueue(static () => throw new HttpRequestException("down"));
        using var service = Service(mailer);
        using var client = service.CreateClient();

        var failed = await Ask(client, "dev@acme.com", "github.com/acme/*");
        var retried = await Ask(client, "dev@acme.com", "github.com/acme/*");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"status":"retry"}"""), failed);
        Assert.Equal(HttpStatusCode.Accepted, retried.Status);
        Assert.Single(mailer.Sent);
        await using var source = NpgsqlDataSource.Create(_connection);
        await using var revoked = source.CreateCommand("select count(*) from licenses where kind = 'trial' and revoked");
        Assert.Equal(1L, await revoked.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Trials_are_off_unless_enabled_and_only_the_site_gets_cors()
    {
        using (var off = Service(new FakeMailer(), enabled: false))
        using (var offClient = off.CreateClient())
        {
            Assert.Equal(HttpStatusCode.NotFound, (await offClient.GetAsync("/v1/trials/challenge")).StatusCode);
        }

        using var service = Service(new FakeMailer());
        using var client = service.CreateClient();
        async Task<bool> Allowed(string origin)
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/trials");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
            using var response = await client.SendAsync(preflight);
            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed) && allowed.Single() == origin;
        }

        Assert.True(await Allowed(Origin));
        Assert.False(await Allowed("https://evil.example"));
    }

    [Fact]
    public async Task The_daily_job_reminds_once_skips_owners_that_bought_and_forgets_old_addresses()
    {
        using (var service = Service(new FakeMailer()))
        {
            service.CreateClient().Dispose(); // creates the schema
        }

        await using var source = NpgsqlDataSource.Create(_connection);
        var trials = new TrialStore(source);
        var now = DateTimeOffset.UtcNow;
        await trials.ReserveAsync(new TrialRequest("T-soon", [1], "soon@acme.com", ["github.com/acme"], now.AddDays(2)), 50, CancellationToken.None);
        await trials.ReserveAsync(new TrialRequest("T-later", [2], "later@beta.io", ["github.com/beta"], now.AddDays(20)), 50, CancellationToken.None);
        await trials.ReserveAsync(new TrialRequest("T-bought", [3], "bought@gamma.dev", ["github.com/gamma"], now.AddDays(1)), 50, CancellationToken.None);
        await trials.ReserveAsync(new TrialRequest("T-ended", [4], "ended@delta.dev", ["github.com/delta"], now.AddDays(-8)), 50, CancellationToken.None);
        await new LicenseStore(source).SaveAsync(
            new LicenseRecord("P-1", "Gamma", "Team", LicenseKind.Subscription, 3, ["github.com/gamma/*"], now, now.AddDays(30), false, "polar"),
            "subscription.created", "test", now, CancellationToken.None);
        var mailer = new FakeMailer();
        var job = new TrialDaily(trials, mailer, NullLogger<TrialDaily>.Instance);

        Assert.Equal(0, await job.RunAsync(CancellationToken.None));
        Assert.Equal(0, await job.RunAsync(CancellationToken.None));

        var (mail, key) = Assert.Single(mailer.Sent);
        Assert.Equal(("soon@acme.com", "T-soon/reminder"), (mail.To, key));
        Assert.Contains("github.com/acme", mail.Text, StringComparison.Ordinal);
        Assert.Contains(TrialReminderMail.PricingUrl, mail.Html!, StringComparison.Ordinal);
        await using var kept = source.CreateCommand("select id from trials where email is not null order by id");
        var withAddress = new List<string>();
        await using (var reader = await kept.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                withAddress.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["T-bought", "T-later", "T-soon"], withAddress);
    }

    [Theory]
    [InlineData("github.com/acme/*", "github.com/acme")]
    [InlineData("GitHub.com/Acme/App\ngit.acme.corp/team/*", "github.com/acme git.acme.corp/team")]
    [InlineData("github.com/acme/*, github.com/acme/tools", "github.com/acme")]
    [InlineData("github.com", null)]
    [InlineData("github/acme", null)]
    [InlineData("github.com//acme", null)]
    [InlineData("*.com/acme/*", null)]
    [InlineData("github.com/ac*/x", null)]
    public void Trial_scopes_name_a_host_and_an_owner_without_wildcards(string text, string? owners) =>
        Assert.Equal(owners, TrialScopes.Parse(text) is { } scopes ? string.Join(' ', scopes.Owners) : null);

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
}
