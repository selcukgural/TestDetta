namespace Sift.LicenseService;

/// <summary>
/// The service's settings, from the <c>Licenses</c> configuration section (appsettings, environment
/// variables such as <c>Licenses__ConnectionString</c>, or the command line).
/// </summary>
/// <param name="ConnectionString">Postgres connection string.</param>
/// <param name="KeyId">Id of the signing key, carried by every license it signs.</param>
/// <param name="KeyFile">PEM file with the ECDSA P-256 private key.</param>
/// <param name="PublicBaseUrl">The service's public base URL; licenses carry their refresh link under it.</param>
public sealed record ServiceSettings(string ConnectionString, string KeyId, string KeyFile, Uri PublicBaseUrl)
{
    /// <summary>Public keys of retired signing keys (key id to base64 SubjectPublicKeyInfo), still accepted when a license asks for its refresh.</summary>
    public IReadOnlyDictionary<string, string> RetiredKeys { get; init; } = new Dictionary<string, string>();

    /// <summary>The Polar webhook endpoint's secret (<c>whsec_...</c>); null turns the endpoint off.</summary>
    public string? PolarWebhookSecret { get; init; }

    /// <summary>
    /// Proxies whose <c>X-Forwarded-For</c> is believed, as IP addresses or CIDR networks; <c>*</c>
    /// believes one hop from anywhere, for a host whose only way in is its own proxy. Empty (the
    /// default) believes no header: otherwise anyone could pick their address and dodge the per-address limit.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    /// <summary>Refresh requests one client address may make per minute.</summary>
    public int PerAddressPerMinute { get; init; } = 60;

    /// <summary>Refresh requests one license may make per hour (every CI runner refreshes at most daily).</summary>
    public int PerLicensePerHour { get; init; } = 600;

    /// <summary>Reads and checks the settings.</summary>
    /// <param name="configuration">Configuration root.</param>
    /// <exception cref="InvalidOperationException">A required setting is missing.</exception>
    public static ServiceSettings From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Licenses");
        string Required(string name) => section[name] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Licenses:{name} is not set.");

        var baseUrl = Required("PublicBaseUrl");
        return new ServiceSettings(
            Required("ConnectionString"),
            Required("KeyId"),
            Required("KeyFile"),
            new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"))
        {
            RetiredKeys = section.GetSection("RetiredKeys").GetChildren().ToDictionary(static c => c.Key, static c => c.Value ?? ""),
            PerAddressPerMinute = section.GetValue("PerAddressPerMinute", 60),
            PerLicensePerHour = section.GetValue("PerLicensePerHour", 600),
            PolarWebhookSecret = section["PolarWebhookSecret"] is { Length: > 0 } secret ? secret : null,
            TrustedProxies = [.. (section["TrustedProxies"] ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)],
        };
    }
}
