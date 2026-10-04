using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Reads where each class of a compiled test assembly is written, from its portable PDB: per source
/// file, the first line of the class's code. Selection uses it to recognise a class the build names
/// differently from the recording by its place (docs/design/unrecorded-classes.md).
/// </summary>
internal static class ClassLocationReader
{
    /// <summary>
    /// Prefix of the document paths a deterministic build writes (<c>ContinuousIntegrationBuild</c>):
    /// the repository root is mapped to it.
    /// </summary>
    private const string DeterministicRoot = "/_/";

    /// <summary>
    /// Locations of every class with code in <paramref name="assemblyPath"/>, by runtime name (nested
    /// classes joined with '+'). Code of compiler-generated nested types (lambdas, iterators, async
    /// state machines) counts for the class that declares it: a class whose tests are all async has
    /// its lines there. Files outside the repository are left out. Empty when there is no readable
    /// portable PDB.
    /// </summary>
    /// <param name="assemblyPath">Compiled assembly, read before instrumentation rewrites it.</param>
    /// <param name="repoRoot">Repository root; locations are relative to it.</param>
    public static Dictionary<string, IReadOnlyList<ClassLocation>> Read(string assemblyPath, string repoRoot)
    {
        var locations = new Dictionary<string, IReadOnlyList<ClassLocation>>(StringComparer.Ordinal);
        try
        {
            using var pe = new PEReader(File.OpenRead(assemblyPath));
            using var provider = GeneratedSources.OpenPdb(pe, assemblyPath);
            if (provider is null)
            {
                return locations;
            }

            var metadata = pe.GetMetadataReader();
            var pdb = provider.GetMetadataReader();
            string[] roots = [Paths.Normalize(repoRoot), Paths.ResolveLinks(repoRoot)];
            var firstLines = new Dictionary<(string Class, string File), int>();
            var files = new Dictionary<DocumentHandle, string?>();
            foreach (var handle in metadata.MethodDefinitions)
            {
                var owner = OwnerName(metadata, metadata.GetMethodDefinition(handle).GetDeclaringType());
                if (owner is null)
                {
                    continue;
                }

                foreach (var point in pdb.GetMethodDebugInformation(handle.ToDebugInformationHandle()).GetSequencePoints())
                {
                    if (point.IsHidden)
                    {
                        continue;
                    }

                    if (!files.TryGetValue(point.Document, out var file))
                    {
                        file = Relative(pdb.GetString(pdb.GetDocument(point.Document).Name), roots);
                        files[point.Document] = file;
                    }

                    if (file is null)
                    {
                        continue;
                    }

                    var key = (owner, file);
                    firstLines[key] = firstLines.TryGetValue(key, out var line) ? Math.Min(line, point.StartLine) : point.StartLine;
                }
            }

            foreach (var group in firstLines.GroupBy(static l => l.Key.Class, StringComparer.Ordinal))
            {
                locations[group.Key] = [.. group.Select(static l => new ClassLocation(l.Key.File, l.Value)).OrderBy(static l => l.File, StringComparer.Ordinal)];
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidDataException or InvalidOperationException)
        {
            // No readable PDB: classes are known by name only, as before.
        }

        return locations;
    }

    /// <summary>
    /// Runtime name of the class whose code a method of <paramref name="handle"/> is: the type itself,
    /// or for a compiler-generated type (its name starts with '&lt;') the nearest declaring type that
    /// is not. Null for code outside any written class (<c>&lt;Module&gt;</c>,
    /// <c>&lt;PrivateImplementationDetails&gt;</c>).
    /// </summary>
    /// <param name="metadata">Assembly metadata.</param>
    /// <param name="handle">Declaring type of a method.</param>
    private static string? OwnerName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        while (metadata.GetString(type.Name).StartsWith('<'))
        {
            var declaring = type.GetDeclaringType();
            if (declaring.IsNil)
            {
                return null;
            }

            type = metadata.GetTypeDefinition(declaring);
        }

        var name = metadata.GetString(type.Name);
        var parent = type.GetDeclaringType();
        if (!parent.IsNil)
        {
            return OwnerName(metadata, parent) + "+" + name;
        }

        var ns = metadata.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>
    /// <paramref name="document"/> relative to the repository root with '/' separators; a deterministic
    /// path (<c>/_/src/A.cs</c>) is relative already. Null for a file outside the repository.
    /// </summary>
    /// <param name="document">Document path from the PDB.</param>
    /// <param name="roots">
    /// The repository root as given and with its links resolved: the compiler writes the resolved
    /// path (<c>/private/var/...</c> for a repository under macOS's <c>/var</c>).
    /// </param>
    private static string? Relative(string document, string[] roots)
    {
        var path = document.Replace('\\', '/');
        if (path.StartsWith(DeterministicRoot, StringComparison.Ordinal))
        {
            return path[DeterministicRoot.Length..];
        }

        if (!Path.IsPathFullyQualified(document))
        {
            return null;
        }

        var normalized = Paths.Normalize(document);
        foreach (var root in roots)
        {
            if (Paths.IsUnder(normalized, root) && normalized.Length > root.Length)
            {
                return normalized[(root.Length + 1)..];
            }
        }

        return null;
    }
}
