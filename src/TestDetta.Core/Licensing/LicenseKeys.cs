namespace TestDetta.Core.Licensing;

/// <summary>The public keys TestDetta trusts and whether a license is enforced yet.</summary>
public static class LicenseKeys
{
    /// <summary>
    /// Public keys (SubjectPublicKeyInfo) by key id: <c>td-2026-10</c>, the license service's production key
    /// (<c>deploy/license-service/admin.sh production public-key</c>). The sandbox's key is never trusted.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Trusted { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>
    /// True since launch: without a license that covers the run, every test runs. It was false before, when
    /// no production key was trusted and every run would otherwise have run every test; a test keeps the
    /// two together (enforced only with the production key trusted).
    /// </summary>
    public const bool Enforced = true;
}
