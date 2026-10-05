using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Eval;

/// <summary>
/// What source generators write for a synthetic change, for TestDetta's selection to compare with the
/// maps as <c>td test</c> would (docs/design/generator-output-changes.md). The change is written into a
/// copy of the clone, which is built, so the clone's own binaries, which the mutant runs use, never hold
/// it. A read is stored with the test outcomes: it depends only on the commit, the change and the
/// toolchain, so a later round reads it instead of building again.
/// </summary>
/// <param name="workspace">The evaluation's clone.</param>
/// <param name="cacheDirectory">Where reads are kept across rounds, or null to keep none.</param>
internal sealed partial class GeneratorWorkspace(EvalWorkspace workspace, string? cacheDirectory)
{
    /// <summary>Serializer settings for stored reads.</summary>
    private static readonly JsonSerializerOptions Options = new();

    private readonly ILogger _logger = workspace.LoggerFactory.CreateLogger<GeneratorWorkspace>();

    /// <summary>The copy's project graph, loaded with the copy.</summary>
    private ProjectGraph? _graph;

    /// <summary>The copy of the clone that changes are built in.</summary>
    private string Root => workspace.RepoRoot + ".generators";

    /// <summary>A reader for the selection of one change, which is in the clone's sources while the selection runs.</summary>
    /// <param name="target">The change.</param>
    public IGeneratedCodeReader For(SyntheticTarget target) => new Reader(this, target);

    /// <summary>Deletes the copy, when one was made; the evaluation calls it when it ends.</summary>
    public void Remove()
    {
        if (_graph is not null)
        {
            WorkerCopies.Remove(Root);
        }
    }

    /// <summary>The stored read of the change, or builds it in the copy and reads what the generators wrote.</summary>
    /// <param name="target">The change, written in the clone's sources now.</param>
    /// <param name="testProject">Test project of the clone whose build is read.</param>
    /// <param name="framework">Framework its map was recorded for, or null.</param>
    private GeneratedCodeRead Read(SyntheticTarget target, ProjectNode testProject, string? framework)
    {
        var relative = Path.GetRelativePath(workspace.RepoRoot, testProject.Path).Replace('\\', '/');
        var key = $"{target.Member.TypeName}|{target.Member.MemberName}|{target.Kind}|{relative}|{framework}";
        var file = cacheDirectory is null ? null : Path.Combine(cacheDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json.gz");
        // A stored failed build is from before such edits fell back to the unedited build.
        if (file is not null && Load(file) is { BuildErrors: null } stored)
        {
            LogReused(relative, target.Member.TypeName, target.Member.MemberName);
            return stored;
        }

        var graph = Copy();
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        GeneratedCodeRead read;
        try
        {
            foreach (var path in target.Edits.Select(static e => e.Path).Distinct(StringComparer.Ordinal))
            {
                var inCopy = Path.Combine(Root, Path.GetRelativePath(workspace.RepoRoot, path));
                originals[inCopy] = File.ReadAllBytes(inCopy);
                File.WriteAllBytes(inCopy, File.ReadAllBytes(path));
            }

            LogBuilding(relative, target.Member.TypeName, target.Member.MemberName);
            read = Build(graph, relative, framework);
        }
        finally
        {
            foreach (var (path, bytes) in originals)
            {
                File.WriteAllBytes(path, bytes);
            }
        }

        // The edit is made to be seen, not to compile: an empty statement a strict repository rejects,
        // or parentheses around a call that is a statement. It keeps behaviour, and no generator runs the
        // mutant, so the generators write what they write without it.
        if (read.BuildErrors is { } errors)
        {
            var firstError = errors.Split('\n')[0];
            LogEditDoesNotCompile(relative, target.Member.TypeName, target.Member.MemberName, firstError);
            read = Unedited(graph, relative, framework);
        }

        if (file is not null)
        {
            Save(file, read);
        }

        return read;
    }

    /// <summary>Reads of the unedited copy, by test project and framework; built at most once each.</summary>
    private readonly Dictionary<string, GeneratedCodeRead> _unedited = new(StringComparer.Ordinal);

    /// <summary>What the generators write without any change, for changes whose edit does not compile.</summary>
    /// <param name="graph">The copy's graph.</param>
    /// <param name="relative">Test project, relative to the repository.</param>
    /// <param name="framework">Framework, or null.</param>
    private GeneratedCodeRead Unedited(ProjectGraph graph, string relative, string? framework)
    {
        var key = $"{relative}|{framework}";
        if (!_unedited.TryGetValue(key, out var read))
        {
            read = Build(graph, relative, framework);
            _unedited[key] = read;
        }

        return read;
    }

    /// <summary>Builds a test project of the copy and reads its generated code.</summary>
    /// <param name="graph">The copy's graph.</param>
    /// <param name="relative">Test project, relative to the repository.</param>
    /// <param name="framework">Framework, or null.</param>
    private GeneratedCodeRead Build(ProjectGraph graph, string relative, string? framework) =>
        new BuildGeneratedCodeReader(workspace.LoggerFactory, new GeneratedCodeBuild(Build: true))
            .Read(graph, Root, graph[Paths.Normalize(Path.Combine(Root, relative))], framework);

    /// <summary>Makes the copy and loads its graph, once per evaluation.</summary>
    private ProjectGraph Copy()
    {
        if (_graph is { } graph)
        {
            return graph;
        }

        WorkerCopies.Remove(Root);
        WorkerCopies.Copy(workspace.RepoRoot, Root);
        _graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>(), workspace.Evaluator).Load(Root);
        return _graph;
    }

    /// <summary>A stored read; null when there is none or it cannot be read.</summary>
    /// <param name="file">Its file.</param>
    private static GeneratedCodeRead? Load(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            return JsonSerializer.Deserialize<GeneratedCodeRead>(gzip, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Stores a read atomically.</summary>
    /// <param name="file">Its file.</param>
    /// <param name="read">The read.</param>
    private static void Save(string file, GeneratedCodeRead read)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        using (var gzip = new GZipStream(File.Create(temporary), CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(gzip, read, Options);
        }

        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>The <see cref="IGeneratedCodeReader"/> of one change; the graph and root it is given are the clone's.</summary>
    /// <param name="owner">The workspace.</param>
    /// <param name="target">The change.</param>
    private sealed class Reader(GeneratorWorkspace owner, SyntheticTarget target) : IGeneratedCodeReader
    {
        /// <inheritdoc />
        public GeneratedCodeRead Read(ProjectGraph graph, string repoRoot, ProjectNode testProject, string? framework) =>
            owner.Read(target, testProject, framework);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{TestProject}: building the change to {TypeName}.{MemberName} in the generator copy to read what its source generators write")]
    private partial void LogBuilding(string testProject, string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{TestProject}: the edit of {TypeName}.{MemberName} does not compile ({Error}); its generated code is read from the unedited build, which the edit does not change")]
    private partial void LogEditDoesNotCompile(string testProject, string typeName, string memberName, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TestProject}: generated code for the change to {TypeName}.{MemberName} reused from the cache")]
    private partial void LogReused(string testProject, string typeName, string memberName);
}
