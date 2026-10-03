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

    private WebApplicationFactory<Program> Service(int perLicensePerHour = 600) => new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    {
        host.UseSetting("Licenses:ConnectionString", _connection);
        host.UseSetting("Licenses:KeyId", "k1");
        host.UseSetting("Licenses:KeyFile", _keyFile);
        host.UseSetting("Licenses:PublicBaseUrl", "https://license.example/");
        host.UseSetting("Licenses:PerLicensePerHour", perLicensePerHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
    });

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
