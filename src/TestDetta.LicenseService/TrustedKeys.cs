namespace TestDetta.LicenseService;

/// <summary>Public keys a presented license may be signed with: the current signing key and the retired ones.</summary>
/// <param name="Keys">Key id to SubjectPublicKeyInfo.</param>
public sealed record TrustedKeys(IReadOnlyDictionary<string, byte[]> Keys)
{
    /// <summary>The current key plus the retired keys from the settings.</summary>
    /// <param name="current">The signing key.</param>
    /// <param name="settings">Service settings.</param>
    public static TrustedKeys From(SigningKey current, ServiceSettings settings)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [current.KeyId] = current.PublicKey };
        foreach (var (keyId, publicKey) in settings.RetiredKeys)
        {
            keys.TryAdd(keyId, Convert.FromBase64String(publicKey));
        }

        return new TrustedKeys(keys);
    }
}
