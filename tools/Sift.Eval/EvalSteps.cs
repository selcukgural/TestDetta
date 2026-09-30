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
        if (!Directory.Exists(directory))
        {
            workspace.Git.Checkout(sha);
            workspace.Executor.Setup();
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>()).Load(workspace.RepoRoot);
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
        using var properties = System.Text.Json.JsonDocument.Parse(dotnet.Capture(query, workspace.RepoRoot));
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
                exitCode = dotnet.Run(arguments, workspace.RepoRoot, new Dictionary<string, string>(), log);
            }

            if (exitCode == 0)
            {
                built.Add(project);
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
    /// wherever its method exists, whichever project Sift thinks the source belongs to.
    /// </summary>
    /// <param name="names">Assembly file names of the repository's projects.</param>
    /// <param name="outputs">Output directories of the evaluated test projects.</param>
    public static List<string> RepositoryAssemblies(HashSet<string> names, IEnumerable<string> outputs)
    {
        var copies = new List<string>();
        foreach (var output in outputs)
        {
            if (!Directory.Exists(output))
            {
                continue;
            }

            copies.AddRange(Directory
                .EnumerateFiles(output, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(f => names.Contains(Path.GetFileName(f))));
        }

        return copies;
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

            var targetPath = dotnet.Capture(query, workspace.RepoRoot).Trim();
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
    public static bool IsSelected(AnalysisResult analysis, string project, string testClass)
    {
        var selection = analysis.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, project, StringComparison.Ordinal));
        return selection?.Mode switch
        {
            TestRunMode.All => true,
            TestRunMode.Filtered => selection.TestClasses.Contains(testClass),
            _ => false,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} does not build here and is left out: {Errors}")]
    private partial void LogBuildFailed(string project, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Project} does not target {Framework}; left out")]
    private partial void LogSkippedFramework(string project, string framework);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} not recorded: {Reason}")]
    private partial void LogNotRecorded(string project, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded coverage at {Sha}")]
    private partial void LogRecorded(string sha);
}
