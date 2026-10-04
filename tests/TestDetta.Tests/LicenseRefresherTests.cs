using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core.Licensing;

namespace TestDetta.Tests;

/// <summary>The daily license refresh: an update channel that never blocks or breaks a run.</summary>
public sealed class LicenseRefresherTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _home = Directory.CreateTempSubdirectory("testdetta-refresh-").FullName;
    private readonly Clock _clock = new();
    private readonly Dictionary<string, string> _variables = [];

    private Dictionary<string, byte[]> Trusted => new() { ["k1"] = _key.ExportSubjectPublicKeyInfo() };

    private string Sign(int seats, DateTimeOffset issued, string id = "L-1", string refresh = "https://license.example/v1/licenses/L-1") =>
        License.Sign(new LicenseClaims(id, "k1", "team", LicenseKind.Subscription, "Acme", seats, ["github.com/acme/*"],
            Start.AddDays(-10), Start.AddDays(30), new Uri(refresh)) { Issued = issued }, _key);

    private string? Stored => File.Exists(LicenseInputs.InstalledPath(_home)) ? File.ReadAllText(LicenseInputs.InstalledPath(_home)) : null;

    private Task Refresh(Handler handler, string license) =>
        new LicenseRefresher(handler, name => _variables.GetValueOrDefault(name), _home, _clock, NullLogger.Instance).RefreshAsync([license], Trusted);

    [Fact]
    public async Task A_newer_version_is_fetched_once_a_day_with_the_license_in_a_header_only()
    {
        var current = Sign(5, Start.AddDays(-5));
        var newer = Sign(7, Start.AddDays(-1));
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(newer) });

        await Refresh(handler, current);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://license.example/v1/licenses/L-1", request.RequestUri!.ToString());
        Assert.Equal(("TestDetta", current), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal(newer, Stored);

        _clock.Now = Start.AddHours(23);
        await Refresh(handler, current);
        Assert.Single(handler.Requests);

        _clock.Now = Start.AddDays(1);
        await Refresh(handler, current);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Off_or_not_https_never_calls()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not be called"));

        _variables["TESTDETTA_LICENSE_REFRESH"] = "off";
        await Refresh(handler, Sign(5, Start));
        _variables.Clear();
        await Refresh(handler, Sign(5, Start, refresh: "http://license.example/v1/licenses/L-1"));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("older")]
    [InlineData("garbage")]
    [InlineData("404")]
    public async Task Anything_but_a_newer_version_of_the_same_genuine_license_is_ignored(string answer)
    {
        var current = Sign(5, Start);
        var body = answer switch
        {
            "other" => Sign(50, Start.AddDays(1), id: "L-2"),
            "older" => Sign(3, Start.AddDays(-1)),
            _ => "garbage",
        };
        var handler = new Handler(_ => new HttpResponseMessage(answer == "404" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body) });

        await Refresh(handler, current);

        Assert.Single(handler.Requests);
        Assert.Null(Stored);
    }

    [Fact]
    public async Task An_unreachable_service_costs_at_most_the_timeout_and_changes_nothing()
    {
        var handler = new Handler(async (_, cancel) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancel);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var watch = Stopwatch.StartNew();

        await Refresh(handler, Sign(5, Start));

        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 6);
        Assert.Null(Stored);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_home, recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }
}
