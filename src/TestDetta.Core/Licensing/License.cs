using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestDetta.Core.Licensing;

/// <summary>
/// A license string: <c>testdetta1.&lt;base64url payload&gt;.&lt;base64url signature&gt;</c>. The payload is
/// the claims as JSON; the signature is ECDSA P-256 with SHA-256 over <c>testdetta1.&lt;payload&gt;</c>, made
/// by the license service, the only holder of the private key. ECDSA P-256 is built into .NET, so no
/// package is needed (the design first named Ed25519, which is not).
/// </summary>
public static class License
{
    /// <summary>Prefix of every license string, with the format version.</summary>
    public const string Prefix = "testdetta1.";

    /// <summary>
    /// Reads and verifies a license string against <paramref name="trustedKeys"/> (key id to public key,
    /// as SubjectPublicKeyInfo). Null, with the reason, when the string is malformed, its key is unknown
    /// or its signature does not match.
    /// </summary>
    /// <param name="text">The license string.</param>
    /// <param name="trustedKeys">Public keys TestDetta trusts, by key id.</param>
    /// <param name="error">Why it was rejected; null when it was not.</param>
    public static LicenseClaims? Verify(string text, IReadOnlyDictionary<string, byte[]> trustedKeys, out string? error)
    {
        text = text.Trim();
        var parts = text.Split('.');
        if (parts.Length != 3 || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "it is not a TestDetta license string";
            return null;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64Url.DecodeFromChars(parts[1]);
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (FormatException)
        {
            error = "it is not a TestDetta license string";
            return null;
        }

        LicenseClaims claims;
        try
        {
            claims = ReadClaims(payload);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            error = "its content cannot be read";
            return null;
        }

        if (!trustedKeys.TryGetValue(claims.KeyId, out var publicKey))
        {
            error = $"it is signed with an unknown key ({claims.KeyId}); a newer TestDetta may know it";
            return null;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        if (!ecdsa.VerifyData(Encoding.ASCII.GetBytes(Prefix + parts[1]), signature, HashAlgorithmName.SHA256))
        {
            error = "its signature does not match its content";
            return null;
        }

        error = null;
        return claims;
    }

    /// <summary>Signs <paramref name="claims"/> into a license string. Used by the license service and by tests.</summary>
    /// <param name="claims">What the license grants; its key id must name <paramref name="privateKey"/>.</param>
    /// <param name="privateKey">The signing key.</param>
    public static string Sign(LicenseClaims claims, ECDsa privateKey)
    {
        var payload = Base64Url.EncodeToString(WriteClaims(claims));
        var signature = privateKey.SignData(Encoding.ASCII.GetBytes(Prefix + payload), HashAlgorithmName.SHA256);
        return $"{Prefix}{payload}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>The claims as UTF-8 JSON, in the format of docs/design/licensing.md.</summary>
    /// <param name="claims">Claims to write.</param>
    private static byte[] WriteClaims(LicenseClaims claims)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("v"u8, 1);
            json.WriteString("id"u8, claims.Id);
            json.WriteString("kid"u8, claims.KeyId);
            json.WriteString("plan"u8, claims.Plan);
            json.WriteString("kind"u8, claims.Kind.ToString().ToLowerInvariant());
            json.WriteString("licensee"u8, claims.Licensee);
            json.WriteNumber("seats"u8, claims.Seats);
            json.WriteStartArray("scopes"u8);
            foreach (var scope in claims.Scopes)
            {
                json.WriteStringValue(scope);
            }

            json.WriteEndArray();
            json.WriteString("issued"u8, claims.Issued.UtcDateTime);
            json.WriteString("validFrom"u8, claims.ValidFrom.UtcDateTime);
            json.WriteString("validUntil"u8, claims.ValidUntil.UtcDateTime);
            if (claims.Refresh is { } refresh)
            {
                json.WriteString("refresh"u8, refresh.AbsoluteUri);
            }

            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Reads the claims JSON; throws when a field is missing or of the wrong type, or the version is unknown.</summary>
    /// <param name="payload">UTF-8 JSON.</param>
    private static LicenseClaims ReadClaims(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("v").GetInt32() != 1)
        {
            throw new FormatException("unknown license version");
        }

        var kind = root.GetProperty("kind").GetString() switch
        {
            "subscription" => LicenseKind.Subscription,
            "annual" => LicenseKind.Annual,
            "trial" => LicenseKind.Trial,
            _ => throw new FormatException("unknown license kind"),
        };

        return new LicenseClaims(
            root.GetProperty("id").GetString()!,
            root.GetProperty("kid").GetString()!,
            root.GetProperty("plan").GetString()!,
            kind,
            root.GetProperty("licensee").GetString()!,
            root.GetProperty("seats").GetInt32(),
            [.. root.GetProperty("scopes").EnumerateArray().Select(static s => s.GetString()!)],
            root.GetProperty("validFrom").GetDateTimeOffset(),
            root.GetProperty("validUntil").GetDateTimeOffset(),
            root.TryGetProperty("refresh", out var refresh) && refresh.GetString() is { } url ? new Uri(url) : null)
        {
            Issued = root.GetProperty("issued").GetDateTimeOffset(),
        };
    }
}
