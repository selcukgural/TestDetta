namespace TestDetta.Core.Licensing;

/// <summary>The public keys TestDetta trusts and whether a license is enforced yet.</summary>
public static class LicenseKeys
{
    /// <summary>
    /// Public keys (SubjectPublicKeyInfo) by key id. Empty until the license service exists and its
    /// first production key is made (docs/design/licensing.md, phase 2): no license verifies until then.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Trusted { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>
    /// False until launch: the license is checked and reported, but tests are selected whatever the
    /// outcome. Without a production key every run would otherwise run every test.
    /// </summary>
    public const bool Enforced = false;
}
