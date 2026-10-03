using System.Threading.RateLimiting;
using Npgsql;
using Sift.LicenseService;

// The license service (docs/design/licensing.md): the only holder of the signing key. Serves license
// refreshes over HTTP; `admin ...` manages licenses from a shell on the host instead (no admin web UI).
if (args is ["admin", .. var adminArgs])
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
    return await AdminCommand.RunAsync(adminArgs, ServiceSettings.From(configuration), Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);
var settings = ServiceSettings.From(builder.Configuration);

// Requests carry no body today; a small limit keeps it that way (webhooks set their own when added).
builder.WebHost.ConfigureKestrel(static kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 16 * 1024;
    kestrel.AddServerHeader = false;
});

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
builder.Services.AddSingleton<LicenseStore>();
builder.Services.AddSingleton(_ => new SigningKey(settings.KeyId, File.ReadAllText(settings.KeyFile)));
builder.Services.AddSingleton(static services => new LicenseIssuer(services.GetRequiredService<SigningKey>(), services.GetRequiredService<ServiceSettings>().PublicBaseUrl));
builder.Services.AddSingleton(static services => TrustedKeys.From(services.GetRequiredService<SigningKey>(), services.GetRequiredService<ServiceSettings>()));
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Per client address and, for refreshes, per license: one CI fleet cannot exhaust another's budget,
    // and one leaked license cannot hammer the service from many addresses.
    limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PerAddressPerMinute, Window = TimeSpan.FromMinutes(1) })),
        PartitionedRateLimiter.Create<HttpContext, string>(context => context.Request.RouteValues["id"] is string id
            ? RateLimitPartition.GetFixedWindowLimiter("license:" + id, _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PerLicensePerHour, Window = TimeSpan.FromHours(1) })
            : RateLimitPartition.GetNoLimiter("other")));
});

var app = builder.Build();
await app.Services.GetRequiredService<LicenseStore>().EnsureSchemaAsync(CancellationToken.None);
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRouting();
app.UseRateLimiter();
app.MapGet("/healthz", static () => Results.Text("ok"));
app.MapGet("/v1/licenses/{id}", RefreshEndpoint.HandleAsync);
await app.RunAsync();
return 0;

/// <summary>The service's entry point; public for the HTTP tests' application factory.</summary>
public partial class Program;
