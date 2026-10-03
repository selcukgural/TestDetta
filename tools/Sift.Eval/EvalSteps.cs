using Microsoft.Extensions.Logging;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Eval;

/// <summary>
/// Steps every evaluation mode shares: recording and caching coverage maps per commit, building the
/// test projects, finding their outputs, placing maps for Sift, and checking a selection.
/// </summary>
/// <param name="workspace">Where to work.</param>
/// <param name="settings">Evaluation settings.</param>
internal sealed partial class EvalSteps(EvalWorkspace workspace, RunSettings settings)
{
    private readonly ILogger _logger = workspace.LoggerFactory.CreateLogger<EvalSteps>();

    /// <summary>Longest a recording test process may run; a hang (a missing database, say) repeats.</summary>
    internal static TimeSpan RecordTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>File in a commit's map cache listing the test projects whose recording hung.</summary>
    private const string HungFile = "hung.txt";

    /// <summary>Test projects whose recording hung; they are left out of the rest of the evaluation.</summary>
    private readonly HashSet<string> _hung = new(StringComparer.Ordinal);

    /// <summary>
    /// Records coverage maps at <paramref name="sha"/> unless the cache has them. The maps of a merge
    /// are the base maps of the next one, so walking the history records each commit once.
    /// </summary>
    /// <param name="sha">Commit to record.</param>
    /// <returns>Maps by normalized test project path.</returns>
    public Dictionary<string, CoverageMap> RecordAt(string sha)
    {
        var directory = Path.Combine(workspace.MapCache, sha);
        var cached = settings.RecordingCache is { } cache ? Path.Combine(cache, $"{sha}-{RecordingKey(settings)}") : null;
        if (!Directory.Exists(directory) && settings.Maps is { } given && Directory.Exists(given))
        {
            // Maps recorded elsewhere at this commit, such as a corpus run's map cache: no recording.
            CopyDirectory(given, directory);
            LogRecordingReused(sha, given);
        }

        if (!Directory.Exists(directory) && cached is not null && Directory.Exists(cached))
        {
            CopyDirectory(cached, directory);
            LogRecordingReused(sha, cached);
        }

        if (!Directory.Exists(directory))
        {
            workspace.Git.Checkout(sha);
            workspace.Executor.Setup();
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>(), workspace.Evaluator).Load(workspace.RepoRoot);
            var recorder = new CoverageRecorder(workspace.LoggerFactory);
            var temporary = directory + ".partial";
            Directory.CreateDirectory(temporary);

            foreach (var project in TestProjects(graph))
            {
                using var log = File.CreateText(Path.Combine(temporary, Path.GetFileNameWithoutExtension(project.Path) + ".record.log"));
                try
                {
                    var (map, _) = recorder.Record(
                        new RecordRequest(workspace.RepoRoot, graph, project, new RecordOptions("Debug", settings.Framework, settings.Parallelism, RecordTimeout)), log);
                    CoverageMapStore.Write(Path.Combine(temporary, Path.GetFileNameWithoutExtension(project.Path) + ".json"), map);
                }
                catch (InvalidOperationException ex)
                {
                    // No tests (a project named like a test project) or no build (a platform the machine
                    // lacks): the project gets no map, and its merges are decided by name. A hang would
                    // repeat in every later run, so that project is left out altogether.
                    if (ex is RecordingTimedOutException)
                    {
                        _hung.Add(project.Path);
                    }

                    LogNotRecorded(project.Path, ex.Message);
                }
                finally
                {
                    workspace.Executor.Cleanup();
                }
            }

            File.WriteAllLines(Path.Combine(temporary, HungFile), _hung.Select(p => Path.GetRelativePath(workspace.RepoRoot, p)));
            Directory.Move(temporary, directory);
            LogRecorded(sha);
            if (cached is not null)
            {
                // Copied in full before it is renamed into place, so a killed run never leaves half a recording.
                var partial = cached + ".partial";
                if (Directory.Exists(partial))
                {
                    Directory.Delete(partial, recursive: true);
                }

                CopyDirectory(directory, partial);
                Directory.Move(partial, cached);
            }
        }

        // Cached maps say nothing about projects that hung while recording; the list next to them does.
        var hung = Path.Combine(directory, HungFile);
        if (File.Exists(hung))
        {
            _hung.UnionWith(File.ReadAllLines(hung).Where(static l => l.Length > 0)
                .Select(l => Path.GetFullPath(Path.Combine(workspace.RepoRoot, l)).Replace('\\', '/')));
        }

        var maps = new Dictionary<string, CoverageMap>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            if (CoverageMapStore.Read(file) is { } map)
            {
                maps[Path.GetFullPath(Path.Combine(workspace.RepoRoot, map.TestProject)).Replace('\\', '/')] = map;
            }
        }

        return maps;
    }

    /// <summary>
    /// Version of the evaluation's own recording logic (<see cref="RecordAt"/>, <see cref="TestProjects"/>,
    /// the recording options it passes); raise it with any change there, since cached recordings are
    /// keyed by it.
    /// </summary>
    private const int RecordingVersion = 1;

    /// <summary>Longest a test project's build may take; a build that hangs leaves the project out instead of stopping the evaluation.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// What decides a recording besides the commit: Sift's recording code (the contents of the core,
    /// coverage and tracker assemblies, so any change to Sift records again), the evaluation's recording
    /// logic (<see cref="RecordingVersion"/>) and the settings that shape it. A hash, as a file name part.
    /// </summary>
    /// <param name="settings">Evaluation settings.</param>
    internal static string RecordingKey(RunSettings settings)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var assembly in new[] { typeof(ProjectGraphLoader).Assembly, typeof(CoverageRecorder).Assembly, typeof(Sift.Coverage.Runtime.SiftTracker).Assembly })
        {
            hash.AppendData(File.ReadAllBytes(assembly.Location));
        }

        var shape = string.Join("\n",
            RecordingVersion,
            settings.Framework,
            settings.Parallelism,
            settings.Commands.Setup,
            string.Join(";", settings.Projects.Order(StringComparer.Ordinal)),
            string.Join(";", settings.Excluded.Order(StringComparer.Ordinal)));

        // Another SDK or runtime may record differently; without a toolchain the key stays as before.
        if (settings.Toolchain is { } toolchain)
        {
            shape += "\n" + toolchain.Description;
        }

        // So may the environment (a larger stack lets a suite run that crashed before).
        if (settings.Environment.Count > 0)
        {
            shape += "\n" + EnvironmentKey(settings);
        }

        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(shape));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>The evaluation's environment variables as one stable string, for cache keys.</summary>
    /// <param name="settings">Evaluation settings.</param>
    internal static string EnvironmentKey(RunSettings settings) =>
        string.Join(";", settings.Environment.OrderBy(static e => e.Key, StringComparer.Ordinal).Select(static e => $"{e.Key}={e.Value}"));

    /// <summary>Copies a directory's files (one level, as recordings are) to a new directory.</summary>
    /// <param name="source">Directory to copy.</param>
    /// <param name="target">Directory to create.</param>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>
    /// The test projects to evaluate: all, or those named with <c>--project</c>; with
    /// <c>--framework</c>, only those that target it.
    /// </summary>
    /// <param name="graph">Projects of the checked-out commit.</param>
    public List<ProjectNode> TestProjects(ProjectGraph graph)
    {
        var wanted = settings.Projects
            .Select(p => Path.GetFullPath(Path.Combine(workspace.RepoRoot, p)).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var excluded = settings.Excluded
            .Select(p => Path.GetFullPath(Path.Combine(workspace.RepoRoot, p)).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        excluded.UnionWith(_hung);

        return
        [
            .. graph.Projects
                .Where(p => p.IsTestProject && (wanted.Count == 0 || wanted.Contains(p.Path)) && !excluded.Contains(p.Path))
                .Where(TargetsFramework)
                .OrderBy(static p => p.Path, StringComparer.Ordinal),
        ];
    }

    /// <summary>True when no framework was requested, or the project targets the requested one.</summary>
    /// <param name="project">Test project.</param>
    private bool TargetsFramework(ProjectNode project)
    {
        if (settings.Framework is not { } framework)
        {
            return true;
        }

        var dotnet = new DotnetProcess(_logger);
        List<string> query = ["msbuild", project.Path, "-getProperty:TargetFramework", "-getProperty:TargetFrameworks"];
        string output;
        try
        {
            output = dotnet.Capture(query, RunDirectory(project));
        }
        catch (InvalidOperationException ex)
        {
            // A stale project (an import that no longer exists, a Visual Studio-only target) cannot be
            // evaluated, let alone built; it is left out instead of ending the whole evaluation.
            LogNotEvaluable(project.Path, ex.Message);
            return false;
        }

        using var properties = System.Text.Json.JsonDocument.Parse(output);
        var values = properties.RootElement.GetProperty("Properties");
        var frameworks = $"{values.GetProperty("TargetFramework").GetString()};{values.GetProperty("TargetFrameworks").GetString()}"
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var targets = frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase);
        if (!targets)
        {
            LogSkippedFramework(project.Path, framework);
        }

        return targets;
    }

    /// <summary>
    /// Where dotnet runs for <paramref name="project"/>: the directory of its global.json, so builds and
    /// MSBuild queries use the SDK its tests run with (<see cref="DotnetTestLocation"/>).
    /// </summary>
    /// <param name="project">A test project of the workspace.</param>
    private string RunDirectory(ProjectNode project) =>
        DotnetTestLocation.For(workspace.RepoRoot, project.Path).FullDirectory(workspace.RepoRoot);

    /// <summary>
    /// Builds the test projects once, so every later run can use <c>--no-build</c>. A project that does
    /// not build here (it needs another platform, say) is left out of the evaluation of this merge.
    /// </summary>
    /// <param name="testProjects">Projects to build.</param>
    /// <returns>The projects that built.</returns>
    public List<ProjectNode> Build(List<ProjectNode> testProjects)
    {
        var dotnet = new DotnetProcess(_logger);
        var built = new List<ProjectNode>(testProjects.Count);
        foreach (var project in testProjects)
        {
            List<string> arguments = ["build", project.Path, "-c", "Debug"];
            if (settings.Framework is { } framework)
            {
                arguments.AddRange(["-f", framework]);
            }

            var logPath = Path.Combine(workspace.Executor.LogDirectory, $"build-{Path.GetFileNameWithoutExtension(project.Path)}.log");
            int exitCode;
            using (var log = File.CreateText(logPath))
            {
                exitCode = dotnet.Run(arguments, RunDirectory(project), new Dictionary<string, string>(), log, BuildTimeout);
            }

            if (exitCode == 0)
            {
                built.Add(project);
                continue;
            }

            if (exitCode == DotnetProcess.TimedOut)
            {
                LogBuildFailed(project.Path, $"did not build within {BuildTimeout.TotalMinutes:0} minutes");
                continue;
            }

            // The first errors say more than the exit code; the full log stays next to the reports.
            var errors = File.ReadLines(logPath)
                .Where(static l => l.Contains(": error ", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(2);
            LogBuildFailed(project.Path, string.Join(" / ", errors));
        }

        return built;
    }

    /// <summary>
    /// The projects whose run without mutants produced a test report and did not crash. Without one a
    /// project's failures are unknown: counting them as none would let a mutant only that project
    /// catches look harmless, or a crash outside the selection look caught; and a project that crashes
    /// anyway would count every mutant as caught by a crash. Such projects are left out and named.
    /// </summary>
    /// <param name="testProjects">Projects that ran.</param>
    /// <param name="baseline">Their run without mutants.</param>
    public List<ProjectNode> Reporting(List<ProjectNode> testProjects, TestRunOutcome baseline)
    {
        var reporting = new List<ProjectNode>(testProjects.Count);
        foreach (var project in testProjects)
        {
            if (baseline.Unreported.Contains(project.Path) || baseline.Crashed.Contains(project.Path))
            {
                LogUnreported(project.Path);
                continue;
            }

            reporting.Add(project);
        }

        return reporting;
    }

    /// <summary>Copies the cached maps of <paramref name="sha"/> to where Sift looks for them.</summary>
    /// <param name="sha">Commit the maps were recorded at.</param>
    public void PlaceMaps(string sha)
    {
        ClearMaps();
        var target = Path.Combine(workspace.RepoRoot, CoverageMapStore.Directory);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(Path.Combine(workspace.MapCache, sha), "*.json"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    /// <summary>Removes the maps from the clone so they never mix with a later commit.</summary>
    public void ClearMaps()
    {
        var directory = Path.Combine(workspace.RepoRoot, ".sift");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Every assembly built from the repository found in the test outputs. The mutant is injected
    /// wherever its method exists, whichever project Sift thinks the source belongs to. A copy that
    /// came from a NuGet package instead (Carter's Newtonsoft negotiator tests use the published
    /// Carter package, not the project) is left alone: no source change can reach it.
    /// </summary>
    /// <param name="names">Assembly file names of the repository's projects.</param>
    /// <param name="outputs">Output directories of the evaluated test projects.</param>
    /// <param name="packagesRoot">NuGet global packages folder; null for the default (<c>NUGET_PACKAGES</c> or <c>~/.nuget/packages</c>).</param>
    public static List<string> RepositoryAssemblies(HashSet<string> names, IEnumerable<string> outputs, string? packagesRoot = null)
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

    /// <summary>
    /// The output directory of each test project, asked from MSBuild for the evaluated framework:
    /// projects may use the artifacts layout or a custom output path instead of <c>bin/Debug</c>.
    /// </summary>
    /// <param name="testProjects">Evaluated test projects.</param>
    public Dictionary<string, string> OutputDirectories(List<ProjectNode> testProjects)
    {
        var dotnet = new DotnetProcess(_logger);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in testProjects)
        {
            List<string> query = ["msbuild", project.Path, "-p:Configuration=Debug", "-getProperty:TargetPath"];
            if (settings.Framework is { } framework)
            {
                query.Add($"-p:TargetFramework={framework}");
            }

            var targetPath = dotnet.Capture(query, RunDirectory(project)).Trim();
            if (targetPath.Length > 0)
            {
                outputs[project.Path] = Path.GetDirectoryName(targetPath)!;
            }
        }

        return outputs;
    }

    /// <summary>True when Sift's selection runs <paramref name="testClass"/> of <paramref name="project"/>.</summary>
    /// <param name="analysis">Sift's analysis.</param>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name.</param>
    public static bool IsSelected(AnalysisResult analysis, string project, string testClass) =>
        IsSelected(analysis.Tests, project, testClass);

    /// <summary>True when the decisions run <paramref name="testClass"/> of <paramref name="project"/>.</summary>
    /// <param name="tests">Sift's decision per affected test project.</param>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime test class name.</param>
    public static bool IsSelected(IReadOnlyList<TestProjectSelection> tests, string project, string testClass)
    {
        var selection = tests.FirstOrDefault(t => string.Equals(t.Project.Path, project, StringComparison.Ordinal));
        return selection?.Mode switch
        {
            TestRunMode.All => true,
            TestRunMode.Filtered => selection.TestClasses.Contains(testClass),
            _ => false,
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording of {Sha} reused from {Cache}")]
    private partial void LogRecordingReused(string sha, string cache);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} does not build here and is left out: {Errors}")]
    private partial void LogBuildFailed(string project, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} wrote no test report without mutants; its failures cannot be seen, so it is left out")]
    private partial void LogUnreported(string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} cannot be evaluated by MSBuild here and is left out: {Error}")]
    private partial void LogNotEvaluable(string project, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Project} does not target {Framework}; left out")]
    private partial void LogSkippedFramework(string project, string framework);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} not recorded: {Reason}")]
    private partial void LogNotRecorded(string project, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded coverage at {Sha}")]
    private partial void LogRecorded(string sha);
}
