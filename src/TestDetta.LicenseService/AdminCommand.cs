using System.Globalization;
using Npgsql;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// <c>admin ...</c>: manages licenses for direct invoices (TL, e-Fatura), offline annual licenses and
/// corrections. In production it runs as the Cloud Run job <c>license-service-admin</c>
/// (<c>deploy/license-service/admin.sh</c>), whose output goes to the project's own logs. Every change is
/// written to the audit table with its <c>--note</c>. There is deliberately no admin web UI to attack.
/// </summary>
public static class AdminCommand
{
    /// <summary>Usage of the admin command.</summary>
    public const string Usage = """
        admin issue --id <id> --licensee <name> --seats <n> --scope <pattern>... --kind subscription|annual|trial
                    --from <yyyy-mm-dd> --until <yyyy-mm-dd> [--plan <name>] --note <why>
        admin update --id <id> [--seats <n>] [--until <yyyy-mm-dd>] [--scope <pattern>...] --note <why>
                    (a Polar license: --scope only; its seats and period come from Polar)
        admin revoke --id <id> --note <why>     for good: Polar's later events do not bring it back
        admin restore --id <id> --note <why>    lifts a revocation made by admin revoke or a refund
        admin send --id <id> --to <address> --note <why>   e-mails the current license (the address is not kept)
        admin show --id <id>        the record, its audit trail and the current license string
        admin attention [--days <n>]   what needs an admin in the last n days (default 14), and paid licenses never e-mailed
        admin trial-release --owner <host/owner> --note <why>   lets an owner have a trial again
        admin list
        admin public-key            the key id and public key to embed in TestDetta (LicenseKeys.Trusted)
        """;

    /// <summary>Runs one admin command; returns the process exit code.</summary>
    /// <param name="args">Arguments after <c>admin</c>.</param>
    /// <param name="settings">Service settings.</param>
    /// <param name="stdout">Receives results.</param>
    /// <param name="stderr">Receives usage and errors.</param>
    /// <param name="mailer">Sends <c>admin send</c>'s e-mail; null makes one from the settings' Resend key.</param>
    public static async Task<int> RunAsync(string[] args, ServiceSettings settings, TextWriter stdout, TextWriter stderr, ILicenseMailer? mailer = null)
    {
        if (args.Length == 0)
        {
            await stderr.WriteLineAsync($"missing command\n\n{Usage}");
            return 2;
        }

        if (!TryParse(args[1..], out var options, out var error))
        {
            await stderr.WriteLineAsync($"{error}\n\n{Usage}");
            return 2;
        }

        using var key = new SigningKey(settings.KeyId, await File.ReadAllTextAsync(settings.KeyFile));
        var issuer = new LicenseIssuer(key, settings.PublicBaseUrl);
        if (args[0] == "public-key")
        {
            await stdout.WriteLineAsync($"{key.KeyId} {Convert.ToBase64String(key.PublicKey)}");
            return 0;
        }

        await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var store = new LicenseStore(dataSource);
        await store.EnsureSchemaAsync(CancellationToken.None);
        try
        {
            return args[0] switch
            {
                "issue" => await IssueAsync(options, store, issuer, stdout),
                "update" => await UpdateAsync(options, store, issuer, stdout, stderr),
                "revoke" => await RevokeAsync(options, store, revoked: true, stdout, stderr),
                "restore" => await RevokeAsync(options, store, revoked: false, stdout, stderr),
                "send" => await SendAsync(options, new AdminContext(store, issuer, settings, mailer), stdout, stderr),
                "show" => await ShowAsync(options, store, issuer, stdout, stderr),
                "attention" => await AttentionAsync(options, store, stdout),
                "trial-release" => await TrialReleaseAsync(options, new TrialStore(dataSource), store, stdout),
                "list" => await ListAsync(store, stdout),
                _ => await UnknownAsync(args[0], stderr),
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or ArgumentException)
        {
            await stderr.WriteLineAsync($"{ex.Message}\n\n{Usage}");
            return 2;
        }
    }

    /// <summary>Creates a license and prints its string.</summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="store">License records.</param>
    /// <param name="issuer">Signs license strings.</param>
    /// <param name="stdout">Receives results.</param>
    private static async Task<int> IssueAsync(Dictionary<string, List<string>> options, LicenseStore store, LicenseIssuer issuer, TextWriter stdout)
    {
        // Issuing over an existing id would silently replace a paid (Polar) record or another invoice's license.
        if (await store.GetAsync(One(options, "id"), CancellationToken.None) is not null)
        {
            throw new ArgumentException($"license {One(options, "id")} exists; change it with admin update");
        }

        var record = new LicenseRecord(
            One(options, "id"),
            One(options, "licensee"),
            options.TryGetValue("plan", out var plan) ? plan[0] : "team",
            Enum.Parse<LicenseKind>(One(options, "kind"), ignoreCase: true),
            Seats(One(options, "seats")),
            Scopes(options),
            Date(One(options, "from")),
            Date(One(options, "until")),
            Revoked: false,
            Source: "manual");
        await store.SaveAsync(record, "issue", One(options, "note"), CancellationToken.None);
        await stdout.WriteLineAsync(issuer.Issue(record));
        return 0;
    }

    /// <summary>Changes seats, end or scopes of a license and prints its new string.</summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="store">License records.</param>
    /// <param name="issuer">Signs license strings.</param>
    /// <param name="stdout">Receives results.</param>
    /// <param name="stderr">Receives errors.</param>
    private static async Task<int> UpdateAsync(Dictionary<string, List<string>> options, LicenseStore store, LicenseIssuer issuer, TextWriter stdout, TextWriter stderr)
    {
        if (await store.GetAsync(One(options, "id"), CancellationToken.None) is not { } record)
        {
            await stderr.WriteLineAsync("no such license");
            return 1;
        }

        var note = One(options, "note");
        if (record.Source != "manual")
        {
            // A provider's record is rebuilt from the provider on its next event: seats and the period are
            // changed there (a seat change in Polar), and only scopes can be set here, for good.
            if (options.ContainsKey("seats") || options.ContainsKey("until") || !options.ContainsKey("scope"))
            {
                await stderr.WriteLineAsync($"{record.Id} comes from {record.Source}: change its seats and period there; here only --scope can be set");
                return 2;
            }

            var scopes = Scopes(options);
            await store.OverrideScopesAsync(record.Id, scopes, $"{note} (scopes {string.Join(", ", record.Scopes)} -> {string.Join(", ", scopes)}; kept over {record.Source}'s)", CancellationToken.None);
            await stdout.WriteLineAsync(issuer.Issue((await store.GetAsync(record.Id, CancellationToken.None))!));
            return 0;
        }

        var updated = record with
        {
            Seats = options.ContainsKey("seats") ? Seats(One(options, "seats")) : record.Seats,
            PeriodEnd = options.ContainsKey("until") ? Date(One(options, "until")) : record.PeriodEnd,
            Scopes = options.ContainsKey("scope") ? Scopes(options) : record.Scopes,
        };
        await store.SaveAsync(updated, "update", $"{note} (seats {record.Seats} -> {updated.Seats}, until {record.PeriodEnd:yyyy-MM-dd} -> {updated.PeriodEnd:yyyy-MM-dd}, scopes {string.Join(", ", updated.Scopes)})", CancellationToken.None);
        await stdout.WriteLineAsync(issuer.Issue(updated));
        return 0;
    }

    /// <summary>
    /// Stops a license from being renewed or refreshed, for good (<paramref name="revoked"/> true), or lifts that
    /// (false). A payment provider's later events keep a revocation made here.
    /// </summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="store">License records.</param>
    /// <param name="revoked">True to revoke, false to restore.</param>
    /// <param name="stdout">Receives results.</param>
    /// <param name="stderr">Receives errors.</param>
    private static async Task<int> RevokeAsync(Dictionary<string, List<string>> options, LicenseStore store, bool revoked, TextWriter stdout, TextWriter stderr)
    {
        var id = One(options, "id");
        if (!await store.SetRevokedAsync(id, revoked, revoked ? "revoke" : "restore", One(options, "note"), CancellationToken.None))
        {
            await stderr.WriteLineAsync("no such license");
            return 1;
        }

        await stdout.WriteLineAsync(revoked
            ? $"{id} revoked; the copy in hand runs out at its end date (offline licenses cannot be recalled)."
            : $"{id} restored; its next refresh hands out a current license again.");
        return 0;
    }

    /// <summary>
    /// E-mails the current license to <c>--to</c>, as the purchase did: for a buyer whose e-mail did not arrive
    /// or who lost it, and for licenses issued by hand. The address is used for this send only; the audit
    /// names the license, never the address.
    /// </summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="context">Records, signing and the mailer.</param>
    /// <param name="stdout">Receives results.</param>
    /// <param name="stderr">Receives errors.</param>
    private static async Task<int> SendAsync(Dictionary<string, List<string>> options, AdminContext context, TextWriter stdout, TextWriter stderr)
    {
        var (store, issuer, settings, given) = context;
        var (id, to, note) = (One(options, "id"), One(options, "to"), One(options, "note"));
        if (!LicenseDelivery.IsDeliverable(to))
        {
            await stderr.WriteLineAsync("--to must be one plain e-mail address");
            return 2;
        }

        if (await store.GetAsync(id, CancellationToken.None) is not { } record)
        {
            await stderr.WriteLineAsync("no such license");
            return 1;
        }

        if (record.Revoked || record.Scopes.Count == 0)
        {
            await stderr.WriteLineAsync($"{id} is revoked or has no scopes; fix that first (admin restore, admin update --scope)");
            return 1;
        }

        using var http = new HttpClient { BaseAddress = ResendMailer.ApiBase, Timeout = TimeSpan.FromSeconds(15) };
        // The mailer's own log lines (Resend's status when it refuses) go to the job's output with the rest.
        using var loggers = LoggerFactory.Create(static logging => CloudLogFormatter.AddServiceConsole(logging));
        var mailer = given ?? (settings.ResendApiKey is null ? null : new ResendMailer(http, settings, loggers.CreateLogger<ResendMailer>()));
        if (mailer is null)
        {
            await stderr.WriteLineAsync("no e-mail provider is configured (Licenses:ResendApiKey)");
            return 1;
        }

        // A fresh key per send: an admin's resend is meant to go out even when the same version went before.
        var outcome = await mailer.SendAsync(LicenseMail.For(record, issuer.Issue(record), to), $"{id}/admin/{Guid.NewGuid():N}", CancellationToken.None);
        if (outcome == MailOutcome.Rejected)
        {
            await stderr.WriteLineAsync("the e-mail provider refused the e-mail; check the address");
            return 1;
        }

        await store.AuditAsync(id, "deliver", $"license e-mailed by an admin: {note}", CancellationToken.None);
        await stdout.WriteLineAsync($"{id} e-mailed.");
        return 0;
    }

    /// <summary>Prints what needs an admin: the <c>attention</c> audit entries of the last <c>--days</c> (14).</summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="store">License records.</param>
    /// <param name="stdout">Receives results.</param>
    private static async Task<int> AttentionAsync(Dictionary<string, List<string>> options, LicenseStore store, TextWriter stdout)
    {
        var days = options.ContainsKey("days") ? Seats(One(options, "days")) : 14;
        var entries = await store.AttentionSinceAsync(DateTimeOffset.UtcNow.AddDays(-days), CancellationToken.None);
        foreach (var entry in entries)
        {
            await stdout.WriteLineAsync(entry);
        }

        await stdout.WriteLineAsync(entries.Count == 0 ? $"nothing needs attention in the last {days} days" : $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} in the last {days} days");
        foreach (var id in await store.UndeliveredAsync(CancellationToken.None))
        {
            await stdout.WriteLineAsync($"{id}: paid but never e-mailed; send it with admin send --id {id} --to <the buyer's address from Polar>");
        }

        return 0;
    }

    /// <summary>
    /// Lets an owner have a trial again (<see cref="TrialStore.ReleaseOwnerAsync"/>): for an owner whose name someone
    /// else used in the trial form. The trial license already sent stays as it is.
    /// </summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="trials">Trial reservations.</param>
    /// <param name="store">License records, for the audit.</param>
    /// <param name="stdout">Receives results.</param>
    private static async Task<int> TrialReleaseAsync(Dictionary<string, List<string>> options, TrialStore trials, LicenseStore store, TextWriter stdout)
    {
        var owner = One(options, "owner").Trim().Trim('/').ToLowerInvariant();
        if (owner.Split('/') is not [{ Length: > 0 }, { Length: > 0 }])
        {
            throw new FormatException($"--owner must be a host and an owner, such as github.com/acme, not {owner}");
        }

        await trials.EnsureSchemaAsync(CancellationToken.None);
        var released = await trials.ReleaseOwnerAsync(owner, CancellationToken.None);
        await store.AuditAsync("trial-owner:" + owner, "trial-release", $"{One(options, "note")} ({released} trial(s) named it)", CancellationToken.None);
        await stdout.WriteLineAsync($"{owner} can have a trial again ({released} earlier trial(s) named it).");
        return 0;
    }

    /// <summary>Prints a record, its audit trail and its current string.</summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="store">License records.</param>
    /// <param name="issuer">Signs license strings.</param>
    /// <param name="stdout">Receives results.</param>
    /// <param name="stderr">Receives errors.</param>
    private static async Task<int> ShowAsync(Dictionary<string, List<string>> options, LicenseStore store, LicenseIssuer issuer, TextWriter stdout, TextWriter stderr)
    {
        if (await store.GetAsync(One(options, "id"), CancellationToken.None) is not { } record)
        {
            await stderr.WriteLineAsync("no such license");
            return 1;
        }

        await stdout.WriteLineAsync($"{record.Id}: {record.Licensee}, {record.Plan}, {record.Kind}, {record.Seats} seats, {record.ValidFrom:yyyy-MM-dd} to {record.PeriodEnd:yyyy-MM-dd}{(record.Revoked ? ", REVOKED" : "")}, source {record.Source}");
        await stdout.WriteLineAsync($"scopes: {string.Join(", ", record.Scopes)}{(record.ScopesSetByAdmin ? " (set by an admin; kept over the provider's)" : "")}{(record.RevokedByAdmin ? "; revoked by an admin or a refund (admin restore lifts it)" : "")}");
        foreach (var entry in await store.AuditAsync(record.Id, CancellationToken.None))
        {
            await stdout.WriteLineAsync($"  {entry}");
        }

        await stdout.WriteLineAsync(issuer.Issue(record));
        return 0;
    }

    /// <summary>Prints every license id.</summary>
    /// <param name="store">License records.</param>
    /// <param name="stdout">Receives results.</param>
    private static async Task<int> ListAsync(LicenseStore store, TextWriter stdout)
    {
        foreach (var id in await store.ListIdsAsync(CancellationToken.None))
        {
            await stdout.WriteLineAsync(id);
        }

        return 0;
    }

    /// <summary>Reports an unknown command.</summary>
    /// <param name="command">The unknown command.</param>
    /// <param name="stderr">Receives errors.</param>
    private static async Task<int> UnknownAsync(string command, TextWriter stderr)
    {
        await stderr.WriteLineAsync($"unknown admin command: {command}\n\n{Usage}");
        return 2;
    }

    /// <summary>Reads <c>--name value</c> pairs; a name may repeat (<c>--scope</c>).</summary>
    /// <param name="args">Arguments after the command.</param>
    /// <param name="options">Values by name.</param>
    /// <param name="error">Why parsing failed.</param>
    private static bool TryParse(string[] args, out Dictionary<string, List<string>> options, out string? error)
    {
        options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                error = $"expected --name value at {args[i]}";
                return false;
            }

            var name = args[i][2..];
            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options[name] = values;
            }

            values.Add(args[i + 1]);
        }

        error = null;
        return true;
    }

    /// <summary>The single value of a required option.</summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="name">Option name without dashes.</param>
    private static string One(Dictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) && values is [var value] ? value : throw new KeyNotFoundException($"--{name} is required once");

    /// <summary>A positive seat count.</summary>
    /// <param name="value">The option's value.</param>
    private static int Seats(string value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var seats) && seats > 0 ? seats : throw new FormatException($"--seats must be a positive number, not {value}");

    /// <summary>A date at midnight UTC.</summary>
    /// <param name="value">The option's value.</param>
    private static DateTimeOffset Date(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// The scopes, normalised as checkout input is (<see cref="ScopePatterns"/>, admin rules: a whole internal
    /// host is allowed); at least one, and none that cannot be read.
    /// </summary>
    /// <param name="options">Parsed options.</param>
    private static string[] Scopes(Dictionary<string, List<string>> options)
    {
        if (!options.TryGetValue("scope", out var scopes) || scopes.Count == 0)
        {
            throw new KeyNotFoundException("--scope is required");
        }

        var parsed = ScopePatterns.Parse(string.Join('\n', scopes), ScopeRules.Admin);
        return parsed.Rejected.Count > 0 || parsed.Patterns.Count == 0
            ? throw new FormatException($"not a repository pattern: {string.Join(", ", parsed.Rejected)}")
            : [.. parsed.Patterns];
    }
}

/// <summary>What <c>admin send</c> needs.</summary>
/// <param name="Store">License records.</param>
/// <param name="Issuer">Signs the license string.</param>
/// <param name="Settings">Service settings, for the Resend key and sender.</param>
/// <param name="Mailer">The mailer to use; null makes one from <paramref name="Settings"/>.</param>
internal sealed record AdminContext(LicenseStore Store, LicenseIssuer Issuer, ServiceSettings Settings, ILicenseMailer? Mailer);
