using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Eval;

/// <summary>
/// Picks data files for <see cref="MutantKind.EmptyFile"/> changes (docs/design/runtime-file-reads.md):
/// tracked text files outside every project folder, which TestDetta selects only through the file reads
/// its maps recorded. Most come from files some map recorded a read of, where a failure can prove
/// something; the rest from files no map names, where a failing test is a read TestDetta never saw.
/// </summary>
internal static class DataFileSampler
{
    /// <summary>Largest file changed; larger ones are rarely test data.</summary>
    private const long MaxBytes = 256 * 1024;

    /// <summary>Picks up to <paramref name="count"/> files.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="maps">Coverage maps at the commit.</param>
    /// <param name="count">Files to pick.</param>
    /// <param name="seed">Seed of the shuffle.</param>
    public static List<SyntheticTarget> Sample(ProjectGraph graph, string repoRoot, IEnumerable<CoverageMap> maps, int count, int seed)
    {
        if (count <= 0)
        {
            return [];
        }

        var reads = maps.Select(static m => m.FileReads).OfType<FileReads>()
            .SelectMany(static r => r.Classes.Values.Concat(r.Fixtures.Values).Append(r.Ambient).Append(r.Initialization))
            .SelectMany(static p => p)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = new GitClient(NullLogger<GitClient>.Instance).TrackedFiles(repoRoot)
            .Where(f => !f.StartsWith(".git", StringComparison.Ordinal) && !f.StartsWith(".testdetta/", StringComparison.Ordinal)
                        && !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        && IsData(repoRoot, f)
                        && graph.ResolveFile(Paths.Normalize(Path.Combine(repoRoot, f))).Count == 0
                        && IsSmallText(Path.Combine(repoRoot, f)))
            .Order(StringComparer.Ordinal)
            .ToList();

        bool Read(string file) => reads.Contains(file) || reads.Any(r => r.EndsWith('/') && file.StartsWith(r, StringComparison.Ordinal));
        var random = new Random(seed);
        var read = candidates.Where(Read).OrderBy(_ => random.Next()).ToList();
        var unread = candidates.Where(f => !Read(f)).OrderBy(_ => random.Next()).ToList();
        var fromRead = Math.Min(read.Count, (int)Math.Ceiling(count * 0.8));
        var picked = read.Take(fromRead).Concat(unread.Take(count - fromRead)).ToList();
        picked.AddRange(read.Skip(fromRead).Take(count - picked.Count));
        return [.. picked.Select(f => Target(repoRoot, f, Read(f)))];
    }

    /// <summary>The change that empties <paramref name="file"/>: one edit that replaces its whole text.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="file">Repository-relative path.</param>
    /// <param name="read">True when some map recorded a read of it.</param>
    private static SyntheticTarget Target(string repoRoot, string file, bool read)
    {
        var path = Paths.Normalize(Path.Combine(repoRoot, file));
        return new SyntheticTarget(new CoveredMethod(DataFile.TypeName, file), [new SourceEdit(path, 0, string.Empty) { Replaces = File.ReadAllText(path).Length }], read)
        {
            Kind = MutantKind.EmptyFile,
        };
    }

    /// <summary>Extensions of files a project may compile or import: a change to one is decided as a build input, not a read.</summary>
    private static readonly string[] BuildExtensions = [".cs", ".fs", ".vb", ".props", ".targets", ".projitems", ".csproj", ".fsproj", ".vbproj"];

    /// <summary>
    /// True for a file whose change the analysis leaves to file reads: not a directory-wide build file
    /// (<c>global.json</c>, <c>Directory.Build.props</c>, <c>.editorconfig</c>, which run every project
    /// below them) and not a source or build file.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="file">Repository-relative path.</param>
    private static bool IsData(string repoRoot, string file) =>
        !BuildFiles.IsDirectoryWide(Paths.Normalize(Path.Combine(repoRoot, file)))
        && !Array.Exists(BuildExtensions, e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a non-empty file of at most <see cref="MaxBytes"/> with no NUL byte in its start.</summary>
    /// <param name="path">Full path.</param>
    private static bool IsSmallText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes)
            {
                return false;
            }

            using var stream = info.OpenRead();
            var buffer = new byte[(int)Math.Min(8192, info.Length)];
            var length = stream.Read(buffer);
            return Array.IndexOf(buffer, (byte)0, 0, length) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// A data file emptied in a worker's copy of the clone for the runs of one <see cref="MutantKind.EmptyFile"/>
/// change; lifting it for a control run writes the original back for a while.
/// </summary>
/// <param name="path">The file in the worker's copy.</param>
internal sealed class DataFile(string path) : IDisposable
{
    /// <summary>The type name a data file change carries in place of a member's; its member name is the file.</summary>
    public const string TypeName = "(file)";

    private readonly byte[] _original = File.ReadAllBytes(path);

    /// <summary>Empties the file.</summary>
    public DataFile Apply()
    {
        File.WriteAllBytes(path, []);
        return this;
    }

    /// <summary>Writes the original back until the result is disposed.</summary>
    public IDisposable Lift()
    {
        File.WriteAllBytes(path, _original);
        return new Relift(this);
    }

    /// <summary>Writes the original back.</summary>
    public void Dispose() => File.WriteAllBytes(path, _original);

    /// <summary>Empties the file again when a lift ends.</summary>
    /// <param name="file">The file.</param>
    private sealed class Relift(DataFile file) : IDisposable
    {
        /// <summary>Empties the file again.</summary>
        public void Dispose() => file.Apply();
    }
}
