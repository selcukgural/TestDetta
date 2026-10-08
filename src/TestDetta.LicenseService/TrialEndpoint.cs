using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// The site's card-free trial (docs/design/licensing.md, "Trials"): <c>GET /v1/trials/challenge</c> hands out
/// a proof-of-work token, <c>POST /v1/trials</c> takes an address and the repositories and e-mails a trial
/// license. Sending the license to the address is the check that it belongs to the visitor. One trial per
/// address and per owner within 12 months, a daily ceiling over all trials, and a per-address rate limit.
/// Every well-formed request gets the same answer after the same minimum time, whether a license went out
/// or the address or owner had a trial already, so nobody can find out who tried TestDetta.
/// </summary>
public static partial class TrialEndpoint
{
    /// <summary>Plan name a trial license carries.</summary>
    public const string Plan = "TestDetta Team";

    /// <summary>HKDF purpose of the challenge HMAC key.</summary>
    public const string ChallengePurpose = "testdetta trial challenge v1";

    /// <summary>HKDF purpose of the address HMAC key.</summary>
    public const string EmailPurpose = "testdetta trial email v1";

    /// <summary>Largest request body: an address, a few patterns, a token and a nonce.</summary>
    public const long MaxBodyBytes = 4 * 1024;

    /// <summary>Hands out a challenge token and how many zero bits its proof needs.</summary>
    /// <param name="services">What the endpoint needs from the service.</param>
    public static IResult Challenge([AsParameters] TrialServices services)
    {
        if (!services.Settings.TrialsEnabled)
        {
            return Results.NotFound();
        }

        return Results.Json(new TrialChallengeAnswer(services.Challenge.Issue(services.Clock.GetUtcNow()), services.Challenge.Bits), TrialJson.Default.TrialChallengeAnswer);
    }

    /// <summary>Handles one trial request.</summary>
    /// <param name="context">The request.</param>
    /// <param name="services">What the endpoint needs from the service.</param>
    public static async Task<IResult> RequestAsync(HttpContext context, [AsParameters] TrialServices services)
    {
        var started = Stopwatch.GetTimestamp();
        var (settings, challenge, trials, store, delivery, clock, signingKey, logger) = services;
        if (!settings.TrialsEnabled)
        {
            return Results.NotFound();
        }

        var ask = await ReadAsync(context);
        if (ask is null)
        {
            return Invalid("request");
        }

        var email = ask.Email?.Trim() ?? "";
        if (!LicenseDelivery.IsDeliverable(email))
        {
            return Invalid("email");
        }

        if (TrialScopes.Parse(ask.Repositories) is not { } scopes)
        {
            return Invalid("repositories");
        }

        var now = clock.GetUtcNow();
        if (!challenge.Check(ask.Challenge, ask.Nonce, now, out var challengeId) || !await trials.UseChallengeAsync(challengeId, context.RequestAborted))
        {
            return Invalid("challenge");
        }

        var id = "T-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var endsAt = now.AddDays(settings.TrialDays);
        var emailHash = HMACSHA256.HashData(signingKey.DeriveKey(EmailPurpose), Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
        var reservation = await trials.ReserveAsync(new TrialRequest(id, emailHash, email, scopes.Owners, endsAt), settings.TrialsPerDay, context.RequestAborted);
        if (reservation == TrialReservation.PausedForToday)
        {
            LogPaused(logger, settings.TrialsPerDay);
            return Results.Json(new TrialAnswer("paused"), TrialJson.Default.TrialAnswer, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        bool issued;
        try
        {
            issued = reservation != TrialReservation.Reserved || await IssueAsync(new TrialIssue(id, email, scopes, now, endsAt), services, context.RequestAborted);
        }
        catch
        {
            // Anything else (the database, say) must not keep the owner and address blocked for a year.
            await trials.ReleaseAsync(id, CancellationToken.None);
            throw;
        }

        if (!issued)
        {
            await trials.ReleaseAsync(id, context.RequestAborted);
            await AnswerNoSoonerAsync(started, settings.TrialMinimumResponse, context.RequestAborted);
            return Results.Json(new TrialAnswer("retry"), TrialJson.Default.TrialAnswer, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        LogDecided(logger, reservation == TrialReservation.Reserved ? id : "-", reservation, scopes.Owners.Count);
        await AnswerNoSoonerAsync(started, settings.TrialMinimumResponse, context.RequestAborted);
        return Results.Json(new TrialAnswer("accepted"), TrialJson.Default.TrialAnswer, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Saves the trial's license record and e-mails it. False when the mail did not go out (provider down,
    /// address refused): the caller releases the reservation and the record is revoked, so a retry can work
    /// and nothing half-issued stays valid.
    /// </summary>
    /// <param name="trial">The reserved trial.</param>
    /// <param name="services">What the endpoint needs from the service.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private static async Task<bool> IssueAsync(TrialIssue trial, TrialServices services, CancellationToken cancellationToken)
    {
        var record = new LicenseRecord(
            trial.Id,
            $"Trial for {trial.Scopes.Owners[0]}",
            Plan,
            LicenseKind.Trial,
            services.Settings.TrialSeats,
            trial.Scopes.Patterns,
            trial.Start,
            trial.EndsAt,
            false,
            "trial");
        await services.Store.SaveAsync(record, "trial", $"trial from the site: {record.Seats} seats, {trial.Scopes.Patterns.Count} pattern(s), until {trial.EndsAt:yyyy-MM-dd}", null, cancellationToken);
        try
        {
            await services.Delivery.DeliverAsync(trial.Id, trial.Email, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogDeliveryFailed(services.Logger, trial.Id);
        }

        if (await services.Store.DeliveredAsync(trial.Id, LicenseDelivery.ContentKey(record), cancellationToken))
        {
            return true;
        }

        await services.Store.SaveAsync(record with { Revoked = true }, "revoke", "trial license e-mail did not go out", null, cancellationToken);
        return false;
    }

    /// <summary>The request body, or null when it is not the JSON object the form sends.</summary>
    /// <param name="context">The request.</param>
    private static async Task<TrialAsk?> ReadAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > MaxBodyBytes || !(context.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, TrialJson.Default.TrialAsk, context.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A 400 naming the field the form should ask for again.</summary>
    /// <param name="field">email, repositories, challenge or request.</param>
    private static IResult Invalid(string field) =>
        Results.Json(new TrialAnswer("invalid") { Field = field }, TrialJson.Default.TrialAnswer, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Waits until <paramref name="minimum"/> has passed since <paramref name="started"/>.</summary>
    /// <param name="started">When the request started (<see cref="Stopwatch.GetTimestamp"/>).</param>
    /// <param name="minimum">Shortest answer time.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private static async Task AnswerNoSoonerAsync(long started, TimeSpan minimum, CancellationToken cancellationToken)
    {
        var left = minimum - Stopwatch.GetElapsedTime(started);
        if (left > TimeSpan.Zero)
        {
            await Task.Delay(left, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Trial request: {Outcome}, license {LicenseId}, {Owners} owner(s)")]
    private static partial void LogDecided(ILogger logger, string licenseId, TrialReservation outcome, int owners);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trials paused: the daily ceiling of {PerDay} is reached")]
    private static partial void LogPaused(ILogger logger, int perDay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trial {LicenseId}: the license e-mail did not go out; the reservation is released")]
    private static partial void LogDeliveryFailed(ILogger logger, string licenseId);
}
