namespace TestDetta.Mutants;

/// <summary>The repository's own assemblies among the files of test output folders, where mutants are written in.</summary>
internal static class RepositoryAssemblies
{
    /// <summary>
    /// Every assembly built from the repository found in the test outputs. The mutant is injected
    /// wherever its method exists, whichever project TestDetta thinks the source belongs to. A copy that
    /// came from a NuGet package instead (Carter's Newtonsoft negotiator tests use the published
    /// Carter package, not the project) is left alone: no source change can reach it.
    /// </summary>
    /// <param name="names">Assembly file names of the repository's projects.</param>
    /// <param name="outputs">Output directories of the evaluated test projects.</param>
    /// <param name="packagesRoot">NuGet global packages folder; null for the default (<c>NUGET_PACKAGES</c> or <c>~/.nuget/packages</c>).</param>
    public static List<string> Find(HashSet<string> names, IEnumerable<string> outputs, string? packagesRoot = null)
    {
        packagesRoot ??= Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var packaged = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var copies = new List<string>();
        foreach (var output in outputs)
        {
            if (!Directory.Exists(output))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(output, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (!names.Contains(name))
                {
                    continue;
                }

                if (!packaged.TryGetValue(name, out var packageIds))
                {
                    packageIds = PackageModuleIds(packagesRoot, name);
                    packaged[name] = packageIds;
                }

                if (packageIds.Count > 0 && ModuleId(file) is { } id && packageIds.Contains(id))
                {
                    continue;
                }

                copies.Add(file);
            }
        }

        return copies;
    }

    /// <summary>
    /// Module ids of the files named <paramref name="fileName"/> in the NuGet package whose id is the
    /// assembly name, any version. Empty when there is no such package: the copy is then injected, as
    /// before, which can only add failures to look at.
    /// </summary>
    /// <param name="packagesRoot">NuGet global packages folder.</param>
    /// <param name="fileName">Assembly file name, such as <c>Carter.dll</c>.</param>
    private static HashSet<Guid> PackageModuleIds(string packagesRoot, string fileName)
    {
        var ids = new HashSet<Guid>();
        var package = Path.Combine(packagesRoot, Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant());
        if (!Directory.Exists(package))
        {
            return ids;
        }

        foreach (var file in Directory.EnumerateFiles(package, fileName, SearchOption.AllDirectories))
        {
            if (ModuleId(file) is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>The module version id of an assembly, unique per build; null when the file cannot be read as one.</summary>
    /// <param name="path">Assembly file.</param>
    private static Guid? ModuleId(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!reader.HasMetadata)
            {
                return null;
            }

            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
