using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TestDetta.LicenseService;

/// <summary>
/// A proof-of-work step in front of the trial form, with no third party and nothing for the visitor to
/// solve by hand. The service hands out a token <c>&lt;unix seconds&gt;.&lt;random&gt;.&lt;mac&gt;</c>, an
/// HMAC under a key derived from the signing key, so any instance can check it without storing it. The
/// browser looks for a nonce such that SHA-256 over <c>&lt;token&gt;.&lt;nonce&gt;</c> starts with
/// <see cref="Bits"/> zero bits (about 2^Bits hashes, a few seconds while the visitor types). A token is
/// good for <see cref="Lifetime"/> and once only: the store remembers the random part of the used ones.
/// </summary>
/// <param name="key">The HMAC key (<see cref="SigningKey.DeriveKey"/>).</param>
/// <param name="bits">Leading zero bits the proof needs.</param>
public sealed class TrialChallenge(byte[] key, int bits)
{
    /// <summary>How long a token may be solved and used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Leading zero bits a proof needs.</summary>
    public int Bits { get; } = bits;

    /// <summary>A new token, issued at <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    public string Issue(DateTimeOffset now)
    {
        var body = $"{now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}.{Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16))}";
        return $"{body}.{Mac(body)}";
    }

    /// <summary>
    /// True when <paramref name="token"/> is one of ours, not older than <see cref="Lifetime"/>, and
    /// <paramref name="nonce"/> proves the work; <paramref name="id"/> is then its random part, which the
    /// caller marks as used.
    /// </summary>
    /// <param name="token">The token the browser got.</param>
    /// <param name="nonce">The nonce it found: decimal digits.</param>
    /// <param name="now">The current time.</param>
    /// <param name="id">The token's random part, for the replay check.</param>
    public bool Check(string? token, string? nonce, DateTimeOffset now, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? id)
    {
        id = null;
        if (token is not { Length: > 0 and <= 128 } || nonce is not { Length: > 0 and <= 20 } || !nonce.All(char.IsAsciiDigit))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var body = $"{parts[0]}.{parts[1]}";
        var age = now - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age > Lifetime || age < TimeSpan.FromMinutes(-1)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(body)), Encoding.ASCII.GetBytes(parts[2]))
            || !Proves(SHA256.HashData(Encoding.UTF8.GetBytes($"{token}.{nonce}")), Bits))
        {
            return false;
        }

        id = parts[1];
        return true;
    }

    /// <summary>True when <paramref name="hash"/> starts with <paramref name="bits"/> zero bits.</summary>
    /// <param name="hash">A SHA-256 hash.</param>
    /// <param name="bits">Zero bits needed.</param>
    public static bool Proves(ReadOnlySpan<byte> hash, int bits)
    {
        var full = bits / 8;
        foreach (var b in hash[..full])
        {
            if (b != 0)
            {
                return false;
            }
        }

        var rest = bits % 8;
        return rest == 0 || hash[full] >> (8 - rest) == 0;
    }

    /// <summary>The MAC of a token's body, 16 bytes in base64url.</summary>
    /// <param name="body">Seconds and random part.</param>
    private string Mac(string body) => Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)).AsSpan(0, 16));
}
