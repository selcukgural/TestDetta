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

    /// <summary>Signs <paramref name="claims"/>, whose key id must be <see cref="KeyId"/>.</summary>
    /// <param name="claims">What the license grants.</param>
    public string Sign(LicenseClaims claims) => License.Sign(claims, _key);

    /// <inheritdoc/>
    public void Dispose() => _key.Dispose();
}
