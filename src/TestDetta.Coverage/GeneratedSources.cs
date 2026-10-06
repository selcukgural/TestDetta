using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace TestDetta.Coverage;

/// <summary>
/// Reads the sources that source generators added to a compilation. The compiler embeds every
/// document without a file on disk in the portable PDB, next to the assembly or inside it.
/// </summary>
internal static class GeneratedSources
{
    /// <summary>Kind of the custom debug information that holds an embedded source.</summary>
    private static readonly Guid EmbeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    /// <summary>
    /// The generated documents of <paramref name="assemblyPath"/>: embedded sources whose path is not a
    /// file on disk, with that path. Empty when the assembly has no portable PDB.
    /// </summary>
    /// <param name="assemblyPath">Compiled assembly.</param>
    public static List<(string Name, string Text)> Read(string assemblyPath)
    {
        var texts = new List<(string Name, string Text)>();
        try
        {
            using var pe = new PEReader(File.OpenRead(assemblyPath));
            using var provider = OpenPdb(pe, assemblyPath);
            if (provider is null)
            {
                return texts;
            }

            var reader = provider.GetMetadataReader();
            foreach (var handle in reader.Documents)
            {
                var name = reader.GetString(reader.GetDocument(handle).Name);
                if (File.Exists(name))
                {
                    continue;
                }

                foreach (var informationHandle in reader.GetCustomDebugInformation(handle))
                {
                    var information = reader.GetCustomDebugInformation(informationHandle);
                    if (reader.GetGuid(information.Kind) == EmbeddedSource)
                    {
                        texts.Add((name, Decode(reader.GetBlobBytes(information.Value))));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidDataException)
        {
            // No readable PDB: generated code simply stays unknown, as it was before.
        }

        return texts;
    }

    /// <summary>
    /// What a build fed the compiler of <paramref name="assemblyPath"/> from outside the files git
    /// tracks (docs/design/runtime-file-reads.md): by file name, the checksums the PDB records of the
    /// compiled documents that are not tracked files, and by <c>resource:</c> and name, the SHA-256 of
    /// each embedded resource. Files the SDK writes from project properties (assembly info and
    /// attributes, global usings) are left out: they carry the commit and change with the project file,
    /// which runs the project anyway. Empty when the assembly has no portable PDB or cannot be read.
    /// </summary>
    /// <param name="assemblyPath">Compiled assembly.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="tracked">Files git tracks, relative to <paramref name="repoRoot"/> with '/' separators.</param>
    public static SortedDictionary<string, string> ReadBuildInputs(string assemblyPath, string repoRoot, IReadOnlySet<string> tracked)
    {
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var pe = new PEReader(File.OpenRead(assemblyPath));
            using (var provider = OpenPdb(pe, assemblyPath))
            {
                if (provider is null)
                {
                    return inputs;
                }

                var reader = provider.GetMetadataReader();
                // Compilers write resolved paths (macOS's /var is /private/var): either form of the root counts.
                string[] roots = [Path.GetFullPath(repoRoot), TestDetta.Core.Paths.ResolveLinks(repoRoot)];
                var byName = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var handle in reader.Documents)
                {
                    var document = reader.GetDocument(handle);
                    var name = reader.GetString(document.Name);
                    var file = name.Replace('\\', '/');
                    var fileName = file[(file.LastIndexOf('/') + 1)..];
                    if (SdkGenerated(fileName))
                    {
                        continue;
                    }

                    if (roots.Any(root => Path.GetRelativePath(root, name).Replace('\\', '/') is var relative
                                          && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && tracked.Contains(relative)))
                    {
                        continue;
                    }

                    if (!byName.TryGetValue(fileName, out var hashes))
                    {
                        byName[fileName] = hashes = [];
                    }

                    hashes.Add(Convert.ToHexString(reader.GetBlobBytes(document.Hash)));
                }

                foreach (var (fileName, hashes) in byName)
                {
                    hashes.Sort(StringComparer.Ordinal);
                    inputs[fileName] = string.Join(',', hashes);
                }
            }

            var metadata = pe.GetMetadataReader();
            var resources = pe.PEHeaders.CorHeader?.ResourcesDirectory.RelativeVirtualAddress is { } rva and > 0
                ? pe.GetSectionData(rva)
                : default;
            foreach (var handle in metadata.ManifestResources)
            {
                var resource = metadata.GetManifestResource(handle);
                if (!resource.Implementation.IsNil || resources.Length == 0)
                {
                    continue;
                }

                var blob = resources.GetReader((int)resource.Offset, resources.Length - (int)resource.Offset);
                var length = blob.ReadInt32();
                inputs["resource:" + metadata.GetString(resource.Name)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob.ReadBytes(length)));
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidDataException or InvalidOperationException)
        {
            inputs.Clear();
        }

        return inputs;

        static bool SdkGenerated(string fileName) =>
            fileName.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="assemblyPath"/> has a portable PDB, embedded or next to it, to read generated code from.</summary>
    /// <param name="assemblyPath">Compiled assembly.</param>
    public static bool HasPdb(string assemblyPath)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(assemblyPath));
            using var provider = OpenPdb(pe, assemblyPath);
            return provider is not null && provider.GetMetadataReader() is not null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>The embedded PDB, or the <c>.pdb</c> file next to the assembly; null when neither is portable.</summary>
    /// <param name="pe">Opened assembly.</param>
    /// <param name="assemblyPath">Assembly file.</param>
    internal static MetadataReaderProvider? OpenPdb(PEReader pe, string assemblyPath)
    {
        foreach (var entry in pe.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            {
                return pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
            }
        }

        var pdb = Path.ChangeExtension(assemblyPath, ".pdb");
        return File.Exists(pdb) ? MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb)) : null;
    }

    /// <summary>
    /// An embedded source blob: a 32-bit uncompressed size, then the text, deflated unless the size is 0.
    /// </summary>
    /// <param name="blob">Blob of the custom debug information.</param>
    private static string Decode(byte[] blob)
    {
        var size = BitConverter.ToInt32(blob, 0);
        if (size == 0)
        {
            return Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
        }

        using var inflated = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
        using var text = new StreamReader(inflated, Encoding.UTF8);
        return text.ReadToEnd();
    }
}
