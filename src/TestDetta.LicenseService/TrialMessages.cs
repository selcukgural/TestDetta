using System.Text.Json.Serialization;

namespace TestDetta.LicenseService;

/// <summary>The trial form's request body.</summary>
/// <param name="Email">Where the license goes.</param>
/// <param name="Repositories">Repository patterns, separated by commas, spaces or new lines.</param>
/// <param name="Challenge">The token from <c>/v1/trials/challenge</c>.</param>
/// <param name="Nonce">The proof of work for it.</param>
public sealed record TrialAsk(string? Email, string? Repositories, string? Challenge, string? Nonce);

/// <summary>Answer to <c>/v1/trials/challenge</c>.</summary>
/// <param name="Challenge">The token to solve.</param>
/// <param name="Bits">Leading zero bits the proof needs.</param>
public sealed record TrialChallengeAnswer(string Challenge, int Bits);

/// <summary>Answer to <c>POST /v1/trials</c>: accepted, invalid (with the field), paused or retry.</summary>
/// <param name="Status">What happened, for the form's message.</param>
public sealed record TrialAnswer(string Status)
{
    /// <summary>The field to ask for again, when the status is invalid.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; init; }
}

/// <summary>A reserved trial on its way to a license.</summary>
/// <param name="Id">Trial and license id.</param>
/// <param name="Email">Where the license goes.</param>
/// <param name="Scopes">What it covers.</param>
/// <param name="Start">When it starts.</param>
/// <param name="EndsAt">When it ends.</param>
public sealed record TrialIssue(string Id, string Email, TrialScopes Scopes, DateTimeOffset Start, DateTimeOffset EndsAt);
