using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Evaluates every project of a repository in one <c>dotnet msbuild</c> process: a generated driver
/// calls an injected target in each project, and in each target framework's inner build, that
/// returns its references, items, test flag and assembly name. Nothing is restored or built, and
/// nothing is written into the repository except the cache. Results are cached by everything that
/// can change them, so the many analyses of one commit evaluate once. See
/// docs/design/msbuild-evaluation.md.
/// </summary>
public sealed partial class MsBuildProjectEvaluator : IProjectEvaluator
{
    /// <summary>Version of the injected target and the output format; part of the cache key.</summary>
    private const int EvaluatorVersion = 3;

    /// <summary>Longest an evaluation may take; a large repository takes seconds.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Evaluations of this process by cache key.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, EvaluatedProject>> Memory = new(StringComparer.Ordinal);

    /// <summary>Directories that hold no MSBuild input: outputs, tools' state, TestDetta's own data.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".testdetta");

    /// <summary>Extensions of files whose contents MSBuild reads while evaluating.</summary>
    private static readonly FrozenSet<string> MsBuildExtensions =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".projitems", ".proj");

    /// <summary>Other files whose contents change an evaluation: the SDK choice and package sources.</summary>
    private static readonly FrozenSet<string> MsBuildFiles = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "global.json", "nuget.config");

    /// <summary>The target injected into every project, and the item types whose files it reports.</summary>
    private const string Targets = """
        <Project>
          <PropertyGroup>
            <!-- An inner build of an SDK project adds the implicit framework symbols to DefineConstants in a target. -->
            <_TestDettaDefineConstantsDependsOn Condition="'$(UsingMicrosoftNETSdk)' == 'true' and '$(TargetFramework)' != ''">AddImplicitDefineConstants</_TestDettaDefineConstantsDependsOn>
          </PropertyGroup>
          <Target Name="TestDettaEvaluate" Returns="@(TestDettaEvaluated)" DependsOnTargets="$(_TestDettaDefineConstantsDependsOn)">
            <ItemGroup>
              <_TestDettaFramework Include="$(TargetFrameworks)" Condition="'$(TargetFramework)' == ''" />
              <TestDettaEvaluated Include="$(MSBuildProjectFullPath)" Kind="Project" Project="$(MSBuildProjectFullPath)" IsTestProject="$(IsTestProject)" AssemblyName="$(AssemblyName)" />
            </ItemGroup>
            <ItemGroup Condition="'@(_TestDettaFramework)' == ''">
              <TestDettaEvaluated Include="@(ProjectReference->'%(FullPath)')" Kind="Reference" Project="$(MSBuildProjectFullPath)" />
              <!-- Referenced without compiling against it: an analyzer or source generator, or build order and copied outputs. -->
              <_TestDettaOpaqueReference Include="@(ProjectReference)" Condition="'%(ProjectReference.OutputItemType)' != '' or '%(ProjectReference.ReferenceOutputAssembly)' == 'false'" />
              <TestDettaEvaluated Include="@(_TestDettaOpaqueReference->'%(FullPath)')" Kind="OpaqueReference" Project="$(MSBuildProjectFullPath)" />
              <TestDettaEvaluated Include="@(PackageReference)" Kind="Package" Project="$(MSBuildProjectFullPath)" />
              <TestDettaEvaluated Include="@(Compile->'%(FullPath)');@(EmbeddedResource->'%(FullPath)');@(Content->'%(FullPath)');@(None->'%(FullPath)');@(AdditionalFiles->'%(FullPath)')" Kind="Item" Project="$(MSBuildProjectFullPath)" />
              <TestDettaEvaluated Include="$(DefineConstants)" Kind="Define" Project="$(MSBuildProjectFullPath)" Framework="$(TargetFramework)" />
              <TestDettaEvaluated Include="$(MSBuildProjectFullPath)" Kind="Framework" Project="$(MSBuildProjectFullPath)" Framework="$(TargetFramework)" />
            </ItemGroup>
            <MSBuild Condition="'@(_TestDettaFramework)' != ''" Projects="$(MSBuildProjectFullPath)" Targets="TestDettaEvaluate" Properties="TargetFramework=%(_TestDettaFramework.Identity)">
              <Output TaskParameter="TargetOutputs" ItemName="TestDettaEvaluated" />
            </MSBuild>
          </Target>
        </Project>
        """;

    /// <summary>Logger of this class.</summary>
    private readonly ILogger _logger;

    /// <summary>Runs dotnet.</summary>
    private readonly DotnetProcess _dotnet;

    /// <summary>True to keep evaluations in the repository's <c>.testdetta/cache/graph</c> as well as in memory.</summary>
    private readonly bool _diskCache;

    /// <summary>Creates the evaluator.</summary>
    /// <param name="loggerFactory">Creates its loggers.</param>
    /// <param name="diskCache">True to keep evaluations in <c>.testdetta/cache/graph</c> of the repository, next to the coverage maps.</param>
    public MsBuildProjectEvaluator(ILoggerFactory loggerFactory, bool diskCache = true)
    {
        _logger = loggerFactory.CreateLogger<MsBuildProjectEvaluator>();
        _dotnet = new DotnetProcess(_logger);
        _diskCache = diskCache;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, EvaluatedProject>? Evaluate(string repoRoot, IReadOnlyCollection<string> projects)
    {
        var key = Key(repoRoot);
        // In memory the paths are absolute, so the same contents in another clone are another entry.
        var memoryKey = key is null ? null : repoRoot + "\n" + key;
        if (memoryKey is not null && Memory.TryGetValue(memoryKey, out var remembered))
        {
            return remembered;
        }

        var cacheFile = key is not null && _diskCache ? Path.Combine(repoRoot, ".testdetta", "cache", "graph", key + ".json") : null;
        if (cacheFile is not null && ReadCache(cacheFile, repoRoot) is { } cached)
        {
            LogCacheHit(cacheFile);
            return Memory[memoryKey!] = cached;
        }

        var evaluated = Run(repoRoot, projects);
        if (evaluated is null || memoryKey is null)
        {
            return evaluated;
        }

        Memory[memoryKey] = evaluated;
        if (cacheFile is not null)
        {
            WriteCache(cacheFile, repoRoot, evaluated);
        }

        return evaluated;
    }

    /// <summary>
    /// Cache key of an evaluation: the evaluator version, the SDK that <c>global.json</c> selects, the
    /// contents of every MSBuild file, and the paths of all files, since globs depend on which files
    /// exist. Source contents are not part of it: an evaluation reads none. Null when the SDK version
    /// cannot be read, which disables caching but not the evaluation.
    /// </summary>
    /// <param name="repoRoot">Normalized repository root.</param>
    private string? Key(string repoRoot)
    {
        string sdk;
        try
        {
            sdk = _dotnet.Capture(["--version"], repoRoot).Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogNoSdkVersion(ex.Message);
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{EvaluatorVersion}\n{sdk}\n"));
        foreach (var file in Files(repoRoot).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repoRoot, file).Replace('\\', '/') + "\n"));
            if (MsBuildExtensions.Contains(Path.GetExtension(file)) || MsBuildFiles.Contains(Path.GetFileName(file)))
            {
                hash.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Every file of the repository outside <see cref="SkippedDirectories"/>.</summary>
    /// <param name="root">Repository root.</param>
    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)) && !new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>Runs the evaluation; null when the process failed as a whole.</summary>
    /// <param name="repoRoot">Normalized repository root.</param>
    /// <param name="projects">Project files to evaluate.</param>
    private Dictionary<string, EvaluatedProject>? Run(string repoRoot, IReadOnlyCollection<string> projects)
    {
        var scratch = Directory.CreateTempSubdirectory("testdetta-evaluate-").FullName;
        try
        {
            var targets = Path.Combine(scratch, "testdetta-evaluate.targets");
            var output = Path.Combine(scratch, "evaluated.txt");
            var driver = Path.Combine(scratch, "driver.proj");
            File.WriteAllText(targets, Targets);
            File.WriteAllText(driver, Driver(projects, targets, output));

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var log = new StringWriter();
            var exitCode = _dotnet.Run(["msbuild", driver, "-nologo", "-v:q", "-m", "-nodeReuse:false"], repoRoot, new Dictionary<string, string>(), log, Timeout);
            // A project that fails to evaluate (a missing import) fails the run but not the others,
            // since the driver goes on past it; it is simply missing from the output.
            if (!File.Exists(output))
            {
                var failure = log.ToString().Trim();
                LogFailed(exitCode, failure);
                return null;
            }

            if (exitCode != 0)
            {
                var failures = log.ToString().Trim();
                LogPartlyFailed(exitCode, failures);
            }

            var evaluated = Parse(File.ReadLines(output));
            var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            LogEvaluated(evaluated.Count, projects.Count, seconds);
            return evaluated;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The driver project: evaluates every project with the target injected through the hooks
    /// Microsoft.Common.targets and its cross-targeting counterpart import, and writes one
    /// tab-separated line per reported item. All projects are evaluated in one parallel call; when one
    /// fails, that call returns no outputs at all, so the projects are evaluated once more one by one,
    /// and only the failing ones are missing.
    /// </summary>
    /// <param name="projects">Project files.</param>
    /// <param name="targets">The injected targets file.</param>
    /// <param name="output">File the lines are written to.</param>
    internal static string Driver(IEnumerable<string> projects, string targets, string output)
    {
        var hooks = $"CustomAfterMicrosoftCommonTargets={targets};CustomAfterMicrosoftCommonCrossTargetingTargets={targets}";
        var document = new XDocument(new XElement("Project",
            new XElement("ItemGroup", projects.Select(static p => new XElement("TestDettaProject", new XAttribute("Include", Escape(p))))),
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("MSBuild",
                    new XAttribute("Projects", "@(TestDettaProject)"),
                    new XAttribute("Targets", "TestDettaEvaluate"),
                    new XAttribute("Properties", Escape(hooks)),
                    new XAttribute("BuildInParallel", "true"),
                    new XAttribute("ContinueOnError", "true"),
                    new XElement("Output", new XAttribute("TaskParameter", "TargetOutputs"), new XAttribute("ItemName", "TestDettaEvaluated"))),
                new XElement("PropertyGroup", new XElement("TestDettaAllEvaluated", "$(MSBuildLastTaskResult)")),
                new XElement("MSBuild",
                    new XAttribute("Condition", "'$(TestDettaAllEvaluated)' == 'false'"),
                    new XAttribute("Projects", "%(TestDettaProject.Identity)"),
                    new XAttribute("Targets", "TestDettaEvaluate"),
                    new XAttribute("Properties", Escape(hooks)),
                    new XAttribute("ContinueOnError", "true"),
                    new XElement("Output", new XAttribute("TaskParameter", "TargetOutputs"), new XAttribute("ItemName", "TestDettaEvaluated"))),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", Escape(output)),
                    new XAttribute("Lines", "@(TestDettaEvaluated->'%(Kind)%09%(Project)%09%(Identity)%09%(IsTestProject)%09%(AssemblyName)%09%(Framework)')"),
                    new XAttribute("Overwrite", "true")))));
        return document.ToString();
    }

    /// <summary>Escapes MSBuild's special characters in a literal path or property list.</summary>
    /// <param name="value">Raw text.</param>
    private static string Escape(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal).Replace("@", "%40", StringComparison.Ordinal)
            .Replace("$", "%24", StringComparison.Ordinal).Replace("'", "%27", StringComparison.Ordinal);

    /// <summary>Unites the reported lines per project; a project with no line was not evaluated.</summary>
    /// <param name="lines">Lines of the output file.</param>
    internal static Dictionary<string, EvaluatedProject> Parse(IEnumerable<string> lines)
    {
        var builders = new Dictionary<string, Builder>(Paths.Comparer);
        foreach (var line in lines)
        {
            var fields = line.Split('\t');
            if (fields.Length < 5 || fields[1].Length == 0 || fields[2].Length == 0)
            {
                continue;
            }

            var project = Paths.Normalize(fields[1]);
            if (!builders.TryGetValue(project, out var builder))
            {
                builder = new Builder();
                builders[project] = builder;
            }

            switch (fields[0])
            {
                case "Project":
                    builder.IsTestProject |= string.Equals(fields[3], "true", StringComparison.OrdinalIgnoreCase);
                    builder.AssemblyName ??= fields[4].Length > 0 ? fields[4] : null;
                    break;
                case "Reference":
                    builder.References.Add(Paths.Normalize(fields[2]));
                    break;
                case "OpaqueReference":
                    builder.OpaqueReferences.Add(Paths.Normalize(fields[2]));
                    break;
                case "Package":
                    builder.Packages.Add(fields[2]);
                    break;
                case "Item":
                    builder.Items.Add(Paths.Normalize(fields[2]));
                    break;
                case "Define" or "Framework":
                    // A framework without any symbol still compiles: its (empty) set is kept too.
                    var framework = fields.Length > 5 ? fields[5] : string.Empty;
                    if (!builder.Symbols.TryGetValue(framework, out var symbols))
                    {
                        symbols = new SortedSet<string>(StringComparer.Ordinal);
                        builder.Symbols[framework] = symbols;
                    }

                    if (fields[0] == "Define" && fields[2].Trim() is { Length: > 0 } symbol)
                    {
                        symbols.Add(symbol);
                    }

                    break;
            }
        }

        return builders.ToDictionary(
            static b => b.Key,
            static b => new EvaluatedProject(b.Value.IsTestProject, b.Value.AssemblyName, [.. b.Value.References.Order(StringComparer.Ordinal)], b.Value.Items, b.Value.Packages)
            {
                SymbolSets = SymbolSets(b.Value.Symbols.Values),
                OpaqueReferences = b.Value.OpaqueReferences,
            },
            Paths.Comparer);
    }

    /// <summary>Distinct symbol sets, each sorted, in a fixed order.</summary>
    /// <param name="sets">Symbols per target framework.</param>
    private static IReadOnlyList<IReadOnlyList<string>> SymbolSets(IEnumerable<IEnumerable<string>> sets) =>
        [.. sets.Select(static s => string.Join(';', s.Order(StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(static joined => (IReadOnlyList<string>)(joined.Length == 0 ? [] : joined.Split(';')))];

    /// <summary>Reads a cached evaluation, its paths relative to the repository; null when missing or unreadable.</summary>
    /// <param name="file">Cache file.</param>
    /// <param name="repoRoot">Repository root the paths are resolved against.</param>
    private static Dictionary<string, EvaluatedProject>? ReadCache(string file, string repoRoot)
    {
        try
        {
            if (!File.Exists(file) || JsonSerializer.Deserialize<Dictionary<string, CachedProject>>(File.ReadAllText(file)) is not { } cached)
            {
                return null;
            }

            string Full(string relative) => Paths.Normalize(Path.Combine(repoRoot, relative));
            return cached.ToDictionary(
                c => Full(c.Key),
                c => new EvaluatedProject(
                    c.Value.IsTestProject,
                    c.Value.AssemblyName,
                    [.. c.Value.References.Select(Full)],
                    c.Value.Items.Select(Full).ToHashSet(Paths.Comparer),
                    c.Value.Packages.ToHashSet(StringComparer.OrdinalIgnoreCase))
                {
                    SymbolSets = SymbolSets(c.Value.SymbolSets ?? []),
                    OpaqueReferences = (c.Value.OpaqueReferences ?? []).Select(Full).ToHashSet(Paths.Comparer),
                },
                Paths.Comparer);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes an evaluation with paths relative to the repository, so a clone elsewhere can use it;
    /// atomically, so a reader never sees half a file. A failure to write only costs the cache.
    /// </summary>
    /// <param name="file">Cache file.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="evaluated">The evaluation.</param>
    private void WriteCache(string file, string repoRoot, IReadOnlyDictionary<string, EvaluatedProject> evaluated)
    {
        string Relative(string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
        var cached = evaluated.ToDictionary(
            p => Relative(p.Key),
            p => new CachedProject(p.Value.IsTestProject, p.Value.AssemblyName, [.. p.Value.References.Select(Relative)], [.. p.Value.Items.Select(Relative)], [.. p.Value.Packages])
            {
                SymbolSets = [.. p.Value.SymbolSets.Select(static s => s.ToList())],
                OpaqueReferences = [.. p.Value.OpaqueReferences.Select(Relative)],
            });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(cached));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheNotWritten(file, ex.Message);
        }
    }

    /// <summary>Collects one project's lines.</summary>
    private sealed class Builder
    {
        /// <summary>See <see cref="EvaluatedProject.IsTestProject"/>.</summary>
        public bool IsTestProject { get; set; }

        /// <summary>See <see cref="EvaluatedProject.AssemblyName"/>.</summary>
        public string? AssemblyName { get; set; }

        /// <summary>See <see cref="EvaluatedProject.References"/>.</summary>
        public HashSet<string> References { get; } = new(Paths.Comparer);

        /// <summary>See <see cref="EvaluatedProject.OpaqueReferences"/>.</summary>
        public HashSet<string> OpaqueReferences { get; } = new(Paths.Comparer);

        /// <summary>See <see cref="EvaluatedProject.Items"/>.</summary>
        public HashSet<string> Items { get; } = new(Paths.Comparer);

        /// <summary>See <see cref="EvaluatedProject.Packages"/>.</summary>
        public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Preprocessor symbols by target framework; see <see cref="EvaluatedProject.SymbolSets"/>.</summary>
        public Dictionary<string, SortedSet<string>> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One project in the cache file, its paths relative to the repository.</summary>
    /// <param name="IsTestProject">See <see cref="EvaluatedProject.IsTestProject"/>.</param>
    /// <param name="AssemblyName">See <see cref="EvaluatedProject.AssemblyName"/>.</param>
    /// <param name="References">See <see cref="EvaluatedProject.References"/>.</param>
    /// <param name="Items">See <see cref="EvaluatedProject.Items"/>.</param>
    /// <param name="Packages">See <see cref="EvaluatedProject.Packages"/>.</param>
    private sealed record CachedProject(bool IsTestProject, string? AssemblyName, List<string> References, List<string> Items, List<string> Packages)
    {
        /// <summary>See <see cref="EvaluatedProject.SymbolSets"/>; null in caches written before symbols were read.</summary>
        public List<List<string>>? SymbolSets { get; init; }

        /// <summary>See <see cref="EvaluatedProject.OpaqueReferences"/>; null in caches written before they were read.</summary>
        public List<string>? OpaqueReferences { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "MSBuild evaluated {Evaluated} of {Count} project(s) in {Seconds:0.0} s")]
    private partial void LogEvaluated(int evaluated, int count, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MSBuild evaluation failed (exit code {ExitCode}): {Output}")]
    private partial void LogFailed(int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MSBuild could not evaluate some projects (exit code {ExitCode}): {Output}")]
    private partial void LogPartlyFailed(int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Project evaluation reused from {File}")]
    private partial void LogCacheHit(string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SDK version unknown ({Error}); project evaluation is not cached")]
    private partial void LogNoSdkVersion(string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not write the evaluation cache {File}: {Error}")]
    private partial void LogCacheNotWritten(string file, string error);
}
