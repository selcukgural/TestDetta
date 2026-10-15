using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>The test classes of one test project that read a changed file no project owns.</summary>
/// <param name="Project">The test project.</param>
/// <param name="WholeProject">Why the whole project runs (a read outside the tests, in a fixture or while initializing), or null.</param>
/// <param name="Classes">Reading classes, each with its evidence.</param>
internal sealed record FileReaders(ProjectNode Project, string? WholeProject, SortedDictionary<string, string> Classes);

/// <summary>A project that references another without compiling against it (not as a source generator).</summary>
/// <param name="Consumer">The referencing project.</param>
/// <param name="Referenced">The affected project it references.</param>
/// <param name="BuildUse">How the consumer's build uses the output (<see cref="BuildOutputUse"/>), or null when it does not.</param>
internal sealed record OutputUse(ProjectNode Consumer, ProjectNode Referenced, string? BuildUse);

/// <summary>A changed non-code file the build of the project that owns it does not use (docs/design/non-code-files.md).</summary>
/// <param name="File">Path relative to the repository root.</param>
/// <param name="Owner">The project that owns it.</param>
internal sealed record InertFile(string File, ProjectNode Owner);

/// <summary>What <c>FindProjectLevelChanges</c> leaves to later steps instead of running a project completely.</summary>
/// <param name="Generators">Projects that use an affected project as a source generator, for <c>CompareGeneratorOutput</c>.</param>
/// <param name="Outputs">
/// Projects that reference an affected project without compiling against it and whose build does not use its
/// output (<see cref="BuildOutputUse"/>), for <c>AddOutputUsers</c>.
/// </param>
/// <param name="NonCode">Tells whether the build uses a non-code file, under the readers rule; null under the project rule.</param>
internal sealed record ProjectLevelUses(List<GeneratorUse> Generators, List<OutputUse> Outputs, NonCodeFileUses? NonCode)
{
    /// <summary>Changed non-code files the build does not use, which run their readers instead of their projects.</summary>
    public List<InertFile> InertFiles { get; } = [];

    /// <summary>
    /// Affected projects whose build output cannot have changed: every changed file that reaches them, directly or
    /// through a reference, is an inert file. Their generator consumers, the projects that reference them without
    /// compiling against them and the tests that load their output have nothing new to see.
    /// </summary>
    public HashSet<string> UnchangedOutputs { get; } = new(Paths.Comparer);
}

public sealed partial class TestSelector
{
    /// <summary>
    /// The readers of changed files that are no code (docs/design/runtime-file-reads.md,
    /// docs/design/non-code-files.md): per test project whose map recorded file reads, the classes that
    /// read such a file or listed its directory. A read outside the tests, in a fixture's setup or while
    /// initializing something shared runs the whole project. A file nobody read selects nothing here, and
    /// a map recorded before reads were knows none; what else a project-owned file runs is decided at
    /// project level.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="files">The changed files no project owns and the non-code files projects own, repository-relative.</param>
    private Dictionary<string, FileReaders> FindFileReaders(TestSelectionRequest request, IReadOnlyList<string> files)
    {
        var readers = new Dictionary<string, FileReaders>(Paths.Comparer);
        var unmapped = files;
        if (unmapped.Count == 0)
        {
            return readers;
        }

        foreach (var (path, map) in request.CoverageMaps)
        {
            if (map.FileReads is not { } reads || request.Graph.Find(path) is not { } project)
            {
                continue;
            }

            var commit = map.Commit[..Math.Min(12, map.Commit.Length)];
            string? whole = null;
            foreach (var (place, paths) in (ReadOnlySpan<(string, IReadOnlyList<string>)>)[
                ("outside its tests", reads.Ambient),
                ("while initializing what its tests share", reads.Initialization),
                .. reads.Fixtures.Select(static f => ($"in the setup of {f.Key}", f.Value))])
            {
                if (FirstRead(paths, unmapped) is { } file)
                {
                    whole = $"it read {file} {place} (map {commit})";
                    break;
                }
            }

            var classes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (testClass, paths) in reads.Classes)
            {
                if (FirstRead(paths, unmapped) is { } file)
                {
                    classes[testClass] = $"read {file} (map {commit})";
                }
            }

            if (whole is not null || classes.Count > 0)
            {
                LogFileReaders(path, classes.Count, whole ?? "-");
                readers[path] = new FileReaders(project, whole, classes);
            }
        }

        return readers;
    }

    /// <summary>
    /// The files of <paramref name="unmapped"/> that some map recorded a read of, anywhere; null when no
    /// map recorded reads.
    /// </summary>
    /// <param name="unmapped">Changed files outside every project, repository-relative.</param>
    /// <param name="maps">Coverage maps by test project.</param>
    internal static IReadOnlyList<string>? FilesRead(IReadOnlyList<string> unmapped, IReadOnlyDictionary<string, CoverageMap> maps)
    {
        var reads = maps.Values.Select(static m => m.FileReads).OfType<FileReads>().ToList();
        if (reads.Count == 0)
        {
            return null;
        }

        var all = reads.SelectMany(static r => r.Classes.Values.Concat(r.Fixtures.Values).Append(r.Ambient).Append(r.Initialization)).ToList();
        return [.. unmapped.Where(f => all.Exists(paths => FirstRead(paths, [f]) is not null))];
    }

    /// <summary>The first of <paramref name="files"/> that one of <paramref name="reads"/> names, itself or through a listed directory above it; null for none.</summary>
    /// <param name="reads">Recorded reads; a directory ends in '/'.</param>
    /// <param name="files">Changed files, repository-relative.</param>
    private static string? FirstRead(IReadOnlyList<string> reads, IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            foreach (var read in reads)
            {
                if (string.Equals(read, file, Paths.Comparison) || (read.EndsWith('/') && file.StartsWith(read, Paths.Comparison)))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="decisions"/> with the readers of changed files added: a project that runs
    /// completely stays so; one that runs some classes, or none, runs the readers as well; a project the
    /// changes did not affect otherwise is added with its readers.
    /// </summary>
    /// <param name="decisions">Decisions per affected test project.</param>
    /// <param name="readers">Readers per test project.</param>
    /// <param name="maps">Coverage maps, for the classes a reader project skips.</param>
    private static List<TestProjectSelection> WithFileReaders(List<TestProjectSelection> decisions, Dictionary<string, FileReaders> readers, IReadOnlyDictionary<string, CoverageMap> maps)
    {
        var result = new List<TestProjectSelection>(decisions.Count + readers.Count);
        var seen = new HashSet<string>(Paths.Comparer);
        foreach (var decision in decisions)
        {
            seen.Add(decision.Project.Path);
            result.Add(readers.TryGetValue(decision.Project.Path, out var reading) ? Merge(decision, reading) : decision);
        }

        foreach (var (path, reading) in readers.Where(r => !seen.Contains(r.Key)))
        {
            var unaffected = new TestProjectSelection(reading.Project, TestRunMode.Skipped, [], "nothing it compiles changed")
            {
                Decisions = [.. (maps.TryGetValue(path, out var map) ? map.Classes.Keys : [])
                    .Order(StringComparer.Ordinal)
                    .Select(static c => new ClassDecision(c, Selected: false, ClassDecisionKind.Skipped, "read none of the changed files"))],
            };
            result.Add(Merge(unaffected, reading));
        }

        result.Sort(static (a, b) => string.Compare(a.Project.Path, b.Project.Path, StringComparison.Ordinal));
        return result;
    }

    /// <summary>One decision with a project's readers of changed files added.</summary>
    /// <param name="decision">The decision without them.</param>
    /// <param name="reading">The readers.</param>
    private static TestProjectSelection Merge(TestProjectSelection decision, FileReaders reading)
    {
        if (decision.Mode == TestRunMode.All)
        {
            return decision;
        }

        if (reading.WholeProject is { } why)
        {
            return decision with
            {
                Mode = TestRunMode.All,
                TestClasses = [],
                Reason = why,
                Decisions = [.. decision.Decisions.Select(d => d with { Selected = true, Kind = ClassDecisionKind.WholeProject, Evidence = why })],
            };
        }

        var classes = decision.TestClasses.Union(reading.Classes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var decisions = decision.Decisions.ToDictionary(static d => d.TestClass, StringComparer.Ordinal);
        foreach (var (testClass, evidence) in reading.Classes)
        {
            if (!decisions.TryGetValue(testClass, out var existing) || !existing.Selected)
            {
                decisions[testClass] = new ClassDecision(testClass, Selected: true, ClassDecisionKind.FileRead, evidence);
            }
        }

        var reason = decision.Mode == TestRunMode.Filtered
            ? $"{decision.Reason}; {reading.Classes.Count} test class(es) read a changed file"
            : $"{reading.Classes.Count} test class(es) read a changed file";
        return decision with
        {
            Mode = TestRunMode.Filtered,
            TestClasses = classes,
            Reason = reason,
            Decisions = [.. decisions.Values.OrderBy(static d => d.TestClass, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// The tests that may use the output of a project referenced without compiling against it
    /// (docs/design/runtime-file-reads.md): in every test project at or below the consumer, the classes
    /// whose map recorded starting, loading or reading a file named after the referenced project. When
    /// the consumer's build uses the output, the test project is built first and what the build fed the
    /// consumer's compiler from outside git is compared with its map: only an equal set leaves the
    /// output a file to run. A use outside the tests, while initializing or in a fixture's setup runs
    /// the whole project, and so does a test project whose map does not record uses or that has no map.
    /// They join the readers of changed files.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="uses">Consumers and the affected projects they reference this way.</param>
    /// <param name="projectLevel">Projects that run completely; updated in place.</param>
    /// <param name="readers">Readers per test project; updated in place.</param>
    private void AddOutputUsers(TestSelectionRequest request, List<OutputUse> uses, Dictionary<string, string> projectLevel, Dictionary<string, FileReaders> readers)
    {
        foreach (var (consumer, referenced, buildUse) in uses)
        {
            var output = Path.GetFileName(referenced.Path);
            string[] names = [referenced.AssemblyName, Path.GetFileNameWithoutExtension(referenced.Path)];
            foreach (var path in request.Graph.WithDependents([consumer.Path]))
            {
                if (request.Graph.Find(path) is not { IsTestProject: true } project || projectLevel.ContainsKey(path))
                {
                    continue;
                }

                if (!request.CoverageMaps.TryGetValue(path, out var map) || map.OutputUses is not { } recorded)
                {
                    AddFallback(projectLevel, path, $"it may start or load the output of {output}, which is affected, and no map says which classes do");
                    continue;
                }

                if (buildUse is not null && BuildInputsDiffer(request, project, map, consumer) is { } differ)
                {
                    AddFallback(projectLevel, path, $"{Path.GetFileName(consumer.Path)} uses {output}, which is affected, in its build ({buildUse}), and {differ}");
                    continue;
                }

                // Only a map that tracked every assembly load can say no other class loaded it (docs/design/loaded-outputs.md).
                if (UnseenLoads(map, names) is { } unseen)
                {
                    AddFallback(projectLevel, path, $"it may start or load the output of {output}, which is affected, and {unseen}");
                    continue;
                }

                AddUsersOf(path, project, map, recorded, output, WithScans(names), "started or loaded the output of", readers);
            }
        }
    }

    /// <summary>Adds <paramref name="added"/> to the readers of its test project, keeping the first reason and evidence of each.</summary>
    /// <param name="readers">Readers per test project; updated in place.</param>
    /// <param name="path">The test project's path.</param>
    /// <param name="added">Readers to add.</param>
    private static void AddReaders(Dictionary<string, FileReaders> readers, string path, FileReaders added) =>
        readers[path] = readers.TryGetValue(path, out var existing)
            ? existing with
            {
                WholeProject = existing.WholeProject ?? added.WholeProject,
                Classes = new SortedDictionary<string, string>(existing.Classes.Concat(added.Classes.Where(c => !existing.Classes.ContainsKey(c.Key))).ToDictionary(), StringComparer.Ordinal),
            }
            : added;

    /// <summary>
    /// <paramref name="names"/> and the same names as a scan of the loaded assemblies (<see cref="CoverageMap.OutputScanPrefix"/>)
    /// and a load by name (<see cref="CoverageMap.OutputNameLoadPrefix"/>) record them: a class that enumerated the
    /// loaded assemblies, or loaded one by its name, may have found an output's types there, as a plugin host does. Only
    /// the rules for outputs found at run time count them; a project the tests compile against is loaded by every class
    /// that names it, repository code scans for its own converters while initializing (Newtonsoft.Json), and a module
    /// catalog loads every library of the dependency context by name (Carter) (docs/design/loaded-outputs.md).
    /// </summary>
    /// <param name="names">An output's assembly and project names.</param>
    internal static string[] WithScans(string[] names) =>
        [.. names, .. names.Select(static n => CoverageMap.OutputScanPrefix + n), .. names.Select(static n => CoverageMap.OutputNameLoadPrefix + n)];

    /// <summary>
    /// Why <paramref name="map"/>'s output uses may miss a class that loaded <paramref name="names"/>, or null when they
    /// cannot: the map was recorded before assembly loads were tracked, or a test process loaded the output without a
    /// call the recording saw (a third-party plugin host, the runtime, before the tracker started).
    /// </summary>
    /// <param name="map">A test project's coverage map.</param>
    /// <param name="names">The output's assembly and project names.</param>
    private static string? UnseenLoads(CoverageMap map, string[] names) =>
        map.KnowsLoadsOf(names) ? null
        : map.OutputLoadsUnseen is null ? "its map was recorded before TestDetta tracked assembly loads (record it again to narrow)"
        : "a test process loaded it without a call the recording saw, so the map cannot say which classes used it";

    /// <summary>
    /// The tests that load the output of a changed project from its path (<c>Assembly.LoadFrom</c>, a
    /// process start, a read of the file in a build output), which they may inspect anywhere through
    /// reflection: a public API baseline test reads every public declaration and static field value
    /// without running the code (AspNetCoreOData's PublicApiTest). The project's own coverage cannot say
    /// which of its changes such a test sees, so every class whose map records the use is selected, or
    /// the whole project when the use came outside the tests, while initializing or in a fixture's setup.
    /// Projects referenced without compiling against them are <see cref="AddOutputUsers"/>' part.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="projectLevel">Projects that run completely; left out.</param>
    /// <param name="readers">Readers per test project; updated in place.</param>
    /// <param name="unchangedOutputs">Projects whose output cannot have changed; see <see cref="ProjectLevelUses.UnchangedOutputs"/>.</param>
    private void AddLoadedOutputUsers(TestSelectionRequest request, Dictionary<string, string> projectLevel, Dictionary<string, FileReaders> readers, HashSet<string> unchangedOutputs)
    {
        var changed = request.Impact.Affected.Where(a => a.Reason == ImpactReason.DirectChange && !unchangedOutputs.Contains(a.Project.Path)).Select(static a => a.Project).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var (testProject, _) in request.Impact.Tests)
        {
            var path = testProject.Path;
            if (projectLevel.ContainsKey(path) || !request.CoverageMaps.TryGetValue(path, out var map) || map.OutputUses is not { } recorded)
            {
                continue;
            }

            foreach (var project in changed)
            {
                // The test host and its framework read a test project's own output outside every test to discover and
                // run them (NUnit's adapter, the dependency context and its deps.json; MimeKit, 2026-10-15). That read
                // inspects nothing a change could fail, and the project's own changes are answered by its own coverage,
                // so it does not run the project completely; a class that loads the output itself still runs.
                var uses = Paths.Comparer.Equals(project.Path, path) ? recorded with { Ambient = [] } : recorded;
                string[] names = [project.AssemblyName, Path.GetFileNameWithoutExtension(project.Path)];
                AddUsersOf(path, testProject, map, uses, Path.GetFileName(project.Path), names, "loaded the output of", readers);
            }
        }
    }

    /// <summary>
    /// Adds to <paramref name="readers"/> the classes of <paramref name="testProject"/> whose map recorded a use
    /// of a file named <paramref name="names"/>, or the whole project for a use outside its tests, while
    /// initializing or in a fixture's setup.
    /// </summary>
    /// <param name="path">Normalized test project path.</param>
    /// <param name="testProject">The test project.</param>
    /// <param name="map">Its coverage map.</param>
    /// <param name="recorded">The map's uses.</param>
    /// <param name="output">The affected project's file name, for the evidence.</param>
    /// <param name="names">Names a use of its output carries.</param>
    /// <param name="verb">How the evidence describes the use.</param>
    /// <param name="readers">Readers per test project; updated in place.</param>
    private void AddUsersOf(string path, ProjectNode testProject, CoverageMap map, FileReads recorded, string output, string[] names, string verb, Dictionary<string, FileReaders> readers)
    {
        var commit = map.Commit[..Math.Min(12, map.Commit.Length)];
        string? whole = null;
        foreach (var (place, used) in (ReadOnlySpan<(string, IReadOnlyList<string>)>)[
            ("outside its tests", recorded.Ambient),
            ("while initializing what its tests share", recorded.Initialization),
            .. recorded.Fixtures.Select(static f => ($"in the setup of {f.Key}", f.Value))])
        {
            if (Uses(used, names))
            {
                whole = $"it {verb} {output} {place} (map {commit})";
                break;
            }
        }

        var classes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (testClass, used) in recorded.Classes)
        {
            if (Uses(used, names))
            {
                classes[testClass] = $"{verb} {output}, which is affected (map {commit})";
            }
        }

        LogOutputUsers(path, output, classes.Count, whole ?? "-");
        if (whole is null && classes.Count == 0)
        {
            return;
        }

        AddReaders(readers, path, new FileReaders(testProject, whole, classes));

        static bool Uses(IReadOnlyList<string> used, string[] names) =>
            used.Any(u => names.Any(n => string.Equals(u, n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Why the build of <paramref name="consumer"/> in <paramref name="testProject"/>'s output may differ
    /// from its recording, or null when what the build fed its compiler from outside git is the same
    /// now in every framework the tests run for: a current build of the test project is read as for a
    /// changed generator (docs/design/build-inputs-per-framework.md). A build that fails ends the analysis,
    /// since <c>dotnet test</c> would fail the same way.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="testProject">Test project at or below the consumer.</param>
    /// <param name="map">Its coverage map.</param>
    /// <param name="consumer">The project whose build uses the output.</param>
    private string? BuildInputsDiffer(TestSelectionRequest request, ProjectNode testProject, CoverageMap map, ProjectNode consumer)
    {
        var key = Path.GetRelativePath(request.RepoRoot, consumer.Path).Replace('\\', '/');
        if (map.BuildInputs is null)
        {
            return "its map does not record what that build fed the compiler";
        }

        // dotnet test runs every framework unless -f names one, and another framework's build may compile
        // other files: each framework that runs is compared with its own recorded build.
        var singleFramework = map.Framework is null && testProject.SymbolSets.Count <= 1;
        var frameworks = new List<string?>();
        if (request.Framework is { } run)
        {
            frameworks.Add(run);
        }
        else if (singleFramework)
        {
            frameworks.Add(null);
        }
        else
        {
            frameworks.AddRange(map.GeneratedFrameworks ?? []);
        }

        if (frameworks.Count == 0)
        {
            return $"the test project targets several frameworks, and its map holds the build of {map.Framework ?? "one"} only (record it without -f, or run the tests with -f {map.Framework ?? "<framework>"})";
        }

        if (request.GeneratedCode is not { } reader)
        {
            return "nothing builds it here to compare";
        }

        foreach (var framework in frameworks)
        {
            // "the build" for a project with one framework, "the net8.0 build" for one of several.
            var build = singleFramework || framework is null ? "the build" : $"the {framework} build";
            var recorded = singleFramework || framework is null || string.Equals(framework, map.Framework, StringComparison.OrdinalIgnoreCase)
                ? map.BuildInputs
                : map.FrameworkBuildInputs?.GetValueOrDefault(framework);
            if (recorded?.GetValueOrDefault(key) is not { } before)
            {
                return $"its map does not record what {build} fed the compiler (record it without -f)";
            }

            LogComparingBuildInputs(testProject.Path, consumer.Path, framework ?? "its only framework");
            var read = reader.Read(request.Graph, request.RepoRoot, testProject, singleFramework ? null : framework);
            if (read.BuildErrors is { } errors)
            {
                throw new GeneratedCodeBuildException(
                    $"Building {Path.GetFileName(testProject.Path)} to see what its build feeds {Path.GetFileName(consumer.Path)} failed; dotnet test would fail the same way:{Environment.NewLine}{errors}");
            }

            if (read.BuildInputs?.GetValueOrDefault(key) is not { } now)
            {
                return read.Unavailable ?? (singleFramework || framework is null ? "its current build could not be read" : $"its current {framework} build could not be read");
            }

            var differing = before.Keys.Union(now.Keys, StringComparer.Ordinal)
                .Where(k => !string.Equals(before.GetValueOrDefault(k), now.GetValueOrDefault(k), StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (differing.Count > 0)
            {
                return $"what {build} fed its compiler changed: {string.Join(", ", differing.Take(5))}{(differing.Count > 5 ? ", ..." : string.Empty)}";
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{TestProject}: building it ({Framework}) to compare what the build feeds {Consumer}'s compiler from outside git with its map")]
    private partial void LogComparingBuildInputs(string testProject, string consumer, string framework);

    [LoggerMessage(Level = LogLevel.Information, Message = "{TestProject}: {Classes} test class(es) started or loaded the output of {Output}; whole project: {WholeProject}")]
    private partial void LogOutputUsers(string testProject, string output, int classes, string wholeProject);

    [LoggerMessage(Level = LogLevel.Information, Message = "{TestProject}: {Classes} test class(es) read a changed file that no project owns; whole project: {WholeProject}")]
    private partial void LogFileReaders(string testProject, int classes, string wholeProject);
}
