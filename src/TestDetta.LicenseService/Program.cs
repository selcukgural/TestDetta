using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using TestDetta.LicenseService;

// The license service (docs/design/licensing.md): the only holder of the signing key. Serves license
// refreshes over HTTP; `admin ...` manages licenses from a shell on the host instead (no admin web UI).
if (args is ["admin", .. var adminArgs])
{
    var configuration = SecretFiles.Add(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables(), Environment.GetEnvironmentVariable(SecretFiles.Variable))
        .Build();
    return await AdminCommand.RunAsync(adminArgs, ServiceSettings.From(configuration), Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);
SecretFiles.Add(builder.Configuration, Environment.GetEnvironmentVariable(SecretFiles.Variable));
var settings = ServiceSettings.From(builder.Configuration);

// Requests carry no body today; a small limit keeps it that way (webhooks set their own when added).
builder.WebHost.ConfigureKestrel(static kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 16 * 1024;
    kestrel.AddServerHeader = false;
});

// As the site sends it (site/firebase.json), so the API is never the weaker link.
builder.Services.AddHsts(static hsts =>
{
    hsts.MaxAge = TimeSpan.FromDays(365);
    hsts.IncludeSubDomains = true;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
builder.Services.AddSingleton<LicenseStore>();
builder.Services.AddSingleton(_ => new SigningKey(settings.KeyId, File.ReadAllText(settings.KeyFile)));
builder.Services.AddSingleton(static services => new LicenseIssuer(services.GetRequiredService<SigningKey>(), services.GetRequiredService<ServiceSettings>().PublicBaseUrl));
if (settings.ResendApiKey is not null)
{
    builder.Services.AddHttpClient<ILicenseMailer, ResendMailer>(static http =>
    {
        http.BaseAddress = ResendMailer.ApiBase;
        http.Timeout = TimeSpan.FromSeconds(15);
    });
}

// Transient, as the typed client it takes: a singleton would pin one HTTP handler for good.
builder.Services.AddTransient(static services => new LicenseDelivery(
    services.GetRequiredService<LicenseStore>(),
    services.GetRequiredService<LicenseIssuer>(),
    services.GetService<ILicenseMailer>(),
    services.GetRequiredService<ILogger<LicenseDelivery>>()));
builder.Services.AddSingleton(static services => TrustedKeys.From(services.GetRequiredService<SigningKey>(), services.GetRequiredService<ServiceSettings>()));
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Per client address and, for refreshes, per license: one CI fleet cannot exhaust another's budget,
    // and one leaked license cannot hammer the service from many addresses.
    limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
            ClientPartition.Of(context.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PerAddressPerMinute, Window = TimeSpan.FromMinutes(1) })),
        PartitionedRateLimiter.Create<HttpContext, string>(context => context.Request.RouteValues["id"] is string id
            ? RateLimitPartition.GetFixedWindowLimiter("license:" + id, _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PerLicensePerHour, Window = TimeSpan.FromHours(1) })
            : RateLimitPartition.GetNoLimiter("other")));
});

var app = builder.Build();
await app.Services.GetRequiredService<LicenseStore>().EnsureSchemaAsync(CancellationToken.None);
if (settings.TrustedProxies.Count > 0)
{
    // First: the rate limiter partitions by the client's address, and HSTS is only sent on requests that
    // count as HTTPS, which behind Cloud Run's front end only X-Forwarded-Proto tells.
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = settings.ForwardedHops };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var proxy in settings.TrustedProxies.Where(static p => p != "*"))
    {
        if (System.Net.IPNetwork.TryParse(proxy, out var network))
        {
            forwarded.KnownIPNetworks.Add(network);
        }
        else
        {
            forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        }
    }

    app.UseForwardedHeaders(forwarded);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRouting();
app.UseRateLimiter();
// Not /healthz: Cloud Run's front end answers some paths ending in "z" itself, so that never reaches the service.
app.MapGet("/v1/health", static () => Results.Text("ok"));
app.MapGet("/v1/licenses/{id}", RefreshEndpoint.HandleAsync);
app.MapPost("/webhooks/polar", PolarWebhook.HandleAsync);
await app.RunAsync();
return 0;

/// <summary>The service's entry point; public for the HTTP tests' application factory.</summary>
public partial class Program;
