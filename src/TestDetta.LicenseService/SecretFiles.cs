namespace TestDetta.LicenseService;

/// <summary>
/// Settings read from files, one file per setting, so secrets need not be environment variables
/// (which leak into process listings, crash reports and child processes). <c>TESTDETTA_SECRETS</c> names a
/// folder: every file in it and in its immediate subfolders is a setting named after the file, with
/// <c>__</c> for <c>:</c> (<c>Licenses__ConnectionString</c>). Subfolders exist because Cloud Run mounts
/// one secret per folder. Files win over environment variables and appsettings.json.
/// </summary>
internal static class SecretFiles
{
    /// <summary>Environment variable naming the folder of secret files.</summary>
    public const string Variable = "TESTDETTA_SECRETS";

    /// <summary>Adds the secret files under <paramref name="root"/> to <paramref name="builder"/>.</summary>
    /// <param name="builder">Configuration being built.</param>
    /// <param name="root">The folder <see cref="Variable"/> names; null or missing adds nothing.</param>
    public static IConfigurationBuilder Add(IConfigurationBuilder builder, string? root)
    {
        if (root is not { Length: > 0 } || !Directory.Exists(root))
        {
            return builder;
        }

        builder.AddKeyPerFile(root, optional: true);
        foreach (var folder in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            // Cloud Run's own entries (..data, timestamped folders) start with a dot.
            if (!Path.GetFileName(folder).StartsWith('.'))
            {
                builder.AddKeyPerFile(folder, optional: true);
            }
        }

        return builder;
    }
}
