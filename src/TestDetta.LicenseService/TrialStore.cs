using Npgsql;
using NpgsqlTypes;

namespace TestDetta.LicenseService;

/// <summary>
/// The trials the site handed out, kept to enforce one trial per owner and per address and to remind the
/// visitor before the trial ends. The address itself is kept only until 7 days after the trial ends
/// (<see cref="PurgeAsync"/>); what stays for 12 months is its HMAC and the owner keys, which is all the
/// one-trial rule needs.
/// </summary>
/// <param name="dataSource">Connections to the service's database.</param>
public sealed class TrialStore(NpgsqlDataSource dataSource)
{
    /// <summary>How long one owner or address waits for another trial.</summary>
    public static readonly TimeSpan OneTrialPer = TimeSpan.FromDays(365);

    /// <summary>Creates the tables when missing.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            create table if not exists trials (
                id text primary key,
                email_hash bytea not null,
                email text,
                owners text[] not null,
                created_at timestamptz not null default now(),
                ends_at timestamptz not null,
                reminded_at timestamptz
            );
            create index if not exists trials_created_at on trials (created_at);
            create table if not exists trial_challenges (
                id text primary key,
                at timestamptz not null default now()
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>True the first time the challenge <paramref name="id"/> is used; a replay gets false.</summary>
    /// <param name="id">The token's random part.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<bool> UseChallengeAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("insert into trial_challenges (id) values ($1) on conflict (id) do nothing");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Reserves a trial, or says why not, in one transaction under an advisory lock so concurrent requests
    /// cannot both pass the checks: the daily ceiling over all trials first, then one trial per address hash
    /// and per owner within <see cref="OneTrialPer"/>.
    /// </summary>
    /// <param name="trial">The trial to reserve.</param>
    /// <param name="perDay">Most trials handed out in any 24 hours.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<TrialReservation> ReserveAsync(TrialRequest trial, int perDay, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockTrials = new NpgsqlCommand("select pg_advisory_xact_lock(73110017)", connection, transaction))
        {
            await lockTrials.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var today = new NpgsqlCommand("select count(*) from trials where created_at > now() - interval '1 day'", connection, transaction))
        {
            if ((long)(await today.ExecuteScalarAsync(cancellationToken))! >= perDay)
            {
                return TrialReservation.PausedForToday;
            }
        }

        await using (var earlier = new NpgsqlCommand("select 1 from trials where created_at > now() - $3 and (email_hash = $1 or owners && $2) limit 1", connection, transaction))
        {
            earlier.Parameters.Add(new NpgsqlParameter { Value = trial.EmailHash, NpgsqlDbType = NpgsqlDbType.Bytea });
            earlier.Parameters.Add(new NpgsqlParameter { Value = trial.Owners.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            earlier.Parameters.Add(new NpgsqlParameter { Value = OneTrialPer, NpgsqlDbType = NpgsqlDbType.Interval });
            if (await earlier.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return TrialReservation.HadOne;
            }
        }

        await using (var insert = new NpgsqlCommand("insert into trials (id, email_hash, email, owners, ends_at) values ($1, $2, $3, $4, $5)", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = trial.Id });
            insert.Parameters.Add(new NpgsqlParameter { Value = trial.EmailHash, NpgsqlDbType = NpgsqlDbType.Bytea });
            insert.Parameters.Add(new NpgsqlParameter { Value = trial.Email });
            insert.Parameters.Add(new NpgsqlParameter { Value = trial.Owners.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            insert.Parameters.Add(new NpgsqlParameter { Value = trial.EndsAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return TrialReservation.Reserved;
    }

    /// <summary>Gives a reservation back when its license could not be e-mailed, so the visitor can try again.</summary>
    /// <param name="id">Trial id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task ReleaseAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("delete from trials where id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Trials that end within <paramref name="ahead"/> of now, were not reminded yet and still have an
    /// address, leaving out owners that bought a license since (a Polar license whose scopes name them).
    /// </summary>
    /// <param name="ahead">How far before its end a trial is reminded.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<TrialToRemind>> DueForReminderAsync(TimeSpan ahead, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select t.id, t.email, t.ends_at, t.owners from trials t
            where t.email is not null and t.reminded_at is null and t.ends_at > now() and t.ends_at <= now() + $1
              and not exists (
                select 1 from licenses l, unnest(l.scopes) s
                where l.source = 'polar' and not l.revoked
                  and split_part(s, '/', 1) || '/' || split_part(s, '/', 2) = any(t.owners))
            order by t.ends_at
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = ahead, NpgsqlDbType = NpgsqlDbType.Interval });
        var due = new List<TrialToRemind>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add(new TrialToRemind(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<string[]>(3)));
        }

        return due;
    }

    /// <summary>Records that the trial <paramref name="id"/> was reminded, so it never is twice.</summary>
    /// <param name="id">Trial id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task MarkRemindedAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("update trials set reminded_at = now() where id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Forgets what is no longer needed: addresses 7 days after their trial ended, whole trials once the
    /// one-trial window has passed (with some margin), used challenges after a day. Returns how many
    /// addresses were forgotten.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("update trials set email = null where email is not null and ends_at < now() - interval '7 days'"));
        batch.BatchCommands.Add(new NpgsqlBatchCommand("delete from trials where created_at < now() - interval '400 days'"));
        batch.BatchCommands.Add(new NpgsqlBatchCommand("delete from trial_challenges where at < now() - interval '1 day'"));
        await batch.ExecuteNonQueryAsync(cancellationToken);
        return batch.BatchCommands[0].RecordsAffected;
    }
}

/// <summary>A trial to reserve.</summary>
/// <param name="Id">Trial id, also the license id.</param>
/// <param name="EmailHash">HMAC of the lower-cased address.</param>
/// <param name="Email">The address, kept until 7 days after the trial ends for the reminder.</param>
/// <param name="Owners">Owner keys the trial covers.</param>
/// <param name="EndsAt">When the trial ends.</param>
public sealed record TrialRequest(string Id, byte[] EmailHash, string Email, IReadOnlyList<string> Owners, DateTimeOffset EndsAt);

/// <summary>A trial whose reminder is due.</summary>
/// <param name="Id">Trial id.</param>
/// <param name="Email">Where the reminder goes.</param>
/// <param name="EndsAt">When the trial ends.</param>
/// <param name="Owners">Owner keys the trial covers.</param>
public sealed record TrialToRemind(string Id, string Email, DateTimeOffset EndsAt, IReadOnlyList<string> Owners);
