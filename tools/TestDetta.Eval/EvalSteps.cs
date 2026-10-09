using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Eval;

/// <summary>
/// Steps every evaluation mode shares: recording and caching coverage maps per commit, building the
/// test projects, finding their outputs, placing maps for TestDetta, and checking a selection.
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

    /// <summary>
    /// File in a commit's map cache listing the test projects that got no map, one per line: the project relative to
    /// the clone, whether the failure was TestDetta's (<see cref="RecordingFailedException"/>: <c>passed</c>,
    /// <c>failed</c> or <c>not run</c>, the tests' result) or not (<c>-</c>: no build, no tests, a hang), and the reason.
    /// </summary>
    private const string NotRecordedFile = "not-recorded.tsv";

    /// <summary>Suffix of the file next to a recorded map that holds the recording's wall time in seconds.</summary>
    private const string SecondsSuffix = ".record-seconds";

    /// <summary>Directory under the map cache that holds the maps an incremental step built at a commit from older maps.</summary>
    /// <param name="sha">Commit the step recorded at.</param>
    /// <param name="mapCommit">Commit of the older maps.</param>
    public static string IncrementalKey(string sha, string mapCommit) => $"{sha}-incremental-{mapCommit[..Math.Min(12, mapCommit.Length)]}";

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
            var untracked = workspace.Git.Untracked();
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>(), workspace.Evaluator).Load(workspace.RepoRoot);
            var recorder = new CoverageRecorder(workspace.LoggerFactory);
            var temporary = directory + ".partial";
            Directory.CreateDirectory(temporary);
            var notRecorded = new List<string>();

            foreach (var project in TestProjects(graph))
            {
                using var log = File.CreateText(Path.Combine(temporary, Path.GetFileNameWithoutExtension(project.Path) + ".record.log"));
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var (map, _) = recorder.Record(
                        new RecordRequest(workspace.RepoRoot, graph, project, new RecordOptions("Debug", settings.Framework, settings.Parallelism, RecordTimeout)), log);
                    CoverageMapStore.Write(Path.Combine(temporary, Path.GetFileNameWithoutExtension(project.Path) + ".json"), map);
                    // Kept for the incremental recording's cost comparison (docs/design/incremental-recording.md).
                    File.WriteAllText(
                        Path.Combine(temporary, Path.GetFileNameWithoutExtension(project.Path) + SecondsSuffix),
                        watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
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
                    var tests = ex is RecordingFailedException failed
                        ? failed.TestsPassed switch { true => "passed", false => "failed", null => "not run" }
                        : "-";
                    notRecorded.Add($"{Path.GetRelativePath(workspace.RepoRoot, project.Path).Replace('\\', '/')}\t{tests}\t{ex.Message.ReplaceLineEndings(" ").Replace('\t', ' ')}");
                }
                finally
                {
                    workspace.Executor.Cleanup();
                }
            }

            RemoveLeftBehind(untracked);
            File.WriteAllLines(Path.Combine(temporary, HungFile), _hung.Select(p => Path.GetRelativePath(workspace.RepoRoot, p)));
            File.WriteAllLines(Path.Combine(temporary, NotRecordedFile), notRecorded);
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
    /// What a reader of the evaluation's numbers must know about its maps at <paramref name="sha"/>
    /// (docs/evaluation.md, "Incidents"): a test project TestDetta failed to record (its test assembly not instrumented,
    /// the tracker silent), above all one whose tests passed; a map that recorded no test class; a map whose every
    /// class failed, or whose failures no report names (it is not used). Each makes the selection for that project
    /// fall back to names, so its share and its misses are not what a working recording would give.
    /// </summary>
    /// <param name="sha">Commit the maps were recorded at.</param>
    /// <param name="maps">The maps, by normalized test project path.</param>
    public List<string> Incidents(string sha, IReadOnlyDictionary<string, CoverageMap> maps)
    {
        var incidents = new List<string>();
        var file = Path.Combine(workspace.MapCache, sha, NotRecordedFile);
        foreach (var line in File.Exists(file) ? File.ReadLines(file) : [])
        {
            var parts = line.Split('\t', 3);
            if (parts.Length < 3 || parts[1] == "-")
            {
                continue;
            }

            incidents.Add(parts[1] == "passed"
                ? $"{parts[0]}: not recorded although its tests passed: {parts[2]}"
                : $"{parts[0]}: not recorded ({(parts[1] == "failed" ? "some of its tests failed" : "its tests were not run")}): {parts[2]}");
        }

        foreach (var map in maps.Values.OrderBy(static m => m.TestProject, StringComparer.Ordinal))
        {
            if (map.FailedClasses is null)
            {
                incidents.Add($"{map.TestProject}: tests failed while recording and no report says which; the map is not used");
            }
            else if (map.Classes.Count == 0)
            {
                incidents.Add($"{map.TestProject}: the map recorded 0 test classes{(map.FailedClasses.Count > 0 ? $" ({map.FailedClasses.Count} failed or unrecorded)" : string.Empty)}");
            }
            else if (map.Classes.Keys.All(map.FailedClasses.Contains))
            {
                incidents.Add($"{map.TestProject}: every one of the map's {map.Classes.Count} test classes failed while recording");
            }
        }

        return incidents;
    }

    /// <summary>
    /// Version of the evaluation's own recording logic (<see cref="RecordAt"/>, <see cref="TestProjects"/>,
    /// the recording options it passes); raise it with any change there, since cached recordings are
    /// keyed by it. 3: the projects that got no map are listed with the reason (<see cref="NotRecordedFile"/>).
    /// </summary>
    private const int RecordingVersion = 3;

    /// <summary>Longest a test project's build may take; a build that hangs leaves the project out instead of stopping the evaluation.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The recording code's fingerprint: the coverage and tracker assemblies in full, and the part of the
    /// core assembly they, the project graph loader and the map store reach (<see cref="RecordingFingerprint"/>).
    /// Computed once per process.
    /// </summary>
    private static readonly Lazy<string> RecordingCode = new(static () => RecordingFingerprint.Compute(
        [typeof(CoverageRecorder).Assembly.Location, typeof(TestDetta.Coverage.Runtime.TestDettaTracker).Assembly.Location],
        typeof(ProjectGraphLoader).Assembly.Location,
        [typeof(ProjectGraphLoader).FullName!, typeof(CoverageMapStore).FullName!]));

    /// <summary>
    /// What decides a recording besides the commit: TestDetta's recording code (<see cref="RecordingCode"/>,
    /// so a change to selection alone keeps the cached maps), the evaluation's recording logic
    /// (<see cref="RecordingVersion"/>) and the settings that shape it. A hash, as a file name part.
    /// </summary>
    /// <param name="settings">Evaluation settings.</param>
    internal static string RecordingKey(RunSettings settings)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(RecordingCode.Value));

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
    /// The projects whose run without mutants produced a test report and completed (no crash, no report or exit
    /// code saying the run did not complete, <see cref="ProjectRun.Complete"/>). Without one a
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
            if (baseline.Unreported.Contains(project.Path) || baseline.Crashed.Contains(project.Path)
                || baseline.Projects.TryGetValue(project.Path, out var run) && !run.Complete)
            {
                LogUnreported(project.Path);
                continue;
            }

            reporting.Add(project);
        }

        return reporting;
    }

    /// <summary>Copies the cached maps of <paramref name="sha"/> to where TestDetta looks for them.</summary>
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

    /// <summary>
    /// Runs one incremental recording step at <paramref name="sha"/> from the full maps at <paramref name="mapCommit"/>, as
    /// <c>td record --incremental</c> does, unless the cache has it, and compares each result with the full map at
    /// <paramref name="sha"/> (docs/design/incremental-recording.md). Both commits must be recorded already.
    /// </summary>
    /// <param name="mapCommit">Commit of the older maps, an ancestor of <paramref name="sha"/>.</param>
    /// <param name="sha">Commit the step records at.</param>
    /// <param name="full">The full maps at <paramref name="sha"/>, by normalized test project path.</param>
    /// <returns>What the step did per test project.</returns>
    public List<IncrementalStepResult> RecordIncremental(string mapCommit, string sha, Dictionary<string, CoverageMap> full)
    {
        var key = IncrementalKey(sha, mapCommit);
        var directory = Path.Combine(workspace.MapCache, key);
        var stepsFile = Path.Combine(directory, "steps.json");
        if (!File.Exists(stepsFile))
        {
            var temporary = directory + ".partial";
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            Directory.CreateDirectory(temporary);
            workspace.Git.Checkout(sha);
            workspace.Executor.Setup();
            var untracked = workspace.Git.Untracked();
            PlaceMaps(mapCommit);
            var graph = new ProjectGraphLoader(workspace.LoggerFactory.CreateLogger<ProjectGraphLoader>(), workspace.Evaluator).Load(workspace.RepoRoot);
            var projects = TestProjects(graph).Where(p => full.ContainsKey(p.Path)).ToList();
            var record = new TestDetta.Cli.RecordSettings("Debug", [], settings.Parallelism, settings.Framework);
            var plan = new TestDetta.Cli.IncrementalRecording(workspace.LoggerFactory, workspace.Evaluator)
                .Plan(new TestDetta.Cli.IncrementalContext(workspace.RepoRoot, graph, record, int.MaxValue, SelectionLicensed: true), projects, DateTimeOffset.UtcNow);
            ClearMaps();

            var recorder = new CoverageRecorder(workspace.LoggerFactory);
            var results = new List<IncrementalStepResult>();
            foreach (var step in plan)
            {
                var name = Path.GetFileNameWithoutExtension(step.Project.Path);
                var fullMap = full[step.Project.Path];
                var total = step.Previous is { } before ? before.Classes.Count + (before.FailedClasses?.Count ?? 0) + (before.SkippedClasses?.Count ?? 0) : 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                CoverageMap map;
                var recorded = 0;
                switch (step.Kind)
                {
                    case TestDetta.Cli.StepKind.Partial:
                        using (var log = File.CreateText(Path.Combine(temporary, name + ".record.log")))
                        {
                            var partial = recorder.RecordClasses(
                                new RecordRequest(workspace.RepoRoot, graph, step.Project, new RecordOptions("Debug", settings.Framework, settings.Parallelism, RecordTimeout) { Classes = step.Classes }), log);
                            map = partial.Complete
                                ? partial.Map
                                : CoverageMapMerge.Merge(step.Previous!, partial.Map, new MapMergeScope(
                                    step.Classes.ToHashSet(StringComparer.Ordinal), partial.AssemblyClasses, CoverageMapMerge.UnobservedBy(step.Changes, partial.Map.Methods)));
                            recorded = step.Classes.Count;
                        }

                        workspace.Executor.Cleanup();
                        break;
                    case TestDetta.Cli.StepKind.Carry:
                        var previous = step.Previous!;
                        map = previous with
                        {
                            Header = previous.Header with { Commit = sha },
                            Lineage = new MapLineage(previous.Commit, previous.Header.RecordedAt, 1),
                        };
                        break;
                    default:
                        // A full recording at this commit is the one already made.
                        map = fullMap;
                        recorded = fullMap.Classes.Count;
                        break;
                }

                var seconds = step.Kind == TestDetta.Cli.StepKind.Partial ? watch.Elapsed.TotalSeconds : step.Kind == TestDetta.Cli.StepKind.Carry ? 0 : FullSeconds(sha, name) ?? 0;
                CoverageMapStore.Write(Path.Combine(temporary, name + ".json"), map);
                var divergent = map.Lineage is null ? [] : MapDivergence.Find(map, fullMap).Select(static d => $"{d.TestClass}: {string.Join(", ", d.Missing)}").ToList();
                results.Add(new IncrementalStepResult(name, step.Kind.ToString().ToLowerInvariant(), step.Reason, recorded, total, Math.Round(seconds, 1), FullSeconds(sha, name), divergent));
                LogIncrementalStep(name, step.Kind, recorded, total);
            }

            RemoveLeftBehind(untracked);
            using (var stream = File.Create(Path.Combine(temporary, "steps.json")))
            {
                System.Text.Json.JsonSerializer.Serialize(stream, results);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.Move(temporary, directory);
        }

        using var read = File.OpenRead(stepsFile);
        return System.Text.Json.JsonSerializer.Deserialize<List<IncrementalStepResult>>(read) ?? [];
    }

    /// <summary>Wall time of the full recording of a test project at a commit; null when the cache did not keep it.</summary>
    /// <param name="sha">Commit.</param>
    /// <param name="project">Test project name.</param>
    private double? FullSeconds(string sha, string project)
    {
        var file = Path.Combine(workspace.MapCache, sha, project + SecondsSuffix);
        return File.Exists(file) && double.TryParse(File.ReadAllText(file), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    /// <summary>Removes the maps from the clone so they never mix with a later commit.</summary>
    public void ClearMaps()
    {
        var directory = Path.Combine(workspace.RepoRoot, ".testdetta");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The repository's assemblies in the test outputs (<see cref="Mutants.RepositoryAssemblies.Find"/>, shared with <c>td audit</c>).</summary>
    /// <param name="names">Assembly file names of the repository's projects.</param>
    /// <param name="outputs">Output directories of the evaluated test projects.</param>
    /// <param name="packagesRoot">NuGet global packages folder; null for the default (<c>NUGET_PACKAGES</c> or <c>~/.nuget/packages</c>).</param>
    public static List<string> RepositoryAssemblies(HashSet<string> names, IEnumerable<string> outputs, string? packagesRoot = null) =>
        Mutants.RepositoryAssemblies.Find(names, outputs, packagesRoot);

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

    /// <summary>True when TestDetta's selection runs <paramref name="testClass"/> of <paramref name="project"/>.</summary>
    /// <param name="analysis">TestDetta's analysis.</param>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime class name.</param>
    public static bool IsSelected(AnalysisResult analysis, string project, string testClass) =>
        IsSelected(analysis.Tests, project, testClass);

    /// <summary>True when the decisions run <paramref name="testClass"/> of <paramref name="project"/>.</summary>
    /// <param name="tests">TestDetta's decision per affected test project.</param>
    /// <param name="project">Normalized test project path.</param>
    /// <param name="testClass">Runtime test class name.</param>
    public static bool IsSelected(IReadOnlyList<TestProjectSelection> tests, string project, string testClass)
    {
        var selection = tests.FirstOrDefault(t => string.Equals(t.Project.Path, project, StringComparison.Ordinal));

        // A project that does not build fails dotnet test as soon as anything of it runs.
        if (testClass == GeneratorMutants.DoesNotBuild)
        {
            return selection is { Mode: TestRunMode.All } or { Mode: TestRunMode.Filtered, TestClasses.Count: > 0 };
        }

        return Selections.Runs(selection, testClass);
    }

    /// <summary>
    /// Deletes what test runs left in the clone since <paramref name="untracked"/> was taken, so
    /// the next analysis sees only the commit and its own edit (<see cref="EvalGit.RemoveUntracked"/>).
    /// </summary>
    /// <param name="untracked">Untracked files before the runs.</param>
    public void RemoveLeftBehind(IReadOnlySet<string> untracked)
    {
        var removed = workspace.Git.RemoveUntracked(untracked);
        if (removed.Count == 0 || !_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var files = string.Join(", ", removed.Take(5));
        LogLeftBehind(removed.Count, files);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} file(s) test runs left in the clone: {Files}")]
    private partial void LogLeftBehind(int count, string files);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental step for {Project}: {Kind}, {Recorded} of {Total} class(es) recorded")]
    private partial void LogIncrementalStep(string project, TestDetta.Cli.StepKind kind, int recorded, int total);

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
