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
    /// Texts of the generated documents of <paramref name="assemblyPath"/>: embedded sources whose
    /// path is not a file on disk. Empty when the assembly has no portable PDB.
    /// </summary>
    /// <param name="assemblyPath">Compiled assembly.</param>
    public static List<string> Read(string assemblyPath)
    {
        var texts = new List<string>();
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
                if (File.Exists(reader.GetString(reader.GetDocument(handle).Name)))
                {
                    continue;
                }

                foreach (var informationHandle in reader.GetCustomDebugInformation(handle))
                {
                    var information = reader.GetCustomDebugInformation(informationHandle);
                    if (reader.GetGuid(information.Kind) == EmbeddedSource)
                    {
                        texts.Add(Decode(reader.GetBlobBytes(information.Value)));
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
