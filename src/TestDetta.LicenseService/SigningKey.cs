using System.Security.Cryptography;
using TestDetta.Core.Licensing;

namespace TestDetta.LicenseService;

/// <summary>
/// The key the service signs licenses with. Read from a PEM file (or, later, a cloud KMS behind the
/// same members); its private part never leaves this object and is never logged.
/// </summary>
public sealed class SigningKey : IDisposable
{
    private readonly ECDsa _key;

    /// <summary>Loads an ECDSA P-256 private key from PEM text.</summary>
    /// <param name="keyId">Id licenses carry to name this key (<c>kid</c>).</param>
    /// <param name="pem">The private key as PEM.</param>
    /// <exception cref="InvalidOperationException">The key is not P-256.</exception>
    public SigningKey(string keyId, string pem)
    {
        KeyId = keyId;
        _key = ECDsa.Create();
        _key.ImportFromPem(pem);
        if (_key.KeySize != 256)
        {
            _key.Dispose();
            throw new InvalidOperationException("The signing key must be ECDSA P-256.");
        }
    }

    /// <summary>Id licenses carry to name this key.</summary>
    public string KeyId { get; }

    /// <summary>The public part, as SubjectPublicKeyInfo: what TestDetta embeds to verify licenses.</summary>
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    /// <summary>
    /// A 32-byte secret for <paramref name="purpose"/>, derived from the private key with HKDF-SHA256, so the
    /// service needs no further secret for its HMACs (trial challenges, e-mail hashes). Every instance holding
    /// the key derives the same value; a new signing key gives new values, which starts those HMACs afresh.
    /// </summary>
    /// <param name="purpose">What the secret is for; different purposes give unrelated secrets.</param>
    public byte[] DeriveKey(string purpose)
    {
        var secret = _key.ExportParameters(includePrivateParameters: true).D!;
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, info: System.Text.Encoding.UTF8.GetBytes(purpose));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Signs <paramref name="claims"/>, whose key id must be <see cref="KeyId"/>.</summary>
    /// <param name="claims">What the license grants.</param>
    public string Sign(LicenseClaims claims) => License.Sign(claims, _key);

    /// <inheritdoc/>
    public void Dispose() => _key.Dispose();
}
