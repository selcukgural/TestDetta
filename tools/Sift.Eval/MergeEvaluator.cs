using Microsoft.Extensions.Logging;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Eval;

/// <summary>Where an evaluation works.</summary>
/// <param name="Git">Git of the clone.</param>
/// <param name="RepoRoot">Clone directory.</param>
/// <param name="MapCache">Directory holding recorded coverage maps, one subdirectory per commit.</param>
/// <param name="Executor">Runs tests in the clone.</param>
/// <param name="LoggerFactory">Creates loggers for Sift's own classes.</param>
internal sealed record EvalWorkspace(EvalGit Git, string RepoRoot, string MapCache, TestExecutor Executor, ILoggerFactory LoggerFactory);

/// <summary>
/// Evaluates one merge: records coverage at its parent, lets Sift select, runs selection and full
/// suite, and checks the selection with coverage recorded after the merge and with mutants.
/// </summary>
/// <param name="workspace">Where to work.</param>
/// <param name="settings">Evaluation settings.</param>
internal sealed partial class MergeEvaluator(EvalWorkspace workspace, RunSettings settings)
{
    private readonly ILogger _logger = workspace.LoggerFactory.CreateLogger<MergeEvaluator>();

    /// <summary>Evaluates <paramref name="commit"/>; a build or run failure is reported, not thrown.</summary>
    /// <param name="commit">Merge to evaluate.</param>
    public MergeResult Evaluate(EvalCommit commit)
    {
        var info = new MergeInfo(commit.Sha, commit.Parent, commit.Subject, workspace.Git.ChangedFileCount(commit.Parent, commit.Sha));
        try
        {
            RecordAt(commit.Parent);

            workspace.Git.Checkout(commit.Sha);
            workspace.Executor.Setup();
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>()).Load(workspace.RepoRoot);
            var testProjects = Build(TestProjects(graph));
            if (testProjects.Count == 0)
            {
                throw new InvalidOperationException("No test project could be built.");
            }

            workspace.Executor.Outputs = OutputDirectories(testProjects);

            PlaceMaps(commit.Parent);
            var analysis = new AnalysisPipeline(workspace.LoggerFactory).Run(workspace.RepoRoot, commit.Parent);
            ClearMaps();

            var mode = DotnetTestModeDetector.Detect(workspace.RepoRoot);
            var inScope = new HashSet<string>(testProjects.Select(static p => p.Path), StringComparer.Ordinal);
            var invocations = TestInvocationBuilder.Build(
                [.. analysis.Tests.Where(t => inScope.Contains(t.Project.Path))], workspace.RepoRoot, [], mode);

            var selected = workspace.Executor.RunSelected(invocations, "selected");
            var full = workspace.Executor.RunAll(testProjects, "full");
            var mutants = RunMutants(analysis, testProjects, full);

            var afterMaps = RecordAt(commit.Sha);
            var coverageMisses = CoverageMisses(analysis, afterMaps);

            LogEvaluated(commit.Sha, full.Seconds, selected.Seconds);
            return new MergeResult(info, analysis.Plan, new RunComparison(full, selected), new SafetyCheck(coverageMisses, mutants), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            LogFailed(commit.Sha, ex);
            return new MergeResult(info, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Records coverage maps at <paramref name="sha"/> unless the cache has them. The maps of a merge
    /// are the base maps of the next one, so walking the history records each commit once.
    /// </summary>
    /// <param name="sha">Commit to record.</param>
    /// <returns>Maps by normalized test project path.</returns>
    private Dictionary<string, CoverageMap> RecordAt(string sha)
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
                        new RecordRequest(workspace.RepoRoot, graph, project, new RecordOptions("Debug", settings.Framework, settings.Parallelism)), log);
                    CoverageMapStore.Write(Path.Combine(temporary, Path.GetFileNameWithoutExtension(project.Path) + ".json"), map);
                }
                catch (InvalidOperationException ex)
                {
                    // No tests (a project named like a test project) or no build (a platform the machine
                    // lacks): the project gets no map, and its merges are decided by name.
                    LogNotRecorded(project.Path, ex.Message);
                }
                finally
                {
                    workspace.Executor.Cleanup();
                }
            }

            Directory.Move(temporary, directory);
            LogRecorded(sha);
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
    private List<ProjectNode> TestProjects(ProjectGraph graph)
    {
        var wanted = settings.Projects
            .Select(p => Path.GetFullPath(Path.Combine(workspace.RepoRoot, p)).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        return
        [
            .. graph.Projects
                .Where(p => p.IsTestProject && (wanted.Count == 0 || wanted.Contains(p.Path)))
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
    private List<ProjectNode> Build(List<ProjectNode> testProjects)
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
    private void PlaceMaps(string sha)
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
    private void ClearMaps()
    {
        var directory = Path.Combine(workspace.RepoRoot, ".sift");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Mutates up to <see cref="RunSettings.Mutants"/> changed members, one at a time, and runs the
    /// full suite against each. A class that fails only with the mutant must be in Sift's selection.
    /// Targets and assemblies are found independently of Sift's project mapping, so a file Sift
    /// cannot place (a linked source file, say) is still checked.
    /// </summary>
    /// <param name="analysis">Sift's analysis of the merge.</param>
    /// <param name="testProjects">Evaluated test projects.</param>
    /// <param name="baseline">Full run without mutants, whose failures are not the mutant's.</param>
    private List<MutantResult> RunMutants(AnalysisResult analysis, List<ProjectNode> testProjects, TestRunOutcome baseline)
    {
        var results = new List<MutantResult>();
        var index = 0;
        var assemblies = RepositoryAssemblies(new ProjectAssemblies(_logger).FileNames(analysis.Graph, workspace.RepoRoot), workspace.Executor.Outputs.Values);
        foreach (var target in MutationTargets(analysis).Take(settings.Mutants))
        {
            index++;
            using var mutation = MutationInjector.Inject(assemblies, target, Path.Combine(workspace.MapCache, "..", "mutant-backup"));
            if (mutation.MutatedMethods == 0)
            {
                results.Add(new MutantResult(target, 0, [], []));
                continue;
            }

            var run = workspace.Executor.RunAll(testProjects, $"mutant-{index}");
            if (run.Aborted > 0)
            {
                // A crash hides which classes failed; a missing class would prove nothing either way.
                results.Add(new MutantResult(target, mutation.MutatedMethods, [], [], Inconclusive: true));
                continue;
            }

            var killed = new List<string>();
            var missed = new List<string>();
            foreach (var (project, failed) in run.FailedByProject)
            {
                var before = baseline.FailedByProject.GetValueOrDefault(project) ?? new HashSet<string>();
                foreach (var testClass in failed.Where(c => !before.Contains(c)).Order(StringComparer.Ordinal))
                {
                    killed.Add(testClass);
                    if (!IsSelected(analysis, project, testClass))
                    {
                        missed.Add(testClass);
                    }
                }
            }

            LogMutant(target.TypeName, target.MemberName, killed.Count, missed.Count);
            results.Add(new MutantResult(target, mutation.MutatedMethods, killed, missed));
        }

        return results;
    }

    /// <summary>
    /// Modified members of every changed C# file outside test projects, including files that belong
    /// to no project directory.
    /// </summary>
    /// <param name="analysis">Sift's analysis of the merge.</param>
    private IEnumerable<CoveredMethod> MutationTargets(AnalysisResult analysis)
    {
        var seen = new HashSet<CoveredMethod>();
        foreach (var change in analysis.Impact.CodeChanges.OrderBy(static c => c.Path, StringComparer.Ordinal))
        {
            var owner = analysis.Graph.FindOwner(Path.GetFullPath(Path.Combine(workspace.RepoRoot, change.Path)).Replace('\\', '/'));
            if (owner is { IsTestProject: true })
            {
                continue;
            }

            foreach (var symbol in change.Symbols)
            {
                if (symbol is not { Kind: SymbolChangeKind.Modified, MemberName: { } member })
                {
                    continue;
                }

                var name = member.StartsWith(".ctor", StringComparison.Ordinal) ? ".ctor"
                    : DeclarationWalker.ReferenceNames(member) is [var single] ? single
                    : null;
                if (name is not null && seen.Add(new CoveredMethod(symbol.TypeName, name)))
                {
                    yield return new CoveredMethod(symbol.TypeName, name);
                }
            }
        }
    }

    /// <summary>
    /// Every assembly built from the repository found in the test outputs. The mutant is injected
    /// wherever its method exists, whichever project Sift thinks the source belongs to.
    /// </summary>
    /// <param name="names">Assembly file names of the repository's projects.</param>
    /// <param name="outputs">Output directories of the evaluated test projects.</param>
    private static List<string> RepositoryAssemblies(HashSet<string> names, IEnumerable<string> outputs)
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
    private Dictionary<string, string> OutputDirectories(List<ProjectNode> testProjects)
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
    private static bool IsSelected(AnalysisResult analysis, string project, string testClass)
    {
        var selection = analysis.Tests.FirstOrDefault(t => string.Equals(t.Project.Path, project, StringComparison.Ordinal));
        return selection?.Mode switch
        {
            TestRunMode.All => true,
            TestRunMode.Filtered => selection.TestClasses.Contains(testClass),
            _ => false,
        };
    }

    /// <summary>
    /// Test classes that, per coverage recorded after the merge, ran a changed member (or any member
    /// of a changed type) but were not selected.
    /// </summary>
    /// <param name="analysis">Sift's analysis.</param>
    /// <param name="afterMaps">Maps recorded at the merge.</param>
    /// <remarks>
    /// Maps name types without their assembly, so a change counts for a test project only when the
    /// changed file's project is one it depends on; otherwise a tool's <c>Program</c> would match the
    /// application's. A file outside every project counts everywhere, so Sift's blind spots stay visible.
    /// </remarks>
    private List<string> CoverageMisses(AnalysisResult analysis, Dictionary<string, CoverageMap> afterMaps)
    {
        var changed = new List<(string Type, string? Member, ProjectNode? Owner)>();
        foreach (var change in analysis.Impact.CodeChanges)
        {
            var owner = analysis.Graph.FindOwner(Path.GetFullPath(Path.Combine(workspace.RepoRoot, change.Path)).Replace('\\', '/'));
            foreach (var symbol in change.Symbols)
            {
                var member = symbol.MemberName switch
                {
                    null => null,
                    var m when m.StartsWith(".ctor", StringComparison.Ordinal) => ".ctor",
                    var m => DeclarationWalker.ReferenceNames(m) is [var single] ? single : null,
                };

                if (symbol.MemberName is null || member is not null)
                {
                    changed.Add((symbol.TypeName, member, owner));
                }
            }
        }

        var misses = new List<string>();
        foreach (var (project, map) in afterMaps)
        {
            var relevant = changed.FindAll(c => c.Owner is null || analysis.Graph.WithDependents([c.Owner.Path]).Contains(project));
            foreach (var (testClass, ids) in map.Classes)
            {
                var ran = ids.Select(id => map.Methods[id]).ToList();
                var touches = relevant.Exists(c => c.Member is null
                    ? ran.Exists(m => m.TypeName == c.Type || m.TypeName.StartsWith(c.Type + ".", StringComparison.Ordinal))
                    : ran.Contains(new CoveredMethod(c.Type, c.Member)));

                if (touches && !IsSelected(analysis, project, testClass))
                {
                    misses.Add($"{Path.GetFileNameWithoutExtension(project)}: {testClass}");
                }
            }
        }

        misses.Sort(StringComparer.Ordinal);
        return misses;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} does not build here and is left out: {Errors}")]
    private partial void LogBuildFailed(string project, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Project} does not target {Framework}; left out")]
    private partial void LogSkippedFramework(string project, string framework);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project} not recorded: {Reason}")]
    private partial void LogNotRecorded(string project, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded coverage at {Sha}")]
    private partial void LogRecorded(string sha);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sha}: full {FullSeconds:0.0} s, selected {SelectedSeconds:0.0} s")]
    private partial void LogEvaluated(string sha, double fullSeconds, double selectedSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mutant {TypeName}.{MemberName}: {Killed} class(es) failed, {Missed} not selected")]
    private partial void LogMutant(string typeName, string memberName, int killed, int missed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Sha} could not be evaluated")]
    private partial void LogFailed(string sha, Exception exception);
}
