namespace TestDetta.LicenseService;

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
    /// The Polar product ids whose subscriptions make licenses; empty takes every product of the organization.
    /// Set it before the organization sells anything else (a free tier, a donation), whose subscriptions would
    /// otherwise become licenses.
    /// </summary>
    public IReadOnlyList<string> PolarProducts { get; init; } = [];

    /// <summary>Resend API key with sending access to the sender's domain; null turns license e-mails off.</summary>
    public string? ResendApiKey { get; init; }

    /// <summary>Sender of license e-mails, such as <c>TestDetta &lt;licenses@example.com&gt;</c>; required with <see cref="ResendApiKey"/>.</summary>
    public string MailFrom { get; init; } = "";

    /// <summary>Where replies to license e-mails go, such as the support address; optional.</summary>
    public string? MailReplyTo { get; init; }

    /// <summary>
    /// Proxies whose <c>X-Forwarded-For</c> is believed, as IP addresses or CIDR networks; <c>*</c>
    /// believes one hop from anywhere, for a host whose only way in is its own proxy. Empty (the
    /// default) believes no header: otherwise anyone could pick their address and dodge the per-address limit.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    /// <summary>
    /// How many trusted proxies in a row append to <c>X-Forwarded-For</c>: 1 behind one load balancer or
    /// Cloud Run alone, 2 behind Firebase Hosting in front of Cloud Run, where Hosting appends the client
    /// and Cloud Run's front end appends Hosting. Each hop is taken only from a trusted proxy.
    /// </summary>
    public int ForwardedHops { get; init; } = 1;

    /// <summary>Whether the site may hand out trial licenses (<c>/v1/trials</c>); off until launch, and off without e-mail.</summary>
    public bool TrialsEnabled { get; init; }

    /// <summary>Sites whose pages may call the trial endpoints (CORS), such as <c>https://testdetta.com</c>.</summary>
    public IReadOnlyList<string> TrialOrigins { get; init; } = [];

    /// <summary>Most trials handed out in any 24 hours, a ceiling on what e-mail and abuse can cost.</summary>
    public int TrialsPerDay { get; init; } = 50;

    /// <summary>Active committers a trial covers; generous, since a trial is for finding out whether it fits.</summary>
    public int TrialSeats { get; init; } = 50;

    /// <summary>Length of a trial, in days.</summary>
    public int TrialDays { get; init; } = 30;

    /// <summary>Leading zero bits the trial form's proof of work needs (<see cref="TrialChallenge"/>).</summary>
    public int TrialProofBits { get; init; } = 17;

    /// <summary>
    /// Shortest time a well-formed trial request takes to answer, so a visitor cannot tell from the timing
    /// whether a license went out (the e-mail takes about a second) or the address or owner had one already.
    /// </summary>
    public TimeSpan TrialMinimumResponse { get; init; } = TimeSpan.FromSeconds(2);

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
        var resendApiKey = section["ResendApiKey"] is { Length: > 0 } apiKey ? apiKey : null;
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
            ResendApiKey = resendApiKey,
            MailFrom = resendApiKey is null ? section["MailFrom"] ?? "" : Required("MailFrom"),
            MailReplyTo = section["MailReplyTo"] is { Length: > 0 } replyTo ? replyTo : null,
            TrustedProxies = [.. (section["TrustedProxies"] ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)],
            ForwardedHops = Math.Max(1, section.GetValue("ForwardedHops", 1)),
            TrialsEnabled = section.GetValue("TrialsEnabled", false) && resendApiKey is not null,
            TrialOrigins = [.. (section["TrialOrigins"] ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)],
            PolarProducts = [.. (section["PolarProducts"] ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)],
            TrialsPerDay = section.GetValue("TrialsPerDay", 50),
            TrialSeats = section.GetValue("TrialSeats", 50),
            TrialDays = section.GetValue("TrialDays", 30),
            TrialProofBits = Math.Clamp(section.GetValue("TrialProofBits", 17), 0, 32),
            TrialMinimumResponse = TimeSpan.FromMilliseconds(section.GetValue("TrialMinimumResponseMs", 2000)),
        };
    }
}
