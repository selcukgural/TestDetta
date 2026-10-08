using Npgsql;
using NpgsqlTypes;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

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
        // Two instances, the trial job and the admin job may start at once; the lock runs the steps one at a time.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select pg_advisory_xact_lock(73110019);
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
            -- Step 4: what each handled webhook did, without its body (which carries the customer's name and
            -- address). Rows from before stay null.
            alter table processed_webhooks add column if not exists type text;
            alter table processed_webhooks add column if not exists license_id text;
            alter table processed_webhooks add column if not exists outcome text;
            -- Step 5: what an admin decided stays put when the payment provider's next event arrives: scopes
            -- set by hand (a mistyped checkout field) and a revocation (a refund).
            alter table licenses add column if not exists scopes_override text[];
            alter table licenses add column if not exists admin_revoked boolean not null default false;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The record with <paramref name="id"/>, or null.</summary>
    /// <param name="id">License id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<LicenseRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select id, licensee, plan, kind, seats, scopes, valid_from, period_end, revoked, source, scopes_override is not null, admin_revoked from licenses where id = $1");
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
            reader.GetString(9))
        {
            ScopesSetByAdmin = reader.GetBoolean(10),
            RevokedByAdmin = reader.GetBoolean(11),
        };
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
    /// Records that the webhook <paramref name="webhookId"/> was handled and what it did. Called after it was
    /// applied, so a failure in between leaves it to the provider's retry; applying twice is harmless, since
    /// provider states are written conditionally. Only the event type, the license and the outcome are
    /// kept, enough to answer "the provider says delivered, what happened?" without personal data.
    /// </summary>
    /// <param name="webhookId">The provider's webhook id.</param>
    /// <param name="type">The event type, e.g. <c>subscription.updated</c>.</param>
    /// <param name="licenseId">The license the event is about; null when it is about none.</param>
    /// <param name="outcome">What handling did.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task MarkWebhookHandledAsync(string webhookId, string type, string? licenseId, WebhookOutcome outcome, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "insert into processed_webhooks (id, type, license_id, outcome) values ($1, $2, $3, $4) on conflict (id) do nothing");
        command.Parameters.Add(new NpgsqlParameter { Value = webhookId });
        command.Parameters.Add(new NpgsqlParameter { Value = type });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)licenseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = outcome.ToString() });
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
                seats = excluded.seats, valid_from = excluded.valid_from, period_end = excluded.period_end, source = excluded.source,
                -- A provider's state (source_modified_at set) never undoes an admin's decision.
                scopes = case when excluded.source_modified_at is not null and licenses.scopes_override is not null
                    then licenses.scopes_override else excluded.scopes end,
                revoked = excluded.revoked or (excluded.source_modified_at is not null and licenses.admin_revoked),
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

    /// <summary>
    /// Sets the scopes of <paramref name="id"/> by hand, with an audit entry: they replace the current ones and
    /// stay when the payment provider's next event carries other scopes. False when there is no such record.
    /// </summary>
    /// <param name="id">License id.</param>
    /// <param name="scopes">The scopes, already normalised (<see cref="ScopePatterns"/>).</param>
    /// <param name="detail">Who did it and why.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<bool> OverrideScopesAsync(string id, IReadOnlyList<string> scopes, string detail, CancellationToken cancellationToken) =>
        AdminChangeAsync(
            "update licenses set scopes = $2, scopes_override = $2, updated_at = now() where id = $1",
            id,
            new NpgsqlParameter { Value = scopes.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            "scopes",
            detail,
            cancellationToken);

    /// <summary>
    /// Revokes <paramref name="id"/> for good (<paramref name="revoked"/> true: an admin's revocation or a full
    /// refund), or lifts such a revocation (false), with an audit entry. A revocation made here outlives the
    /// payment provider's later events, which would otherwise rebuild the record as active. False when there
    /// is no such record.
    /// </summary>
    /// <param name="id">License id.</param>
    /// <param name="revoked">True to revoke, false to restore.</param>
    /// <param name="action">For the audit: <c>revoke</c>, <c>refund</c> or <c>restore</c>.</param>
    /// <param name="detail">Who or what did it and why.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<bool> SetRevokedAsync(string id, bool revoked, string action, string detail, CancellationToken cancellationToken) =>
        AdminChangeAsync(
            "update licenses set revoked = $2, admin_revoked = $2, updated_at = now() where id = $1",
            id,
            new NpgsqlParameter { Value = revoked },
            action,
            detail,
            cancellationToken);

    /// <summary>Runs one update of a record and its audit entry in a transaction; false when the record does not exist.</summary>
    /// <param name="sql">The update, with the id as <c>$1</c> and <paramref name="value"/> as <c>$2</c>.</param>
    /// <param name="id">License id.</param>
    /// <param name="value">The new value.</param>
    /// <param name="action">For the audit.</param>
    /// <param name="detail">For the audit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private async Task<bool> AdminChangeAsync(string sql, string id, NpgsqlParameter value, string action, string detail, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand(sql, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = id });
            update.Parameters.Add(value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return false;
            }
        }

        await using (var audit = new NpgsqlCommand("insert into audit (license_id, action, detail) values ($1, $2, $3)", connection, transaction))
        {
            audit.Parameters.Add(new NpgsqlParameter { Value = id });
            audit.Parameters.Add(new NpgsqlParameter { Value = action });
            audit.Parameters.Add(new NpgsqlParameter { Value = detail });
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The <c>attention</c> audit entries since <paramref name="since"/>, newest first, as
    /// <c>time license: detail</c>: what an admin must look at (a checkout without a usable scope, a refused
    /// e-mail, a refund).
    /// </summary>
    /// <param name="since">Earliest entry.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<string>> AttentionSinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select at, license_id, detail from audit where action = 'attention' and at >= $1 order by at desc, id desc");
        command.Parameters.Add(new NpgsqlParameter { Value = since.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add($"{reader.GetFieldValue<DateTimeOffset>(0):yyyy-MM-dd HH:mm} {reader.GetString(1)}: {reader.GetString(2)}");
        }

        return entries;
    }

    /// <summary>
    /// Paid licenses that were never e-mailed although they could be: from a provider, not revoked, with scopes,
    /// last changed more than an hour ago, and no delivery recorded. A long outage of the e-mail provider outlasts
    /// the provider's webhook retries and leaves exactly these; <c>admin send</c> delivers them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<string>> UndeliveredAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select id from licenses l
            where source not in ('manual', 'trial') and not revoked and cardinality(scopes) > 0
              and updated_at < now() - interval '1 hour'
              and not exists (select 1 from license_deliveries d where d.license_id = l.id)
            order by updated_at
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
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
