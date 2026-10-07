namespace TestDetta.LicenseService;

/// <summary>
/// <c>trials daily</c>: the once-a-day job for trials, run as a Cloud Run job on a schedule rather than inside
/// the service, which scales to zero and has no clock of its own. It reminds trials that end within
/// <see cref="RemindAhead"/> (once each, never an owner that bought since) and then forgets what the
/// privacy policy says goes: addresses 7 days after their trial, old trials and used challenges.
/// </summary>
/// <param name="trials">Trial reservations.</param>
/// <param name="mailer">Sends the reminders; null skips them (nothing is marked reminded then).</param>
/// <param name="logger">Receives counts and trial ids, never addresses.</param>
public sealed partial class TrialDaily(TrialStore trials, ILicenseMailer? mailer, ILogger<TrialDaily> logger)
{
    /// <summary>How long before its end a trial is reminded.</summary>
    public static readonly TimeSpan RemindAhead = TimeSpan.FromDays(3);

    /// <summary>Runs the job; returns the process exit code (1 when a reminder could not be sent, so the job shows red).</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var failed = 0;
        var sent = 0;
        if (mailer is not null)
        {
            foreach (var trial in await trials.DueForReminderAsync(RemindAhead, cancellationToken))
            {
                try
                {
                    if (await mailer.SendAsync(TrialReminderMail.For(trial), $"{trial.Id}/reminder", cancellationToken) == MailOutcome.Rejected)
                    {
                        LogRefused(logger, trial.Id);
                    }

                    // A refused address will not get better: it is marked too, so it is not tried every day.
                    await trials.MarkRemindedAsync(trial.Id, cancellationToken);
                    sent++;
                }
                catch (HttpRequestException)
                {
                    failed++;
                    LogFailed(logger, trial.Id);
                }
            }
        }

        var forgotten = await trials.PurgeAsync(cancellationToken);
        LogDone(logger, sent, failed, forgotten);
        return failed == 0 ? 0 : 1;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Trials daily: {Sent} reminder(s) sent, {Failed} failed, {Forgotten} address(es) forgotten")]
    private static partial void LogDone(ILogger logger, int sent, int failed, int forgotten);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trial {TrialId}: the reminder could not be sent now; it is tried again tomorrow")]
    private static partial void LogFailed(ILogger logger, string trialId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trial {TrialId}: the e-mail provider refused the reminder")]
    private static partial void LogRefused(ILogger logger, string trialId);
}
