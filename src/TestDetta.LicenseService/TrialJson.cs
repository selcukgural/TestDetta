using System.Text.Json.Serialization;

namespace TestDetta.LicenseService;

/// <summary>Source-generated JSON for the trial endpoints, camelCase like the form.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TrialAsk))]
[JsonSerializable(typeof(TrialChallengeAnswer))]
[JsonSerializable(typeof(TrialAnswer))]
public sealed partial class TrialJson : JsonSerializerContext;
