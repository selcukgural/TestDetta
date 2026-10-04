using System.Globalization;
using Npgsql;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// <c>admin ...</c>: manages licenses from a shell on the service's host, for direct invoices (TL,
/// e-Fatura), offline annual licenses and corrections. Every change is written to the audit table with
/// its <c>--note</c>. There is deliberately no admin web UI to attack.
/// </summary>
public static class AdminCommand
{
    /// <summary>Usage of the admin command.</summary>
    public const string Usage = """
        admin issue --id <id> --licensee <name> --seats <n> --scope <pattern>... --kind subscription|annual|trial
                    --from <yyyy-mm-dd> --until <yyyy-mm-dd> [--plan <name>] --note <why>
        admin update --id <id> [--seats <n>] [--until <yyyy-mm-dd>] [--scope <pattern>...] --note <why>
        admin revoke --id <id> --note <why>
        admin show --id <id>        the record, its audit trail and the current license string
        admin list
        admin public-key            the key id and public key to embed in TestDetta (LicenseKeys.Trusted)
        """;

    /// <summary>Runs one admin command; returns the process exit code.</summary>
    /// <param name="args">Arguments after <c>admin</c>.</param>
    /// <param name="settings">Service settings.</param>
    /// <param name="stdout">Receives results.</param>
    /// <param name="stderr">Receives usage and errors.</param>
    public static async Task<int> RunAsync(string[] args, ServiceSettings settings, TextWriter stdout, TextWriter stderr)
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
                "revoke" => await RevokeAsync(options, store, stdout, stderr),
                "show" => await ShowAsync(options, store, issuer, stdout, stderr),
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

        var updated = record with
        {
            Seats = options.ContainsKey("seats") ? Seats(One(options, "seats")) : record.Seats,
            PeriodEnd = options.ContainsKey("until") ? Date(One(options, "until")) : record.PeriodEnd,
            Scopes = options.ContainsKey("scope") ? Scopes(options) : record.Scopes,
        };
        await store.SaveAsync(updated, "update", $"{One(options, "note")} (seats {record.Seats} -> {updated.Seats}, until {record.PeriodEnd:yyyy-MM-dd} -> {updated.PeriodEnd:yyyy-MM-dd})", CancellationToken.None);
        await stdout.WriteLineAsync(issuer.Issue(updated));
        return 0;
    }

    /// <summary>Stops a license from being renewed or refreshed.</summary>
    /// <param name="options">Parsed options.</param>
    /// <param name="store">License records.</param>
    /// <param name="stdout">Receives results.</param>
    /// <param name="stderr">Receives errors.</param>
    private static async Task<int> RevokeAsync(Dictionary<string, List<string>> options, LicenseStore store, TextWriter stdout, TextWriter stderr)
    {
        if (await store.GetAsync(One(options, "id"), CancellationToken.None) is not { } record)
        {
            await stderr.WriteLineAsync("no such license");
            return 1;
        }

        await store.SaveAsync(record with { Revoked = true }, "revoke", One(options, "note"), CancellationToken.None);
        await stdout.WriteLineAsync($"{record.Id} revoked; it runs out at its end date (offline licenses cannot be recalled).");
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
        await stdout.WriteLineAsync($"scopes: {string.Join(", ", record.Scopes)}");
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

    /// <summary>The scopes, normalised to lower case; at least one.</summary>
    /// <param name="options">Parsed options.</param>
    private static string[] Scopes(Dictionary<string, List<string>> options) =>
        options.TryGetValue("scope", out var scopes) && scopes.Count > 0
            ? [.. scopes.Select(static s => s.Trim().ToLowerInvariant())]
            : throw new KeyNotFoundException("--scope is required");
}
