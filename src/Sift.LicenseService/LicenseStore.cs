using Npgsql;
using NpgsqlTypes;
using Sift.Core.Licensing;

namespace Sift.LicenseService;

/// <summary>
/// The license records and their audit trail in Postgres, with plain parameterised SQL. The schema is
/// created on start (<see cref="EnsureSchemaAsync"/>); later versions add numbered steps.
/// </summary>
/// <param name="dataSource">Connections to the service's database.</param>
public sealed class LicenseStore(NpgsqlDataSource dataSource)
{
    /// <summary>Creates the tables when missing.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            create table if not exists licenses (
                id text primary key,
                licensee text not null,
                plan text not null,
                kind text not null,
                seats integer not null check (seats > 0),
                scopes text[] not null,
                valid_from timestamptz not null,
                period_end timestamptz not null,
                revoked boolean not null default false,
                source text not null,
                updated_at timestamptz not null default now()
            );
            create table if not exists audit (
                id bigint generated always as identity primary key,
                at timestamptz not null default now(),
                license_id text not null,
                action text not null,
                detail text not null
            );
            -- Step 2: payment provider webhooks.
            alter table licenses add column if not exists source_modified_at timestamptz;
            create table if not exists processed_webhooks (
                id text primary key,
                at timestamptz not null default now()
            );
            -- Step 3: license e-mails, one row per license and version of what it grants; no address is kept.
            create table if not exists license_deliveries (
                license_id text not null,
                content_key text not null,
                at timestamptz not null default now(),
                primary key (license_id, content_key)
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The record with <paramref name="id"/>, or null.</summary>
    /// <param name="id">License id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<LicenseRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select id, licensee, plan, kind, seats, scopes, valid_from, period_end, revoked, source from licenses where id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LicenseRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<LicenseKind>(reader.GetString(3), ignoreCase: true),
            reader.GetInt32(4),
            reader.GetFieldValue<string[]>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetBoolean(8),
            reader.GetString(9));
    }

    /// <summary>True when the webhook <paramref name="webhookId"/> was handled before: providers deliver an event more than once.</summary>
    /// <param name="webhookId">The provider's webhook id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<bool> WebhookHandledAsync(string webhookId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select 1 from processed_webhooks where id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = webhookId });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Records that the webhook <paramref name="webhookId"/> was handled. Called after it was applied,
    /// so a failure in between leaves it to the provider's retry; applying twice is harmless, since
    /// provider states are written conditionally.
    /// </summary>
    /// <param name="webhookId">The provider's webhook id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task MarkWebhookHandledAsync(string webhookId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("insert into processed_webhooks (id) values ($1) on conflict (id) do nothing");
        command.Parameters.Add(new NpgsqlParameter { Value = webhookId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>True when the license <paramref name="licenseId"/> was e-mailed with the grants <paramref name="contentKey"/> stands for.</summary>
    /// <param name="licenseId">License id.</param>
    /// <param name="contentKey">See <see cref="LicenseDelivery.ContentKey"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<bool> DeliveredAsync(string licenseId, string contentKey, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select 1 from license_deliveries where license_id = $1 and content_key = $2");
        command.Parameters.Add(new NpgsqlParameter { Value = licenseId });
        command.Parameters.Add(new NpgsqlParameter { Value = contentKey });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Records that the license was e-mailed, with an audit entry, in one transaction.</summary>
    /// <param name="licenseId">License id.</param>
    /// <param name="contentKey">See <see cref="LicenseDelivery.ContentKey"/>.</param>
    /// <param name="detail">What was sent, without the address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task MarkDeliveredAsync(string licenseId, string contentKey, string detail, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(
            "insert into license_deliveries (license_id, content_key) values ($1, $2) on conflict do nothing", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = licenseId });
            insert.Parameters.Add(new NpgsqlParameter { Value = contentKey });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var audit = new NpgsqlCommand("insert into audit (license_id, action, detail) values ($1, 'deliver', $2)", connection, transaction))
        {
            audit.Parameters.Add(new NpgsqlParameter { Value = licenseId });
            audit.Parameters.Add(new NpgsqlParameter { Value = detail });
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Writes an audit entry alone: something about a license that needs an admin.</summary>
    /// <param name="licenseId">License id, which may have no record yet.</param>
    /// <param name="action">What happened.</param>
    /// <param name="detail">Why it needs attention.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task AuditAsync(string licenseId, string action, string detail, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("insert into audit (license_id, action, detail) values ($1, $2, $3)");
        command.Parameters.Add(new NpgsqlParameter { Value = licenseId });
        command.Parameters.Add(new NpgsqlParameter { Value = action });
        command.Parameters.Add(new NpgsqlParameter { Value = detail });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>All records, newest period end first, for the admin command.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<string>> ListIdsAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select id from licenses order by period_end desc, id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>Inserts or replaces <paramref name="record"/> and writes an audit entry, in one transaction.</summary>
    /// <param name="record">The record.</param>
    /// <param name="action">What happened, for the audit: <c>issue</c>, <c>update</c>, <c>revoke</c>.</param>
    /// <param name="detail">Who or what did it and why.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task SaveAsync(LicenseRecord record, string action, string detail, CancellationToken cancellationToken) =>
        SaveAsync(record, action, detail, sourceModified: null, cancellationToken);

    /// <summary>
    /// As <see cref="SaveAsync(LicenseRecord, string, string, CancellationToken)"/>, for a provider's state:
    /// written only when <paramref name="sourceModified"/> is newer than what the record was last built
    /// from, in one conditional statement, so events that arrive late or at the same time never put an
    /// older state back. False when nothing was written.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="action">What happened, for the audit.</param>
    /// <param name="detail">Who or what did it and why.</param>
    /// <param name="sourceModified">When the provider changed the source; null for manual changes, which always apply.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<bool> SaveAsync(LicenseRecord record, string action, string detail, DateTimeOffset? sourceModified, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var upsert = new NpgsqlCommand("""
            insert into licenses (id, licensee, plan, kind, seats, scopes, valid_from, period_end, revoked, source, source_modified_at, updated_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, now())
            on conflict (id) do update set licensee = excluded.licensee, plan = excluded.plan, kind = excluded.kind,
                seats = excluded.seats, scopes = excluded.scopes, valid_from = excluded.valid_from,
                period_end = excluded.period_end, revoked = excluded.revoked, source = excluded.source,
                source_modified_at = coalesce(excluded.source_modified_at, licenses.source_modified_at), updated_at = now()
            where excluded.source_modified_at is null or licenses.source_modified_at is null
                or licenses.source_modified_at < excluded.source_modified_at
            """, connection, transaction))
        {
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Id });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Licensee });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Plan });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Kind.ToString().ToLowerInvariant() });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Seats });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Scopes.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.ValidFrom.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.PeriodEnd.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Revoked });
            upsert.Parameters.Add(new NpgsqlParameter { Value = record.Source });
            upsert.Parameters.Add(new NpgsqlParameter { Value = (object?)sourceModified?.UtcDateTime ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            if (await upsert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                // An older state than the one stored: nothing to audit.
                return false;
            }
        }

        await using (var audit = new NpgsqlCommand("insert into audit (license_id, action, detail) values ($1, $2, $3)", connection, transaction))
        {
            audit.Parameters.Add(new NpgsqlParameter { Value = record.Id });
            audit.Parameters.Add(new NpgsqlParameter { Value = action });
            audit.Parameters.Add(new NpgsqlParameter { Value = detail });
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The audit entries of one license, oldest first, as <c>action: detail</c>.</summary>
    /// <param name="id">License id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<string>> AuditAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select action, detail from audit where license_id = $1 order by id");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add($"{reader.GetString(0)}: {reader.GetString(1)}");
        }

        return entries;
    }
}
