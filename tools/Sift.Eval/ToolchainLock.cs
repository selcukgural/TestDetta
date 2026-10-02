using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sift.Eval;

/// <summary>
/// URL and SHA-512 of every toolchain archive the corpus uses, committed with the repository like
/// <c>packages.lock.json</c>: an archive is installed only when its hash matches. Missing entries
/// are added from Microsoft's release metadata, except in locked mode (CI), where they fail the run.
/// </summary>
/// <param name="path">Lock file; it need not exist yet.</param>
/// <param name="locked">True when entries may not be added (CI).</param>
/// <exception cref="InvalidDataException">The file is not a lock file.</exception>
internal sealed class ToolchainLock(string path, bool locked)
{
    /// <summary>Serializer settings of the file: indented, keys in a stable order.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Entries by <see cref="Key"/>, sorted so the file diffs well.</summary>
    private readonly SortedDictionary<string, LockedArchive> _archives = new(Read(path), StringComparer.Ordinal);

    /// <summary>Guards the entries and the file.</summary>
    private readonly Lock _gate = new();

    /// <summary>The lock file.</summary>
    public string Path { get; } = path;

    /// <summary>True when entries may not be added; set from <c>CI=true</c>.</summary>
    public bool Locked { get; } = locked;

    /// <summary>Key of an archive, such as <c>sdk/10.0.401/osx-arm64</c>.</summary>
    /// <param name="kind">Kind of archive.</param>
    /// <param name="version">Its version.</param>
    /// <param name="rid">Its runtime identifier.</param>
    public static string Key(ArchiveKind kind, string version, string rid) =>
        $"{(kind == ArchiveKind.Sdk ? "sdk" : "aspnetcore-runtime")}/{version}/{rid}";

    /// <summary>The locked archive, or null.</summary>
    /// <param name="key">See <see cref="Key"/>.</param>
    public LockedArchive? Find(string key)
    {
        lock (_gate)
        {
            return _archives.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Adds entries and writes the file, merged with what another evaluation may have added to it
    /// meanwhile; the file is replaced atomically.
    /// </summary>
    /// <param name="entries">Archives by key.</param>
    /// <exception cref="InvalidOperationException">The lock is in locked mode.</exception>
    public void Add(IReadOnlyDictionary<string, LockedArchive> entries)
    {
        if (Locked)
        {
            throw new InvalidOperationException($"{Path} is read-only with CI=true.");
        }

        lock (_gate)
        {
            foreach (var (key, archive) in Read(Path))
            {
                _archives.TryAdd(key, archive);
            }

            foreach (var (key, archive) in entries)
            {
                _archives[key] = archive;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new LockFile { Archives = new(_archives, StringComparer.Ordinal) }, Options) + "\n");
            File.Move(temporary, Path, overwrite: true);
        }
    }

    /// <summary>Entries of a lock file; none when it does not exist.</summary>
    /// <param name="path">Lock file.</param>
    /// <exception cref="InvalidDataException">The file is not a lock file.</exception>
    private static Dictionary<string, LockedArchive> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), Options)?.Archives ?? []
                : [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a toolchain lock file: {ex.Message}", ex);
        }
    }

    /// <summary>Contents of the lock file.</summary>
    private sealed class LockFile
    {
        /// <summary>Format version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Archives by key.</summary>
        public Dictionary<string, LockedArchive> Archives { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>Kinds of toolchain archive.</summary>
internal enum ArchiveKind
{
    /// <summary>A .NET SDK, with its host and runtimes.</summary>
    Sdk,

    /// <summary>An ASP.NET Core runtime, with the .NET runtime of the same version.</summary>
    AspNetCoreRuntime,
}

/// <summary>Where an archive is downloaded from and its expected hash.</summary>
/// <param name="Url">Download URL.</param>
/// <param name="Sha512">Lower-case hex SHA-512 of the archive.</param>
internal sealed record LockedArchive(string Url, string Sha512);
