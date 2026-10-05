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
/// it; a change to a generator is read with its mutant in the generator (docs/design/generator-mutants.md).
/// A read is stored with the test outcomes: it depends only on the commit, the change and the
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
    /// <param name="originals">The clone's edited files as they are without the change, by path.</param>
    public IGeneratedCodeReader For(SyntheticTarget target, IReadOnlyDictionary<string, byte[]> originals) => new Reader(this, target, originals);

    /// <summary>Deletes the copy, when one was made; the evaluation calls it when it ends.</summary>
    public void Remove()
    {
        if (_graph is not null)
        {
            WorkerCopies.Remove(Root);
            WorkerCopies.Remove(Root + ".mutant-backup");
        }
    }

    /// <summary>The stored read of the change, or builds it in the copy and reads what the generators wrote.</summary>
    /// <param name="target">The change, written in the clone's sources now.</param>
    /// <param name="cloneOriginals">The clone's edited files without the change, by path.</param>
    /// <param name="testProject">Test project of the clone whose build is read.</param>
    /// <param name="framework">Framework its map was recorded for, or null.</param>
    private GeneratedCodeRead Read(SyntheticTarget target, IReadOnlyDictionary<string, byte[]> cloneOriginals, ProjectNode testProject, string? framework)
    {
        var relative = Path.GetRelativePath(workspace.RepoRoot, testProject.Path).Replace('\\', '/');
        // "3": reads stored before the generators ran the mutant (docs/design/generator-mutants.md).
        var key = $"3|{target.Member.TypeName}|{target.Member.MemberName}|{target.Kind}|{relative}|{framework}";
        var file = cacheDirectory is null ? null : Path.Combine(cacheDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json.gz");
        if (file is not null && Load(file) is { } stored)
        {
            LogReused(relative, target.Member.TypeName, target.Member.MemberName);
            return stored;
        }

        var graph = Copy(cloneOriginals);
        var generators = new GeneratorMutants(graph).Generators(target.Edits.Select(e => Path.Combine(Root, Path.GetRelativePath(workspace.RepoRoot, e.Path))));
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        GeneratedCodeRead read;
        bool editCompiles;
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
            editCompiles = read.BuildErrors is null;
            if (editCompiles)
            {
                read = WithMutant(target, generators, graph, relative, framework) ?? read;
            }
        }
        finally
        {
            foreach (var (path, bytes) in originals)
            {
                File.WriteAllBytes(path, bytes);
            }
        }

        // The edit is made to be seen, not to compile: an empty statement a strict repository rejects,
        // or parentheses around a call that is a statement. It keeps behaviour, so the unedited build
        // stands in for it; the mutant goes into the generators' assemblies after that build instead.
        if (!editCompiles)
        {
            var firstError = read.BuildErrors!.Split('\n')[0];
            LogEditDoesNotCompile(relative, target.Member.TypeName, target.Member.MemberName, firstError);
            read = Unedited(graph, relative, framework);
            if (generators.Count > 0 && read.BuildErrors is null)
            {
                // The unedited sources were written back after the last build: the generator builds again first.
                read = Build(graph, relative, framework);
                read = (read.BuildErrors is null ? WithMutant(target, generators, graph, relative, framework) : null) ?? read;
            }
        }

        if (file is not null)
        {
            Save(file, read);
        }

        return read;
    }

    /// <summary>The built assemblies of each generator project of the copy, found once they exist.</summary>
    private readonly Dictionary<string, List<string>> _generatorAssemblies = new(StringComparer.Ordinal);

    /// <summary>
    /// What the generators write with the change's mutant in them, as <c>td test</c> would read it after
    /// the real change: the mutant is written alone into each generator's built assemblies of the copy,
    /// which is up to date, the test project builds again (its consumers compile again with the
    /// generator, without a compiler server), and the assemblies are put back. Null when the change is
    /// in no generator, or the mutant is not found in its assemblies.
    /// </summary>
    /// <param name="target">The change.</param>
    /// <param name="generators">Generator projects of the copy whose sources the change edits.</param>
    /// <param name="graph">The copy's graph.</param>
    /// <param name="relative">Test project, relative to the repository.</param>
    /// <param name="framework">Framework, or null.</param>
    private GeneratedCodeRead? WithMutant(SyntheticTarget target, List<ProjectNode> generators, ProjectGraph graph, string relative, string? framework)
    {
        var assemblies = new List<string>();
        foreach (var generator in generators)
        {
            if (!_generatorAssemblies.TryGetValue(generator.Path, out var found))
            {
                found = GeneratorMutants.Assemblies(generator, _logger);
                if (found.Count > 0)
                {
                    _generatorAssemblies[generator.Path] = found;
                }
            }

            assemblies.AddRange(found);
        }

        if (assemblies.Count == 0)
        {
            return null;
        }

        var mutation = MutationInjector.Inject(assemblies, target.Member, Root + ".mutant-backup", target.Kind);
        try
        {
            if (mutation.MutatedMethods == 0)
            {
                return null;
            }

            GeneratorMutants.Touch(assemblies);
            LogBuildingMutant(relative, target.Member.TypeName, target.Member.MemberName, target.Kind);
            return Build(graph, relative, framework, GeneratorMutants.NoCompilerServer);
        }
        finally
        {
            mutation.Dispose();

            // Newer than every consumer's output, so the next build compiles them again without the mutant.
            GeneratorMutants.Touch(assemblies);
        }
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
    /// <param name="arguments">More <c>dotnet build</c> arguments.</param>
    private GeneratedCodeRead Build(ProjectGraph graph, string relative, string? framework, IReadOnlyList<string>? arguments = null) =>
        new BuildGeneratedCodeReader(workspace.LoggerFactory, new GeneratedCodeBuild(Build: true) { Arguments = arguments ?? [] })
            .Read(graph, Root, graph[Paths.Normalize(Path.Combine(Root, relative))], framework);

    /// <summary>Makes the copy and loads its graph, once per evaluation.</summary>
    /// <param name="cloneOriginals">
    /// The clone's files the current change edits, without it: the copy is made while the clone holds
    /// the change, and keeping it would put it into every later build of the copy, the unedited one too.
    /// </param>
    private ProjectGraph Copy(IReadOnlyDictionary<string, byte[]> cloneOriginals)
    {
        if (_graph is { } graph)
        {
            return graph;
        }

        WorkerCopies.Remove(Root);
        WorkerCopies.Copy(workspace.RepoRoot, Root);
        foreach (var (path, bytes) in cloneOriginals)
        {
            File.WriteAllBytes(Path.Combine(Root, Path.GetRelativePath(workspace.RepoRoot, path)), bytes);
        }

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
    /// <param name="originals">The clone's edited files without the change.</param>
    private sealed class Reader(GeneratorWorkspace owner, SyntheticTarget target, IReadOnlyDictionary<string, byte[]> originals) : IGeneratedCodeReader
    {
        /// <inheritdoc />
        public GeneratedCodeRead Read(ProjectGraph graph, string repoRoot, ProjectNode testProject, string? framework) =>
            owner.Read(target, originals, testProject, framework);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{TestProject}: building the change to {TypeName}.{MemberName} in the generator copy to read what its source generators write")]
    private partial void LogBuilding(string testProject, string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{TestProject}: the edit of {TypeName}.{MemberName} does not compile ({Error}); its generated code is read from the unedited build, which the edit does not change")]
    private partial void LogEditDoesNotCompile(string testProject, string typeName, string memberName, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "{TestProject}: building again with the {Kind} mutant of {TypeName}.{MemberName} in the generator, to read what it writes then")]
    private partial void LogBuildingMutant(string testProject, string typeName, string memberName, MutantKind kind);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TestProject}: generated code for the change to {TypeName}.{MemberName} reused from the cache")]
    private partial void LogReused(string testProject, string typeName, string memberName);
}
