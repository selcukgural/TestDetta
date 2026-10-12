using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Coverage;

/// <summary>What <see cref="CoverageRecorder.Record"/> should record.</summary>
/// <param name="RepoRoot">Repository root directory.</param>
/// <param name="Graph">Projects of the repository.</param>
/// <param name="TestProject">Test project to run.</param>
/// <param name="Options">How to build and run the tests.</param>
public sealed record RecordRequest(string RepoRoot, ProjectGraph Graph, ProjectNode TestProject, RecordOptions Options)
{
    /// <summary>Build configuration.</summary>
    public string Configuration => Options.Configuration;

    /// <summary>Test host processes to spread the test classes over.</summary>
    public int Parallelism => Options.Parallelism;
}

/// <summary>How <see cref="CoverageRecorder"/> builds and runs a test project.</summary>
/// <param name="Configuration">Build configuration, such as <c>Debug</c>.</param>
/// <param name="Framework">
/// Target framework to record, such as <c>net10.0</c>; null takes the project's only framework, or
/// the first one of a multi-targeted project.
/// </param>
/// <param name="Parallelism">
/// Number of test host processes to spread the test classes over. Each process still runs its
/// tests one at a time, which is what keeps attribution exact; the suite must tolerate several
/// processes at once (no fixed ports or shared local databases).
/// </param>
/// <param name="Timeout">Longest a test process may run before it is killed; null waits forever.</param>
public sealed record RecordOptions(string Configuration = "Debug", string? Framework = null, int Parallelism = 1, TimeSpan? Timeout = null)
{
    /// <summary>
    /// True to use the existing build output instead of building, when a pipeline built with the
    /// same configuration just before. The output must have its PDBs for generated code to be read.
    /// </summary>
    public bool NoBuild { get; init; }

    /// <summary>
    /// Test classes (runtime names) to record, for an incremental step (docs/design/incremental-recording.md); null
    /// records every class. Tests outside every class the source index knows are recorded as well, since the selection
    /// cannot have judged them.
    /// </summary>
    public IReadOnlyCollection<string>? Classes { get; init; }

    /// <summary>
    /// The team's own <c>dotnet test</c> arguments (<c>td record -- …</c>, the action's <c>dotnet-test-args</c>): recording runs
    /// under its filter and settings as well as the project's, with its other options (docs/design/team-test-configuration.md).
    /// </summary>
    public IReadOnlyList<string> DotnetTestArguments { get; init; } = [];
}

/// <summary>A recording of some classes of a test project, for an incremental step.</summary>
/// <param name="Map">What the recorded classes ran, at the current commit.</param>
/// <param name="TestsPassed">True when every recorded test passed.</param>
/// <param name="AssemblyClasses">Every class the test assembly's PDB names; empty when it could not be read.</param>
/// <param name="Complete">
/// True when every test ran after all: the filtered run crashed and the project was recorded the way it normally runs.
/// The map is then a full recording.
/// </param>
public sealed record PartialRecording(CoverageMap Map, bool TestsPassed, IReadOnlySet<string> AssemblyClasses, bool Complete);

/// <summary>
/// Builds a test project, instruments the repository's assemblies in its output, runs the tests
/// with <see cref="TestDettaInProcCollector"/> attached and turns the result into a <see cref="CoverageMap"/>.
/// The original assemblies are always restored.
/// </summary>
/// <param name="loggerFactory">Creates the loggers of the steps.</param>
public sealed partial class CoverageRecorder(ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CoverageRecorder>();

    /// <summary>
    /// Assembly file names of the last graph recorded for: every test project of a repository needs
    /// the same set, and evaluating names may start MSBuild.
    /// </summary>
    private (ProjectGraph Graph, Dictionary<string, List<ProjectNode>> Owners)? _assemblyNames;

    /// <summary>Records the coverage map of one test project.</summary>
    /// <param name="request">What to record.</param>
    /// <param name="output">Receives build and test output.</param>
    /// <returns>The map, and whether all tests passed while recording.</returns>
    /// <exception cref="InvalidOperationException">The build failed.</exception>
    /// <exception cref="NoTestsRecordedException">No test ran, so nothing was recorded.</exception>
    public (CoverageMap Map, bool TestsPassed) Record(RecordRequest request, TextWriter output)
    {
        var recording = RecordShards(request with { Options = request.Options with { Classes = null } }, output);
        return (recording.Map, recording.TestsPassed);
    }

    /// <summary>Records only <see cref="RecordOptions.Classes"/> of one test project, for an incremental step.</summary>
    /// <param name="request">What to record; its options name the classes.</param>
    /// <param name="output">Receives build and test output.</param>
    /// <exception cref="InvalidOperationException">The build failed.</exception>
    /// <exception cref="NoTestsRecordedException">No test ran, so nothing was recorded.</exception>
    public PartialRecording RecordClasses(RecordRequest request, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(request.Options.Classes);
        return RecordShards(request, output);
    }

    /// <summary>Builds, instruments and runs the test project, all of it or the classes its options name.</summary>
    /// <param name="request">What to record.</param>
    /// <param name="output">Receives build and test output.</param>
    private PartialRecording RecordShards(RecordRequest request, TextWriter output)
    {
        var dotnet = new DotnetProcess(_logger);
        var project = request.TestProject.Path;
        var root = request.RepoRoot;

        // Everything runs where the project's global.json is, as dotnet test would for a developer
        // there: the SDK it pins and the runner it selects (DotnetTestLocation).
        var location = DotnetTestLocation.For(root, project);
        var runDirectory = location.FullDirectory(root);

        List<string> build = ["build", project, "-c", request.Configuration];
        if (request.Options.Framework is { } targetFramework)
        {
            build.AddRange(["-f", targetFramework]);
        }

        // A legacy (non-SDK) project, or one it references, restores and builds only with the full
        // MSBuild; its tests then run from the compiled assembly (docs/design/legacy-projects.md).
        var legacy = request.Graph.NeedsFullMsBuild(request.TestProject);
        if (!request.Options.NoBuild)
        {
            var errors = new ErrorCollectingWriter(output);
            var buildExit = legacy
                ? LegacyBuilder(request).Build(root, project, request.Configuration, errors)
                : dotnet.Run(build, runDirectory, new Dictionary<string, string>(), errors);
            if (buildExit != 0)
            {
                var reason = errors.Errors.Count == 0 ? string.Empty : ": " + string.Join(" / ", errors.Errors);
                throw new InvalidOperationException($"Building {Path.GetFileName(project)} failed with exit code {buildExit}{reason}");
            }
        }

        var (targetPath, framework) = FindTargetPath(dotnet, request, runDirectory);
        if (request.Options.NoBuild && !File.Exists(targetPath))
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(project)} has no build output at {targetPath}; build it with -c {request.Configuration} first, or drop --no-build.");
        }
        var outputDirectory = Path.GetDirectoryName(targetPath)!;
        var owners = GeneratedSourceReader.CompiledAgainst(request.Graph, request.TestProject, AssemblyOwners(request));
        var assemblies = GeneratedSourceReader.RepositoryAssemblies(owners.Keys, outputDirectory);
        if (!assemblies.Contains(targetPath, StringComparer.OrdinalIgnoreCase))
        {
            // Test classes mark themselves through their instrumented entry points: without the test assembly among
            // the instrumented ones, the map would know no class and list every one as failed.
            throw new RecordingFailedException(
                $"its test assembly {Path.GetFileName(targetPath)} is not among the repository assemblies TestDetta instruments in its output " +
                "(no project the test project compiles against owns that file name), so no test class could be recorded; the tests were not run");
        }

        var generated = ReadGenerated(request, targetPath, owners, assemblies, framework);
        // Read before instrumentation rewrites the assemblies and their PDBs, as the generated code is.
        var tracked = new GitClient(loggerFactory.CreateLogger<GitClient>()).TrackedFiles(request.RepoRoot).ToHashSet(StringComparer.Ordinal);
        var buildInputs = GeneratedSourceReader.ReadBuildInputs(request.RepoRoot, request.TestProject, targetPath, owners, assemblies, tracked);
        IReadOnlyList<string>? generatedFrameworks = null;
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? frameworkBuildInputs = null;
        if (framework is not null && request.Options.Framework is null)
        {
            var others = ReadOtherFrameworks(dotnet, request, runDirectory, new RecordedBuild(framework, owners, tracked), generated);
            (generated, generatedFrameworks) = (others.Generated, others.Frameworks);
            frameworkBuildInputs = others.Frameworks is null ? null : others.BuildInputs;
        }
        else if (framework is not null && TargetFrameworks(dotnet, request, runDirectory) is [] or [_])
        {
            // -f names the project's only framework: its generated code is all there is.
            generatedFrameworks = [framework];
        }

        var locations = ClassLocationReader.Read(targetPath, request.RepoRoot);
        LogAssemblies(assemblies.Count, outputDirectory);

        var work = Path.Combine(Path.GetTempPath(), "testdetta-record-" + Guid.NewGuid().ToString("N"));
        var instrumenter = new AssemblyInstrumenter(_logger);
        var testAssemblies = new HashSet<string>(StringComparer.Ordinal) { targetPath };
        var thirdParty = ThirdPartyAssemblies.Find(outputDirectory, AssemblyOwners(request).Keys);
        // Loads by name in code that also runs outside the tests are the product finding its modules (docs/design/loaded-outputs.md).
        var productAssemblies = assemblies
            .Where(a => owners.TryGetValue(Path.GetFileName(a), out var owner) && request.Graph.RunsOutsideTests(owner.Path))
            .ToHashSet(StringComparer.Ordinal);
        var instrumentation = instrumenter.Instrument(assemblies, testAssemblies, Path.Combine(work, "backup"), thirdParty, productAssemblies);
        var mode = legacy ? DotnetTestMode.VSTest : location.Mode;
        RuntimeDeployment? deployment = null;

        try
        {
            // Under Microsoft.Testing.Platform the tests run in the test project's own executable,
            // where no VSTest collector loads the runtime; it goes into the output instead.
            // The runtime always goes into the output, so instrumented code finds it without the VSTest
            // collector: xUnit v3 runs tests in the test executable, where no collector loads, and old
            // test hosts (Microsoft.NET.Test.Sdk 16) do not load the collector at all.
            deployment = new RuntimeDeployment(targetPath, assemblies);
            var testArguments = TestArguments(new TestRun(request, mode, targetPath, framework) { Legacy = legacy }, work);
            LogMode(project, mode);

            // Told by the runner's assembly in the output, which also catches a framework the project
            // gets through another package.
            var binaries = Path.GetDirectoryName(targetPath)!;
            var syntax = File.Exists(Path.Combine(binaries, "TUnit.Engine.dll")) ? TestFilterSyntax.TUnit
                : mode == DotnetTestMode.TestingPlatform && File.Exists(Path.Combine(binaries, "xunit.v3.core.dll")) ? TestFilterSyntax.XunitV3
                : TestFilterSyntax.VSTest;
            var shards = Shards(request, syntax);
            var synchronizedOutput = TextWriter.Synchronized(output);
            var runs = RunShards(shards, new ShardContext(request, mode, runDirectory, outputDirectory, instrumentation, work), testArguments, synchronizedOutput);
            ThrowIfTimedOut(runs, project, request);
            ThrowIfTrackerSilent(runs, project);
            var complete = request.Options.Classes is null;

            // A shard that crashed before its process wrote results (NLog: a subset of its classes takes
            // the host down when they run without the others) would cost the project its whole map.
            // The suite passes in one process, so the project is recorded again the way dotnet test runs it.
            if (shards.Count > 1 && runs.Any(static r => r.Result.ResultsFiles.Count == 0))
            {
                // Recorded unfiltered, also in an incremental step: the result is then a full recording.
                complete = true;
                LogShardCrashed(project);
                var single = Path.Combine(work, "single");
                Directory.CreateDirectory(single);
                runs = RunShards([[]], new ShardContext(request, mode, runDirectory, outputDirectory, instrumentation, single), testArguments, synchronizedOutput);
                ThrowIfTimedOut(runs, project, request);
            }

            // Some suites only pass with their tests in parallel (NLog's host runs out of memory when its
            // classes run one at a time). They are recorded the way they normally run: code inside a test
            // flow still counts for its class, code outside every flow counts as ambient, and class times
            // mean nothing, so the map has none.
            var suiteParallelism = false;
            if (runs.Any(static r => r.Result.ResultsFiles.Count == 0))
            {
                LogSequentialCrashed(project);
                var parallel = Path.Combine(work, "parallel");
                Directory.CreateDirectory(parallel);
                var parallelArguments = TestArguments(new TestRun(request, mode, targetPath, framework, SuiteParallelism: true) { Legacy = legacy }, parallel);
                runs = RunShards([[]], new ShardContext(request, mode, runDirectory, outputDirectory, instrumentation, parallel, SuiteParallelism: true), parallelArguments, synchronizedOutput);
                ThrowIfTimedOut(runs, project, request);
                suiteParallelism = true;
                complete = true;
            }

            var resultsFiles = new List<string>(runs.Length);
            foreach (var run in runs)
            {
                if (run.Result.ResultsFiles.Count == 0 && run.Result.Report is { Total: > 0 } ran)
                {
                    // The report says tests ran: this is no project without tests, and the map would be empty.
                    throw new RecordingFailedException(
                        $"its tests ran ({ran.Total}, exit code {run.Result.ExitCode}), but TestDetta's tracker wrote no coverage results, also when run again " +
                        "in one process and with the suite's own parallelism: the test host crashed before writing them, or never loaded the tracker")
                    {
                        TestsPassed = run.Result.ExitCode == 0 && ran.FailedClasses.Count == 0,
                    };
                }

                if (run.Result.ResultsFiles.Count == 0)
                {
                    throw new NoTestsRecordedException(
                        $"No test ran in {Path.GetFileName(project)} (exit code {run.Result.ExitCode}), so it has no coverage map. Is it a test project?");
                }

                resultsFiles.AddRange(run.Result.ResultsFiles);
            }

            var reports = runs.Select(static r => r.Result.Report).ToList();
            if (reports.All(static r => r is { Total: 0 }))
            {
                throw new NoTestsRecordedException($"No test ran in {Path.GetFileName(project)}, so it has no coverage map.");
            }

            var map = BuildMap(request, instrumentation.Methods, resultsFiles);
            ThrowIfNoClassRecorded(runs, map, project);
            map = map with { FailedClasses = Untrusted(runs, map) };
            map = map with
            {
                SkippedClasses = Skipped(runs, map),
                Platform = RuntimeInformation.RuntimeIdentifier,
                Framework = framework,
            };
            map = map with
            {
                ClassLocations = Reported(map, locations),
                Generated = generated,
                GeneratedFrameworks = generatedFrameworks,
                BuildInputs = buildInputs,
                FrameworkBuildInputs = frameworkBuildInputs,
                InstrumentedProjects = InstrumentedProjects(request.RepoRoot, owners, assemblies),
                FileReadsUnseen = instrumentation.ReadsUnseen,
                DataSourceUsers = instrumentation.DataSourceUsers.Count > 0 ? instrumentation.DataSourceUsers : null,
                LeakedReads = LeakedReads(resultsFiles, instrumentation.Fields),
                OutputLoadsUnseen = LoadsUnseen(resultsFiles),
                Timings = suiteParallelism ? null : map.Timings,
                ExternalCallbacks = instrumentation.ExternalCallbacks,
                NestedMethods = instrumentation.NestedMethods,
            };
            LogFailedClasses(project, map.FailedClasses?.Count ?? -1);
            LogSkippedClasses(project, map.SkippedClasses?.Count ?? -1, map.ClassLocations?.Count ?? 0);
            return new PartialRecording(map, runs.All(static r => r.Result.ExitCode == 0), locations.Keys.ToHashSet(StringComparer.Ordinal), complete);
        }
        finally
        {
            deployment?.Dispose();
            instrumenter.Restore(instrumentation);
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// Throws when a test process ran tests and passed, but the tracker wrote no results in it: it never loaded there.
    /// That is no crash (a crash fails the run), so running again in one process or with the suite's parallelism, as
    /// for a crash, would only end in a misleading "no test ran"; the project is not recorded, with this reason.
    /// </summary>
    /// <param name="runs">Finished shards.</param>
    /// <param name="project">Test project path, for the message.</param>
    internal static void ThrowIfTrackerSilent(Task<ShardResult>[] runs, string project)
    {
        if (runs.FirstOrDefault(static r => r.Result is { ResultsFiles.Count: 0, ExitCode: 0, Report: { Total: > 0, FailedClasses.Count: 0 } }) is { } silent)
        {
            throw new RecordingFailedException(
                $"the tests of {Path.GetFileName(project)} ran and passed ({silent.Result.Report!.Total}), but TestDetta's tracker wrote no coverage results " +
                "in their test process: it did not load there")
            {
                TestsPassed = true,
            };
        }
    }

    /// <summary>
    /// Throws when the tests ran and passed but no test class was recorded: the tracker wrote results, yet no class
    /// marked itself while it ran (its entry points were not instrumented). Such a map would list every class as
    /// failed and say nothing; a map with some classes recorded keeps the rest as untrusted, as before
    /// (docs/design/unrecorded-classes.md).
    /// </summary>
    /// <param name="runs">Finished shards.</param>
    /// <param name="map">The map built from their results.</param>
    /// <param name="project">Test project path, for the message.</param>
    internal static void ThrowIfNoClassRecorded(Task<ShardResult>[] runs, CoverageMap map, string project)
    {
        if (map.Classes.Count > 0 || map.Fixtures.Count > 0 || runs.Any(static r => r.Result.ExitCode != 0 || r.Result.Report is not { FailedClasses.Count: 0 }))
        {
            return;
        }

        var executed = runs.Sum(static r => r.Result.Report!.ExecutedClasses.Count);
        if (executed > 0)
        {
            throw new RecordingFailedException(
                $"the tests of {Path.GetFileName(project)} ran and passed ({executed} test classes), but TestDetta recorded none of their classes: " +
                "the tracker wrote results, yet no test class marked itself while it ran")
            {
                TestsPassed = true,
            };
        }
    }

    /// <summary>
    /// The projects behind the instrumented assemblies, relative to the repository root and sorted, for
    /// <see cref="CoverageMap.InstrumentedProjects"/>.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="owners">Project of each repository assembly file name in the test project's build.</param>
    /// <param name="assemblies">The instrumented assemblies.</param>
    internal static List<string> InstrumentedProjects(string repoRoot, IReadOnlyDictionary<string, ProjectNode> owners, IReadOnlyList<string> assemblies) =>
        [.. assemblies
            .Select(a => owners.GetValueOrDefault(Path.GetFileName(a)))
            .OfType<ProjectNode>()
            .Select(p => Path.GetRelativePath(repoRoot, p.Path).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>Runs every shard in its own test process and waits for all of them.</summary>
    /// <param name="shards">Filters, one per shard; a single empty one runs everything.</param>
    /// <param name="context">What every shard shares.</param>
    /// <param name="testArguments"><c>dotnet test</c> arguments shared by all shards.</param>
    /// <param name="output">Shared, synchronized output.</param>
    private Task<ShardResult>[] RunShards(List<IReadOnlyList<string>> shards, ShardContext context, List<string> testArguments, TextWriter output)
    {
        var dotnet = new DotnetProcess(_logger);
        var runs = new Task<ShardResult>[shards.Count];
        for (var i = 0; i < shards.Count; i++)
        {
            // The catch-all shard usually matches nothing; under the testing platform that is exit
            // code 8, reported as an error unless the platform is told to accept it.
            var isCatchAll = shards.Count > 1 && i == shards.Count - 1;
            var arguments = isCatchAll && context.Mode == DotnetTestMode.TestingPlatform
                ? AcceptingZeroTests(testArguments, context.Request.TestProject.TestConfiguration?.PlatformArguments)
                : testArguments;
            var shard = new ShardRun(dotnet, arguments, shards[i], i, shards.Count);
            runs[i] = Task.Run(() => RunShard(shard, context, output));
        }

        Task.WaitAll(runs);
        return runs;
    }

    /// <summary>The longest path a Unix domain socket may have, without its terminating zero (macOS; Linux allows 107).</summary>
    private const int SocketPathLimit = 103;

    /// <summary>
    /// The longest name .NET gives a socket in the temporary directory: a diagnostics socket,
    /// <c>dotnet-diagnostic-&lt;pid&gt;-&lt;start time&gt;-socket</c>.
    /// </summary>
    private const int LongestSocketName = 41;

    /// <summary>
    /// <paramref name="arguments"/> with the testing platform told to accept exit code 8 (zero tests ran). The platform
    /// rejects a second <c>--ignore-exit-code</c> with exit code 5 before any test runs (docs/design/team-test-configuration.md),
    /// so a team's own option on the command line takes 8 into its list, and when the project's
    /// <c>TestingPlatformCommandLineArguments</c> has one, nothing is added: an empty shard then exits with 8 only when the
    /// team's list leaves it out, and the project is recorded as failing, which selects more rather than less.
    /// </summary>
    /// <param name="arguments">The shard's <c>dotnet test</c> arguments, the team's included.</param>
    /// <param name="platformArguments">The project's <c>TestingPlatformCommandLineArguments</c>, or null.</param>
    internal static List<string> AcceptingZeroTests(IReadOnlyList<string> arguments, string? platformArguments)
    {
        var zeroTests = TestingPlatformZeroTests.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (platformArguments is not null && platformArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsIgnoreExitCode))
        {
            return [.. arguments];
        }

        List<string> result = [.. arguments];
        var at = result.FindIndex(IsIgnoreExitCode);
        if (at < 0)
        {
            return [.. result, IgnoreExitCode, zeroTests];
        }

        // The value follows the option, or is joined to it with '=' or ':'.
        var valueAt = result[at] == IgnoreExitCode ? at + 1 : at;
        var codes = valueAt == at ? result[at][(IgnoreExitCode.Length + 1)..] : valueAt < result.Count ? result[valueAt] : "";
        if (valueAt < result.Count && !codes.Split(';').Contains(zeroTests, StringComparer.Ordinal))
        {
            result[valueAt] += ";" + zeroTests;
        }

        return result;
    }

    /// <summary>Whether <paramref name="argument"/> is the testing platform's <c>--ignore-exit-code</c>, with or without a joined value.</summary>
    /// <param name="argument">A command-line argument.</param>
    private static bool IsIgnoreExitCode(string argument) =>
        argument == IgnoreExitCode
        || (argument.StartsWith(IgnoreExitCode, StringComparison.Ordinal) && argument.Length > IgnoreExitCode.Length && argument[IgnoreExitCode.Length] is '=' or ':');

    /// <summary>The testing platform's option for exit codes to accept, which it allows once.</summary>
    private const string IgnoreExitCode = "--ignore-exit-code";

    /// <summary>Throws when a test process was killed for running too long; a hang would repeat.</summary>
    /// <param name="runs">Finished shards.</param>
    /// <param name="project">Test project path, for the message.</param>
    /// <param name="request">What is recorded, for the time limit.</param>
    private static void ThrowIfTimedOut(Task<ShardResult>[] runs, string project, RecordRequest request)
    {
        if (runs.Any(static r => r.Result.ExitCode == DotnetProcess.TimedOut))
        {
            throw new RecordingTimedOutException(
                $"A test process of {Path.GetFileName(project)} did not finish within {request.Options.Timeout?.TotalMinutes:0} min and was killed.");
        }
    }

    /// <summary>What <see cref="TestArguments"/> needs to know about the run.</summary>
    /// <param name="Request">What is recorded.</param>
    /// <param name="Mode">Runner <c>dotnet test</c> uses.</param>
    /// <param name="TargetPath">Compiled test assembly.</param>
    /// <param name="Framework">Target framework to pass, for a multi-targeted project; otherwise null.</param>
    /// <param name="SuiteParallelism">True to let the suite run its tests in parallel, as it normally does.</param>
    private sealed record TestRun(RecordRequest Request, DotnetTestMode Mode, string TargetPath, string? Framework, bool SuiteParallelism = false)
    {
        /// <summary>True when the tests run from the compiled assembly of a legacy project (<see cref="FullMsBuild"/>).</summary>
        public bool Legacy { get; init; }

        /// <summary>
        /// The team's run settings file, which TestDetta's own settings are added to (<see cref="WriteRunSettings"/>):
        /// the project's <c>RunSettingsFilePath</c> or <c>VSTestSetting</c>; null for none.
        /// </summary>
        public string? TeamSettings => Team.Settings is { } settings ? Path.GetFullPath(settings, Request.RepoRoot) : Request.TestProject.TestConfiguration?.RunSettings;

        /// <summary>The team's <c>dotnet test</c> arguments, split (<see cref="RecordOptions.DotnetTestArguments"/>).</summary>
        public TeamTestArguments Team { get; } = TeamTestArguments.Parse(Request.Options.DotnetTestArguments);
    }

    /// <summary>The full MSBuild for a legacy project, or an error that says how to provide one.</summary>
    /// <param name="request">What is being recorded.</param>
    private FullMsBuild LegacyBuilder(RecordRequest request) =>
        FullMsBuild.Locate() is { } msbuild
            ? new FullMsBuild(_logger, msbuild)
            : throw new InvalidOperationException(
                $"{Path.GetFileName(request.TestProject.Path)} is a legacy (non-SDK) project or references one, which builds only with the full MSBuild: "
                + $"install Visual Studio or its Build Tools (Windows) or Mono (macOS, Linux), or set {FullMsBuild.Variable} to an MSBuild executable.");

    /// <summary>
    /// <c>dotnet test</c> arguments shared by all shards. Tests must run one at a time: VSTest and the
    /// VSTest bridge of MSTest and NUnit take that from run settings, xUnit v3 from <c>--parallel none</c>.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="work">Work directory for the run settings file.</param>
    private static List<string> TestArguments(TestRun run, string work)
    {
        var project = run.Request.TestProject.Path;
        List<string> arguments = run.Legacy ? ["test", run.TargetPath]
            : run.Mode == DotnetTestMode.TestingPlatform
                ? ["test", "--project", project, "--no-build", "-c", run.Request.Configuration]
                : ["test", project, "--no-build", "-c", run.Request.Configuration];

        // One test at a time: code outside every test flow goes to the class entered last. xUnit v3
        // and TUnit under the testing platform take their own option; TUnit runs no test at all when
        // given a run settings file.
        var binaries = Path.GetDirectoryName(run.TargetPath)!;
        if (run.SuiteParallelism)
        {
            // Only VSTest needs settings then, for the collector.
            if (run.Mode == DotnetTestMode.VSTest)
            {
                arguments.AddRange(["--settings", WriteRunSettings(work, new SettingsParts(WithCollector: true, Sequential: false, RuntimeDeployment.TrackerFor(run.TargetPath)), run.TeamSettings)]);
            }
        }
        else if (File.Exists(Path.Combine(binaries, "TUnit.Engine.dll")))
        {
            arguments.AddRange(["--maximum-parallel-tests", "1"]);
        }
        else if (run.Mode == DotnetTestMode.TestingPlatform && File.Exists(Path.Combine(binaries, "xunit.v3.core.dll")))
        {
            arguments.AddRange(["--parallel", "none"]);
        }
        else
        {
            arguments.AddRange(["--settings", WriteRunSettings(work, new SettingsParts(WithCollector: run.Mode == DotnetTestMode.VSTest, Sequential: true, RuntimeDeployment.TrackerFor(run.TargetPath)), run.TeamSettings)]);
        }

        if (run.Framework is not null && !run.Legacy)
        {
            arguments.AddRange(["-f", run.Framework]);
        }

        // The team's other options run as given; its filter joins each shard's, and inline run settings go last (RunShard).
        arguments.AddRange(run.Team.Others);
        return arguments;
    }

    /// <summary>One test host process of a sharded recording.</summary>
    /// <param name="Dotnet">Process runner.</param>
    /// <param name="Arguments">Shared <c>dotnet test</c> arguments.</param>
    /// <param name="Filter">Filter arguments selecting this shard's tests; empty to run everything.</param>
    /// <param name="Index">Shard number, for output prefixes and file names.</param>
    /// <param name="Count">Number of shards.</param>
    private sealed record ShardRun(DotnetProcess Dotnet, List<string> Arguments, IReadOnlyList<string> Filter, int Index, int Count);

    /// <summary>
    /// Filters for the shards: the known test classes dealt round-robin into
    /// <see cref="RecordRequest.Parallelism"/> groups, plus one shard for any test outside the known
    /// classes, so a class the source index misses is still recorded. A single shard runs unfiltered.
    /// </summary>
    /// <param name="request">What to record.</param>
    /// <param name="syntax">Filter syntax of the project's runner.</param>
    private List<IReadOnlyList<string>> Shards(RecordRequest request, TestFilterSyntax syntax)
    {
        if (request.Options.Classes is { } selected)
        {
            return ClassShards(request, selected, syntax);
        }

        if (request.Parallelism <= 1)
        {
            return [[]];
        }

        var classes = new TestClassFinder(loggerFactory.CreateLogger<TestClassFinder>())
            .Find(request.Graph, request.RepoRoot, request.TestProject);
        if (classes.Count <= 1)
        {
            return [[]];
        }

        var groupCount = Math.Min(request.Parallelism, classes.Count);
        var groups = new List<string>[groupCount];
        for (var i = 0; i < groupCount; i++)
        {
            groups[i] = [];
        }

        for (var i = 0; i < classes.Count; i++)
        {
            groups[i % groupCount].Add(classes[i]);
        }

        var filters = new List<IReadOnlyList<string>>(groupCount + 1);
        foreach (var group in groups)
        {
            filters.Add(TestFilter.Arguments(group, exclude: false, syntax));
        }

        filters.Add(TestFilter.Arguments(classes, exclude: true, syntax));
        LogShards(request.TestProject.Path, groupCount, classes.Count);
        return filters;
    }

    /// <summary>
    /// Filters for an incremental step: the selected classes dealt round-robin into at most
    /// <see cref="RecordRequest.Parallelism"/> groups, plus one shard for every test outside the classes the source index
    /// knows. Those the selection could not judge, so they are recorded again each step.
    /// </summary>
    /// <param name="request">What to record.</param>
    /// <param name="selected">Classes to record.</param>
    /// <param name="syntax">Filter syntax of the project's runner.</param>
    private List<IReadOnlyList<string>> ClassShards(RecordRequest request, IReadOnlyCollection<string> selected, TestFilterSyntax syntax)
    {
        var known = new TestClassFinder(loggerFactory.CreateLogger<TestClassFinder>())
            .Find(request.Graph, request.RepoRoot, request.TestProject)
            .Union(selected, StringComparer.Ordinal)
            .ToList();
        var ordered = selected.Order(StringComparer.Ordinal).ToList();
        var groupCount = Math.Max(1, Math.Min(request.Parallelism, ordered.Count));
        var groups = new List<string>[groupCount];
        for (var i = 0; i < groupCount; i++)
        {
            groups[i] = [];
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            groups[i % groupCount].Add(ordered[i]);
        }

        var filters = new List<IReadOnlyList<string>>(groupCount + 1);
        foreach (var group in groups)
        {
            if (group.Count > 0)
            {
                filters.Add(TestFilter.Arguments(group, exclude: false, syntax));
            }
        }

        filters.Add(TestFilter.Arguments(known, exclude: true, syntax));
        LogClassShards(request.TestProject.Path, ordered.Count, known.Count);
        return filters;
    }

    /// <summary>What every shard of a recording shares.</summary>
    /// <param name="Request">What is recorded.</param>
    /// <param name="Mode">Runner <c>dotnet test</c> uses.</param>
    /// <param name="RunDirectory">Directory <c>dotnet test</c> runs in (<see cref="DotnetTestLocation"/>).</param>
    /// <param name="OutputDirectory">Output directory of the test project.</param>
    /// <param name="Instrumentation">Instrumentation result, for the method count.</param>
    /// <param name="Work">Work directory.</param>
    /// <param name="SuiteParallelism">True when the tests run with the suite's own parallelism.</param>
    private sealed record ShardContext(
        RecordRequest Request,
        DotnetTestMode Mode,
        string RunDirectory,
        string OutputDirectory,
        InstrumentationResult Instrumentation,
        string Work,
        bool SuiteParallelism = false);

    /// <summary>What one shard left behind.</summary>
    /// <param name="ExitCode">Exit code of <c>dotnet test</c>.</param>
    /// <param name="ResultsFiles">Coverage results written by the tracker, one per process that loaded it.</param>
    /// <param name="Report">The shard's TRX report, or null when the run could not write one.</param>
    internal sealed record ShardResult(int ExitCode, IReadOnlyList<string> ResultsFiles, TrxOutcome? Report);

    /// <summary>
    /// Test classes whose coverage cannot be trusted: those that failed in any shard, and those the
    /// reports say ran but the map has no trace of (their test process could not load the tracker,
    /// say). Null when a shard failed without a report to say which classes. A shard that exited
    /// cleanly had no failures, report or not.
    /// </summary>
    /// <param name="runs">Finished shards.</param>
    /// <param name="map">The map built from the shards' results.</param>
    private HashSet<string>? Untrusted(Task<ShardResult>[] runs, CoverageMap map)
    {
        var untrusted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (run.Result.Report is { } report)
            {
                untrusted.UnionWith(report.FailedClasses);
                foreach (var testClass in report.ExecutedClasses)
                {
                    if (!map.Classes.ContainsKey(testClass) && untrusted.Add(testClass))
                    {
                        LogUnrecordedClass(testClass);
                    }
                }

                continue;
            }

            if (run.Result.ExitCode != 0)
            {
                return null;
            }
        }

        return untrusted;
    }

    /// <summary>
    /// Test classes the reports list with none of their tests executed (every test skipped), which the
    /// map therefore knows by name only. Null when a shard left no report: the map cannot tell which
    /// classes the runner saw, so selection treats none as known (docs/design/unrecorded-classes.md).
    /// </summary>
    /// <param name="runs">Finished shards.</param>
    /// <param name="map">The map with its failed classes.</param>
    private static HashSet<string>? Skipped(Task<ShardResult>[] runs, CoverageMap map)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var executed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (run.Result.Report is not { } report)
            {
                return null;
            }

            reported.UnionWith(report.ReportedClasses);
            executed.UnionWith(report.ExecutedClasses);
        }

        reported.ExceptWith(executed);
        reported.RemoveWhere(c => map.Classes.ContainsKey(c) || map.FailedClasses?.Contains(c) == true);
        return reported;
    }

    /// <summary>
    /// The part of <paramref name="locations"/> that belongs to classes the runner reported; the PDB
    /// also lists helpers and fakes, which selection never asks about. Null without any location (no
    /// readable PDB).
    /// </summary>
    /// <param name="map">The map with its failed and skipped classes.</param>
    /// <param name="locations">Locations of every class of the test assembly.</param>
    private static Dictionary<string, IReadOnlyList<ClassLocation>>? Reported(CoverageMap map, Dictionary<string, IReadOnlyList<ClassLocation>> locations)
    {
        if (locations.Count == 0)
        {
            return null;
        }

        var reported = new Dictionary<string, IReadOnlyList<ClassLocation>>(StringComparer.Ordinal);
        foreach (var (testClass, places) in locations)
        {
            if (map.Reported(testClass))
            {
                reported[testClass] = places;
            }
        }

        return reported;
    }

    /// <summary>Runs one shard with its own results file and report, prefixing its output lines with the shard number.</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="context">What every shard shares.</param>
    /// <param name="output">Shared, synchronized output.</param>
    private static ShardResult RunShard(ShardRun shard, ShardContext context, TextWriter output)
    {
        var (request, instrumentation, work) = (context.Request, context.Instrumentation, context.Work);
        var resultsFile = Path.Combine(work, $"coverage-{shard.Index}.json");
        var reportDirectory = Path.Combine(work, $"report-{shard.Index}");
        var reportName = $"testdetta-record-{Path.GetFileName(work)}-{shard.Index}.trx";
        List<string> arguments = [.. shard.Arguments];
        if (TestReports.UsesNUnitXml(context.Mode, context.OutputDirectory))
        {
            // NUnit names its result file after the assembly, so every shard writes its own folder,
            // through its own copy of the run settings.
            var at = arguments.IndexOf("--settings");
            var settings = at >= 0 ? XDocument.Load(arguments[at + 1]).Root! : new XElement("RunSettings");
            TestReports.AddNUnitXml(settings, reportDirectory);
            Directory.CreateDirectory(reportDirectory);
            var path = Path.Combine(reportDirectory, "testdetta.runsettings");
            new XDocument(settings).Save(path);
            if (at >= 0)
            {
                arguments[at + 1] = path;
            }
            else
            {
                arguments.AddRange(["--settings", path]);
            }
        }
        else
        {
            arguments.AddRange(TestReports.Arguments(context.Mode, context.OutputDirectory, reportDirectory, reportName));
        }

        var team = TeamTestArguments.Parse(request.Options.DotnetTestArguments);
        arguments.AddRange(WithinTeamFilter(shard.Filter, team.Filter ?? (context.Mode == DotnetTestMode.VSTest ? request.TestProject.TestConfiguration?.CaseFilter : null)));
        arguments.AddRange(team.InlineSettings);

        var environment = new Dictionary<string, string>(UnattendedRun.Variables)
        {
            [TestDettaTracker.MethodCountVariable] = instrumentation.Methods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [TestDettaTracker.FieldCountVariable] = instrumentation.Fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [TestDettaTracker.OutputVariable] = resultsFile,
            // Every .NET process a test starts reports itself, whoever starts it (docs/design/runtime-file-reads.md).
            [TestDettaTracker.StartupHookVariable] = typeof(StartupHook).Assembly.Location,
            [StartupHook.OutputVariable] = Path.Combine(work, $"{ProcessStartsPrefix}{shard.Index}.tsv"),
        };
        if (context.SuiteParallelism)
        {
            environment[TestDettaTracker.ParallelFlowsVariable] = "1";
        }

        var temporary = shard.Count > 1 ? ShardTemporary(environment) : null;
        var writer = shard.Count == 1 ? output : new PrefixWriter(output, $"[shard {shard.Index + 1}/{shard.Count}] ");
        int exitCode;
        try
        {
            exitCode = shard.Dotnet.Run(arguments, context.RunDirectory, environment, writer, request.Options.Timeout);
        }
        finally
        {
            RemoveTemporary(temporary);
        }

        // The catch-all shard legitimately matches no test when the source index knew every class,
        // and then the test host never starts the collector.
        var isCatchAll = shard.Count > 1 && shard.Index == shard.Count - 1;
        var written = Directory.GetFiles(work, $"{Path.GetFileNameWithoutExtension(resultsFile)}.*.json");
        if (isCatchAll && written.Length == 0)
        {
            var empty = TestDettaTracker.ProcessResultsPath(resultsFile, 0);
            File.WriteAllText(empty, EmptyResults);
            written = [empty];
        }

        // Microsoft.Testing.Platform exits with 8 when no test ran, which is what an empty catch-all should do.
        if (isCatchAll && exitCode == TestingPlatformZeroTests)
        {
            exitCode = 0;
        }

        TestReports.Collect(context.Mode, request.RepoRoot, reportName, reportDirectory);
        return new ShardResult(exitCode, written, ReadReport(reportDirectory, writer));
    }

    /// <summary>
    /// The shard's test report, read as no report when it is not well-formed XML: a test host that crashed or was killed
    /// while writing it leaves it cut off (one of MSBuild's shards ended its TRX inside an attribute). Thrown, it failed the
    /// whole recording; without a report the shard counts as one that wrote none, so a failed shard leaves every class
    /// untrusted and the map unused for selection (<see cref="Untrusted"/>), and no class counts as known to be skipped
    /// (<see cref="Skipped"/>).
    /// </summary>
    /// <param name="directory">The shard's report directory.</param>
    /// <param name="output">The shard's output, which says the report was not read.</param>
    internal static TrxOutcome? ReadReport(string directory, TextWriter output)
    {
        try
        {
            return TrxReport.Read(directory);
        }
        catch (System.Xml.XmlException exception)
        {
            output.WriteLine($"TestDetta could not read this run's test report ({exception.Message}); it counts as a run without one");
            return null;
        }
    }

    /// <summary>
    /// Gives a shard's processes a temporary directory of their own (<c>TMPDIR</c>, <c>TMP</c>, <c>TEMP</c>) and returns
    /// it. Shards run at once, while the suite's own runs have one test process; fixtures often claim or clean fixed names
    /// under the system's temporary directory (LibGit2Sharp's <c>BaseFixture</c> deletes old test repositories there),
    /// so two shards doing it together failed every class of one of them, which then ran on every change. A short folder
    /// in the system's own temporary directory, so the suite sees the path it sees in its own runs: macOS links
    /// <c>/tmp</c> to <c>/private/tmp</c>, which fails suites that compare the temporary directory with a path a native
    /// library resolved (LibGit2Sharp adds <c>/private</c> to a <c>/var</c> one only), and <c>/private</c> has an
    /// <c>i</c> a Turkish <c>ToUpper</c> changes. .NET puts Unix domain sockets there, whose path may not exceed
    /// <see cref="SocketPathLimit"/> bytes; when the system's directory leaves no room for the longest (a diagnostics
    /// socket), the folder goes under the real path of <c>/tmp</c> instead. NuGet keeps the locks that make processes
    /// take turns writing its shared HTTP cache under the temporary directory too (<c>NuGetScratch/lock</c>), so the
    /// shards keep the one they would share without folders of their own (<see cref="NuGetScratch"/>).
    /// </summary>
    /// <param name="environment">The shard's environment, which receives the variables.</param>
    private static string ShardTemporary(Dictionary<string, string> environment)
    {
        var name = $"td{Guid.NewGuid():N}"[..8];
        var system = Path.GetTempPath();
        NuGetScratch.KeepShared(environment);
        var root = OperatingSystem.IsWindows() || system.Length + name.Length + 1 + LongestSocketName <= SocketPathLimit ? system : RealPath("/tmp");
        var temporary = Path.Combine(root, name);
        Directory.CreateDirectory(temporary);
        environment["TMPDIR"] = temporary + Path.DirectorySeparatorChar;
        environment["TMP"] = temporary;
        environment["TEMP"] = temporary;
        return temporary;
    }

    /// <summary>The directory a link points to, through every link; the directory itself when it is none.</summary>
    /// <param name="directory">A directory.</param>
    private static string RealPath(string directory) =>
        new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory;

    /// <summary>
    /// Deletes a shard's temporary directory; a file a leftover process still holds keeps it, which costs only disk
    /// space, so a failure is ignored.
    /// </summary>
    /// <param name="temporary">The directory, or null when the shard had none.</param>
    private static void RemoveTemporary(string? temporary)
    {
        if (temporary is null)
        {
            return;
        }

        try
        {
            Directory.Delete(temporary, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the system to clean.
        }
    }

    /// <summary>
    /// A shard's filter within the team's (docs/design/team-test-configuration.md): the team's <c>--filter</c>, else, under
    /// VSTest, the project's <c>VSTestTestCaseFilter</c>, which a filter on the command line would replace, so the recording
    /// would run tests the team's runs exclude. A VSTest-syntax filter becomes <c>(team)&amp;(shard)</c>; an unfiltered shard
    /// takes the team's filter alone (under VSTest the project's applies anyway). Next to another runner's class options the
    /// team's filter stays as given: if the runner cannot join them the shard fails, and the project is not recorded,
    /// rather than recorded beyond the team's filter.
    /// </summary>
    /// <param name="filter">The shard's filter arguments.</param>
    /// <param name="team">The team's filter, or null.</param>
    internal static IReadOnlyList<string> WithinTeamFilter(IReadOnlyList<string> filter, string? team) => (team, filter) switch
    {
        (null, _) => filter,
        (_, ["--filter", var ours]) => ["--filter", $"({team})&({ours})"],
        (_, []) => ["--filter", team],
        _ => [.. filter, "--filter", team],
    };

    /// <summary>Exit code of Microsoft.Testing.Platform when zero tests ran.</summary>
    private const int TestingPlatformZeroTests = 8;

    /// <summary>Results of a test host that ran no test.</summary>
    private const string EmptyResults = "{\"classes\":{},\"fixtures\":{},\"ambient\":[],\"outputLoadsUnseen\":[],\"classSeconds\":{},\"fixtureSeconds\":{}}";

    /// <summary>
    /// Asks MSBuild for the test assembly path. A multi-targeted project has no single path, so
    /// the first target framework is used and returned for the test run.
    /// </summary>
    /// <param name="dotnet">Process runner.</param>
    /// <param name="request">What to record.</param>
    /// <param name="runDirectory">Directory MSBuild runs in, so it uses the SDK the tests will.</param>
    private static (string TargetPath, string? Framework) FindTargetPath(DotnetProcess dotnet, RecordRequest request, string runDirectory)
    {
        if (request.Options.Framework is { } requested)
        {
            List<string> exact =
            [
                "msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}", $"-p:TargetFramework={requested}",
                "-getProperty:TargetPath",
            ];
            return (dotnet.Capture(exact, runDirectory).Trim(), requested);
        }

        List<string> query =
        [
            "msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}",
            "-getProperty:TargetPath", "-getProperty:TargetFrameworks",
        ];

        using var properties = JsonDocument.Parse(dotnet.Capture(query, runDirectory));
        var values = properties.RootElement.GetProperty("Properties");
        if (values.GetProperty("TargetPath").GetString() is { Length: > 0 } targetPath)
        {
            return (targetPath, null);
        }

        var framework = RecordingFramework(values.GetProperty("TargetFrameworks").GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [], OperatingSystem.IsWindows())
            ?? throw new InvalidOperationException($"Could not find the output of {request.TestProject.Path}.");

        query = ["msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}", $"-p:TargetFramework={framework}", "-getProperty:TargetPath"];
        return (dotnet.Capture(query, runDirectory).Trim(), framework);
    }

    /// <summary>
    /// The framework a multi-targeted test project is recorded for without <c>-f</c>: the first one its
    /// tests can run for here. .NET Framework (<c>net48</c>, <c>net472</c>, ...) runs on Windows only, so
    /// elsewhere the first other framework is taken (NodaTime.Test lists <c>net48</c> first); null for none.
    /// </summary>
    /// <param name="frameworks">The project's <c>TargetFrameworks</c>, in order.</param>
    /// <param name="windows">True on Windows.</param>
    internal static string? RecordingFramework(IReadOnlyList<string> frameworks, bool windows)
    {
        var listed = frameworks.Select(static f => f.Trim()).Where(static f => f.Length > 0).ToList();
        return (windows ? null : listed.FirstOrDefault(static f => !IsNetFramework(f))) ?? listed.FirstOrDefault();

        // net35 to net481: no dot after "net"; net5.0 and later have one, and so do netcoreapp and netstandard.
        static bool IsNetFramework(string framework) =>
            framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) && framework.Length > 3 && framework[3..].All(char.IsAsciiDigit);
    }

    /// <summary>Every project behind each assembly file name of the request's repository, cached per graph.</summary>
    /// <param name="request">What is being recorded.</param>
    private Dictionary<string, List<ProjectNode>> AssemblyOwners(RecordRequest request)
    {
        if (_assemblyNames is { } cached && ReferenceEquals(cached.Graph, request.Graph))
        {
            return cached.Owners;
        }

        var owners = new ProjectAssemblies(_logger).Owners(request.Graph, request.RepoRoot);
        _assemblyNames = (request.Graph, owners);
        return owners;
    }

    /// <summary>
    /// Code source generators wrote into the test assembly and the repository's assemblies, read from
    /// their PDBs before instrumentation rewrites them.
    /// </summary>
    /// <param name="request">What is being recorded.</param>
    /// <param name="targetPath">Test assembly.</param>
    /// <param name="owners">Project of each repository assembly in the test project's build (<see cref="GeneratedSourceReader.CompiledAgainst"/>).</param>
    /// <param name="assemblies">Repository assemblies in the test output.</param>
    /// <param name="framework">Framework of the build, for a project that targets several; null otherwise.</param>
    private List<GeneratedSource> ReadGenerated(RecordRequest request, string targetPath, IReadOnlyDictionary<string, ProjectNode> owners, List<string> assemblies, string? framework)
    {
        var generated = GeneratedSourceReader.Read(request.RepoRoot, request.TestProject, targetPath, owners, assemblies)
            .ConvertAll(source => source with { Framework = framework });
        LogGenerated(request.TestProject.Path, generated.Count);
        return generated;
    }

    /// <summary>
    /// Adds the generated code and the build inputs of every other framework of a multi-targeted test
    /// project, which the build without <c>-f</c> produced too: a generator may write other code for another
    /// framework, another framework's build may compile other files, and <c>dotnet test</c> runs them all
    /// (docs/design/generator-output-changes.md, docs/design/build-inputs-per-framework.md). The framework
    /// list is null when an output is missing, so the selection knows the map holds only some of them.
    /// </summary>
    /// <param name="dotnet">Runs dotnet.</param>
    /// <param name="request">What is being recorded.</param>
    /// <param name="runDirectory">Where dotnet runs.</param>
    /// <param name="build">The recorded framework's build: its framework, owners and the tracked files.</param>
    /// <param name="generated">Generated code of the recorded framework.</param>
    private OtherFrameworks ReadOtherFrameworks(DotnetProcess dotnet, RecordRequest request, string runDirectory, RecordedBuild build, List<GeneratedSource> generated)
    {
        var frameworks = TargetFrameworks(dotnet, request, runDirectory);
        var all = new List<GeneratedSource>(generated);
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var other in frameworks.Where(f => !string.Equals(f, build.Framework, StringComparison.OrdinalIgnoreCase)))
        {
            List<string> query = ["msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}", $"-p:TargetFramework={other}", "-getProperty:TargetPath"];
            var targetPath = dotnet.Capture(query, runDirectory).Trim();
            if (targetPath.Length == 0 || !File.Exists(targetPath))
            {
                LogFrameworkNotBuilt(request.TestProject.Path, other);
                return new OtherFrameworks(all, null, inputs);
            }

            var assemblies = GeneratedSourceReader.RepositoryAssemblies(build.Owners.Keys, Path.GetDirectoryName(targetPath)!);
            all.AddRange(ReadGenerated(request, targetPath, build.Owners, assemblies, other));
            inputs[other] = GeneratedSourceReader.ReadBuildInputs(request.RepoRoot, request.TestProject, targetPath, build.Owners, assemblies, build.Tracked);
        }

        return new OtherFrameworks(all, frameworks, inputs);
    }

    /// <summary>The recorded framework's build, which the other frameworks' builds are read next to.</summary>
    /// <param name="Framework">Framework whose tests are recorded, whose generated code and inputs are read already.</param>
    /// <param name="Owners">Project of each repository assembly in the test project's build (<see cref="GeneratedSourceReader.CompiledAgainst"/>).</param>
    /// <param name="Tracked">Files git tracks, relative, with '/' separators.</param>
    private sealed record RecordedBuild(string Framework, IReadOnlyDictionary<string, ProjectNode> Owners, IReadOnlySet<string> Tracked);

    /// <summary>What <see cref="ReadOtherFrameworks"/> read.</summary>
    /// <param name="Generated">Generated code of every framework read, the recorded one's first.</param>
    /// <param name="Frameworks">Every framework of the project, or null when one was not built.</param>
    /// <param name="BuildInputs">Build inputs of each other framework read, by framework.</param>
    private sealed record OtherFrameworks(
        List<GeneratedSource> Generated,
        IReadOnlyList<string>? Frameworks,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> BuildInputs);

    /// <summary>What recording needs in the run settings.</summary>
    /// <param name="WithCollector">True to load the in-process collector (VSTest).</param>
    /// <param name="Sequential">True to make the tests run one at a time.</param>
    /// <param name="Tracker">The build of the tracker assembly the test host loads (<see cref="RuntimeDeployment.TrackerFor"/>).</param>
    internal sealed record SettingsParts(bool WithCollector, bool Sequential, string Tracker);

    /// <summary>
    /// Writes the run settings of a recording: a copy of the team's file, when it has one, with TestDetta's own
    /// elements set in it, so the team's test case filter, environment variables, parameters, data collectors and
    /// timeouts stay as in its own runs (docs/design/team-test-configuration.md); on the command line, a file of
    /// TestDetta's alone would replace all of that. TestDetta sets: no test AppDomains, one test at a time
    /// (<paramref name="parts"/>), and the in-process collector next to the team's.
    /// </summary>
    /// <param name="directory">Where the file goes.</param>
    /// <param name="parts">TestDetta's part.</param>
    /// <param name="teamSettings">The team's run settings file, or null.</param>
    /// <exception cref="InvalidOperationException">The team's file cannot be read: recording under other settings would record other paths.</exception>
    internal static string WriteRunSettings(string directory, SettingsParts parts, string? teamSettings)
    {
        XElement root;
        try
        {
            root = teamSettings is null ? new XElement("RunSettings") : XDocument.Load(teamSettings).Root ?? new XElement("RunSettings");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidOperationException($"its run settings file {teamSettings} could not be read ({ex.Message}); TestDetta records under the team's settings or not at all");
        }

        // Tests run one at a time while recording: code outside every test flow is credited to the
        // test class entered last, which is only right when one class runs at a time.
        // On .NET Framework, xUnit v1/v2 and MSTest run tests in an AppDomain of their own, whose tracker
        // statics the collector in the default domain never sees: recording runs them without one, as on
        // .NET (docs/design/framework-support.md). The settings mean nothing to .NET test hosts.
        var configuration = Child(root, "RunConfiguration");
        Set(configuration, "DisableAppDomain", "true");
        var xunit = Child(root, "xUnit");
        Set(xunit, "AppDomain", "denied");
        Set(xunit, "ShadowCopy", "false");
        if (parts.Sequential)
        {
            Set(configuration, "DisableParallelization", "true");
            Set(xunit, "ParallelizeTestCollections", "false");
            Set(xunit, "MaxParallelThreads", "1");
            Set(Child(root, "NUnit"), "NumberOfTestWorkers", "0");
            Set(Child(Child(root, "MSTest"), "Parallelize"), "Workers", "1");
        }

        if (parts.WithCollector)
        {
            // The collector type is named, not loaded: it depends on test platform assemblies that only
            // the test host has. TestDettaTracker lives in the same assembly and has no such dependency.
            var runtime = typeof(TestDettaTracker).Assembly;
            var collectorName = $"{typeof(TestDettaTracker).Namespace}.{nameof(TestDettaInProcCollector)}, {runtime.FullName}";
            Child(Child(root, "InProcDataCollectionRunSettings"), "InProcDataCollectors").Add(
                new XElement("InProcDataCollector",
                    new XAttribute("friendlyName", "TestDetta"),
                    new XAttribute("uri", "InProcDataCollector://TestDetta/Coverage/1.0"),
                    new XAttribute("assemblyQualifiedName", collectorName),
                    new XAttribute("codebase", parts.Tracker)));
        }

        var path = Path.Combine(directory, "testdetta.runsettings");
        new XDocument(root).Save(path);
        return path;

        static XElement Child(XElement parent, string name)
        {
            if (parent.Element(name) is { } existing)
            {
                return existing;
            }

            var element = new XElement(name);
            parent.Add(element);
            return element;
        }

        static void Set(XElement parent, string name, string value) => Child(parent, name).Value = value;
    }

    /// <summary>Combines the method table with the collectors' per-class hits, merging all shards.</summary>
    /// <param name="request">What was recorded.</param>
    /// <param name="methods">Method table from instrumentation.</param>
    /// <param name="resultsFiles">JSON files written by <see cref="TestDettaTracker.WriteResults()"/>, one per shard.</param>
    private CoverageMap BuildMap(RecordRequest request, IReadOnlyList<CoveredMethod> methods, IReadOnlyList<string> resultsFiles)
    {
        var merged = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var mergedFixtures = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var ambientIds = new SortedSet<int>();
        var fixtureBuilders = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var classSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var fixtureSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var startupSeconds = new List<double>();
        var initialization = new SortedSet<int>();
        var linked = new SortedSet<int>();
        var unlinked = new SortedSet<int>();
        var builtClasses = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var builtFixtures = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var builtAmbient = new SortedSet<int>();
        var reads = new RawFileReads();
        var uses = new RawOutputUses();

        foreach (var file in resultsFiles)
        {
            using var document = ReadResults(file);
            var root = document.RootElement;
            // Ids past the methods are tracked fields a bucket read; what built their values in this
            // process counts for the bucket through the value, and so do the files and outputs used
            // while building them.
            var fieldReads = new FieldReads(root.TryGetProperty("methodCount", out var count) ? count.GetInt32() : methods.Count, Builders(root));
            reads.Add(root, fieldReads);
            uses.Add(root, fieldReads);
            MergeBuckets(root.GetProperty("classes"), merged, builtClasses, fieldReads);
            MergeBuckets(root.GetProperty("fixtures"), mergedFixtures, builtFixtures, fieldReads);
            if (root.TryGetProperty("initialization", out var initializing))
            {
                foreach (var id in initializing.EnumerateArray())
                {
                    initialization.Add(id.GetInt32());
                }
            }

            foreach (var (name, set) in (ReadOnlySpan<(string, SortedSet<int>)>)[("linked", linked), ("unlinked", unlinked)])
            {
                if (root.TryGetProperty(name, out var ids))
                {
                    foreach (var id in ids.EnumerateArray())
                    {
                        set.Add(id.GetInt32());
                    }
                }
            }
            foreach (var id in root.GetProperty("ambient").EnumerateArray())
            {
                if (!fieldReads.Add(id.GetInt32(), builtAmbient))
                {
                    ambientIds.Add(id.GetInt32());
                }
            }

            if (root.TryGetProperty("fixtureBuilders", out var builders))
            {
                foreach (var fixture in builders.EnumerateObject())
                {
                    if (!fixtureBuilders.TryGetValue(fixture.Name, out var classesBuilding))
                    {
                        classesBuilding = new SortedSet<string>(StringComparer.Ordinal);
                        fixtureBuilders[fixture.Name] = classesBuilding;
                    }

                    classesBuilding.UnionWith(fixture.Value.EnumerateArray().Select(static c => c.GetString()!));
                }
            }

            // A fixture set up in several shards cost its time in each, so times add up.
            AddSeconds(root, "classSeconds", classSeconds);
            AddSeconds(root, "fixtureSeconds", fixtureSeconds);

            // Only processes that ran a test report a start-up; an empty catch-all has none.
            if (root.TryGetProperty("startupSeconds", out var startup) && startup.GetDouble() > 0)
            {
                startupSeconds.Add(startup.GetDouble());
            }
        }

        foreach (var directory in resultsFiles.Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal))
        {
            foreach (var starts in Directory.GetFiles(directory!, $"{ProcessStartsPrefix}*.tsv"))
            {
                uses.AddProcessStarts(starts);
            }
        }

        var classes = Freeze(merged);
        var fixtures = Freeze(mergedFixtures);
        int[] ambient = [.. ambientIds];

        var git = new GitClient(loggerFactory.CreateLogger<GitClient>());
        var commit = git.Head(request.RepoRoot);
        var paths = new FileReadPaths(request.RepoRoot, git.TrackedFiles(request.RepoRoot));
        LogRecorded(request.TestProject.Path, classes.Count, fixtures.Count, methods.Count);

        var header = new CoverageMapHeader(
            Path.GetRelativePath(request.RepoRoot, request.TestProject.Path).Replace('\\', '/'),
            commit,
            DateTimeOffset.UtcNow);
        return new CoverageMap(
            header,
            methods,
            new CoverageHits(classes, fixtures, ambient),
            new CoverageTimings(classSeconds, fixtureSeconds, startupSeconds.Count == 0 ? 0 : startupSeconds.Average()))
        {
            Initialization = [.. initialization],
            FixtureBuilders = fixtureBuilders.ToDictionary(static f => f.Key, static IReadOnlyList<string> (f) => [.. f.Value], StringComparer.Ordinal),
            // Linked in some process and unlinked in none: one path the probes do not see is enough to stay run-once.
            LinkedInitialization = [.. initialization.Where(id => linked.Contains(id) && !unlinked.Contains(id))],
            BuiltValues = new CoverageHits(Freeze(builtClasses), Freeze(builtFixtures), [.. builtAmbient]),
            FileReads = reads.Seen ? reads.Resolve(paths) : null,
            OutputUses = uses.Seen ? uses.With(reads.BuildOutputs(paths)) : null,
        };
    }

    /// <summary>File name prefix of the lines the startup hook writes, one file per shard.</summary>
    private const string ProcessStartsPrefix = "process-starts-";

    /// <summary>
    /// The <c>outputUses</c> of every process's results and the processes the startup hook reported,
    /// as names (docs/design/runtime-file-reads.md).
    /// </summary>
    private sealed class RawOutputUses
    {
        private readonly Dictionary<string, HashSet<string>> _classes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _fixtures = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ambient = new(StringComparer.Ordinal);
        private readonly HashSet<string> _initialization = new(StringComparer.Ordinal);

        /// <summary>True once some process wrote <c>outputUses</c>: a tracker that records them ran.</summary>
        public bool Seen { get; private set; }

        /// <summary>Adds one process's uses.</summary>
        /// <param name="root">The process's results.</param>
        /// <param name="fieldReads">The process's tracked fields: a bucket that read one used what built its value.</param>
        public void Add(JsonElement root, FieldReads fieldReads)
        {
            if (!root.TryGetProperty("outputUses", out var uses))
            {
                return;
            }

            AddFieldUses(root, uses, fieldReads, _classes, _fixtures, _ambient);

            Seen = true;
            foreach (var (name, buckets) in (ReadOnlySpan<(string, Dictionary<string, HashSet<string>>)>)[("classes", _classes), ("fixtures", _fixtures)])
            {
                foreach (var bucket in uses.GetProperty(name).EnumerateObject())
                {
                    Bucket(buckets, bucket.Name).UnionWith(bucket.Value.EnumerateArray().Select(static p => p.GetString()!));
                }
            }

            _ambient.UnionWith(uses.GetProperty("ambient").EnumerateArray().Select(static p => p.GetString()!));
            _initialization.UnionWith(uses.GetProperty("initialization").EnumerateArray().Select(static p => p.GetString()!));
        }

        /// <summary>
        /// Adds the processes the startup hook reported: per line the bucket current when the process
        /// started (<c>c|class</c>, <c>f|fixture</c>, <c>a|</c>), its entry assembly and its command line.
        /// </summary>
        /// <param name="path">A file of lines the hook wrote.</param>
        public void AddProcessStarts(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                var fields = line.Split('\t');
                if (fields.Length < 2 || fields[0].Length < 2 || fields[0][1] != '|')
                {
                    continue;
                }

                var name = fields[0][2..];
                var names = fields[0][0] switch
                {
                    'c' when name.Length > 0 => Bucket(_classes, name),
                    'f' when name.Length > 0 => Bucket(_fixtures, name),
                    _ => _ambient,
                };
                foreach (var field in fields.Skip(1))
                {
                    TestDettaTracker.AddNames(names, field);
                }
            }
        }

        /// <summary>The uses, with the names of build outputs the tests read added to the readers' (see <see cref="RawFileReads.BuildOutputs"/>).</summary>
        /// <param name="outputs">Names of untracked build outputs read, by bucket, shaped like the uses.</param>
        public FileReads With(FileReads outputs)
        {
            foreach (var (name, values) in outputs.Classes)
            {
                Bucket(_classes, name).UnionWith(values);
            }

            foreach (var (name, values) in outputs.Fixtures)
            {
                Bucket(_fixtures, name).UnionWith(values);
            }

            _ambient.UnionWith(outputs.Ambient);
            _initialization.UnionWith(outputs.Initialization);
            return new FileReads(Sorted(_classes), Sorted(_fixtures), [.. _ambient.Order(StringComparer.Ordinal)], [.. _initialization.Order(StringComparer.Ordinal)]);
        }

        /// <summary>The set of <paramref name="name"/>, added when missing.</summary>
        /// <param name="buckets">Sets by bucket.</param>
        /// <param name="name">Bucket name.</param>
        private static HashSet<string> Bucket(Dictionary<string, HashSet<string>> buckets, string name)
        {
            if (!buckets.TryGetValue(name, out var set))
            {
                buckets[name] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            return set;
        }

        /// <summary>Sorted lists of the non-empty sets.</summary>
        /// <param name="buckets">Sets by bucket.</param>
        private static Dictionary<string, IReadOnlyList<string>> Sorted(Dictionary<string, HashSet<string>> buckets) =>
            buckets.Where(static b => b.Value.Count > 0)
                .ToDictionary(static b => b.Key, static b => (IReadOnlyList<string>)[.. b.Value.Order(StringComparer.Ordinal)], StringComparer.Ordinal);
    }

    /// <summary>The <c>fileReads</c> of every process's results, as full paths, until they are resolved.</summary>
    private sealed class RawFileReads
    {
        private readonly Dictionary<string, HashSet<string>> _classes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _fixtures = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ambient = new(StringComparer.Ordinal);
        private readonly HashSet<string> _initialization = new(StringComparer.Ordinal);

        /// <summary>True once some process wrote <c>fileReads</c>: a tracker that records them ran.</summary>
        public bool Seen { get; private set; }

        /// <summary>Adds one process's reads.</summary>
        /// <param name="root">The process's results.</param>
        /// <param name="fieldReads">The process's tracked fields: a bucket that read one used what built its value.</param>
        public void Add(JsonElement root, FieldReads fieldReads)
        {
            if (!root.TryGetProperty("fileReads", out var reads))
            {
                return;
            }

            AddFieldUses(root, reads, fieldReads, _classes, _fixtures, _ambient);

            Seen = true;
            foreach (var (name, buckets) in (ReadOnlySpan<(string, Dictionary<string, HashSet<string>>)>)[("classes", _classes), ("fixtures", _fixtures)])
            {
                foreach (var bucket in reads.GetProperty(name).EnumerateObject())
                {
                    if (!buckets.TryGetValue(bucket.Name, out var set))
                    {
                        buckets[bucket.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    }

                    set.UnionWith(bucket.Value.EnumerateArray().Select(static p => p.GetString()!));
                }
            }

            _ambient.UnionWith(reads.GetProperty("ambient").EnumerateArray().Select(static p => p.GetString()!));
            _initialization.UnionWith(reads.GetProperty("initialization").EnumerateArray().Select(static p => p.GetString()!));
        }

        /// <summary>
        /// The names (see <see cref="TestDettaTracker.OutputUsed"/>) of the files read under a <c>bin</c> or
        /// <c>obj</c> folder of the repository that stand for no tracked file: a build wrote them, so they
        /// may be the output of a project referenced without compiling against it. A file only checked for
        /// (<see cref="TestDettaTracker.CheckPrefix"/>) is left out: a changed project's output exists before and after
        /// the change, so the check cannot see it, and NUnit's engine checks for every library of the dependency
        /// context whenever the runtime cannot resolve a name (an XmlSerializer looking for its pregenerated assembly).
        /// A load of what it found shows as a load of its own.
        /// </summary>
        /// <param name="paths">Resolves full paths.</param>
        public FileReads BuildOutputs(FileReadPaths paths)
        {
            return new FileReads(Names(_classes), Names(_fixtures), Of(_ambient), Of(_initialization));

            Dictionary<string, IReadOnlyList<string>> Names(Dictionary<string, HashSet<string>> buckets) =>
                buckets.Select(b => (b.Key, Names: Of(b.Value)))
                    .Where(static b => b.Names.Count > 0)
                    .ToDictionary(static b => b.Key, static b => b.Names, StringComparer.Ordinal);

            IReadOnlyList<string> Of(HashSet<string> fulls)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var full in fulls.Where(f => !f.EndsWith('/') && !f.StartsWith(TestDettaTracker.CheckPrefix, StringComparison.Ordinal) && paths.IsUntrackedBuildOutput(f)))
                {
                    TestDettaTracker.AddNames(names, full);
                }

                return [.. names.Order(StringComparer.Ordinal)];
            }
        }

        /// <summary>The reads as repository paths; classes and fixtures left with none are left out.</summary>
        /// <param name="paths">Resolves full paths.</param>
        public FileReads Resolve(FileReadPaths paths) => new(
            Resolve(_classes, paths),
            Resolve(_fixtures, paths),
            paths.ResolveAll(_ambient),
            paths.ResolveAll(_initialization));

        /// <summary>Resolves the reads of each class or fixture.</summary>
        /// <param name="buckets">Full paths by bucket.</param>
        /// <param name="paths">Resolves full paths.</param>
        private static Dictionary<string, IReadOnlyList<string>> Resolve(Dictionary<string, HashSet<string>> buckets, FileReadPaths paths) =>
            buckets.Select(b => (b.Key, Paths: paths.ResolveAll(b.Value)))
                .Where(static b => b.Paths.Count > 0)
                .ToDictionary(static b => b.Key, static b => b.Paths, StringComparer.Ordinal);
    }

    /// <summary>
    /// The tracked fields of one process's results: ids from <see cref="MethodCount"/> on are fields,
    /// and what built each field's values in that process.
    /// </summary>
    /// <param name="MethodCount">Number of instrumented methods; field <c>i</c> has the id <c>MethodCount + i</c>.</param>
    /// <param name="Builders">Methods that built the values stored into each field, by field index.</param>
    private sealed record FieldReads(int MethodCount, Dictionary<int, int[]> Builders)
    {
        /// <summary>When <paramref name="id"/> is a field, adds what built its values to <paramref name="built"/> and returns true.</summary>
        /// <param name="id">Bit id from the results.</param>
        /// <param name="built">Methods the bucket depends on through values; updated in place.</param>
        public bool Add(int id, SortedSet<int> built)
        {
            if (id < MethodCount)
            {
                return false;
            }

            Walk(id - MethodCount, built, fields: null);
            return true;
        }

        /// <summary>
        /// Adds to <paramref name="fields"/> the field <paramref name="id"/> stands for and every field
        /// whose value went into building it; nothing when <paramref name="id"/> is a method.
        /// </summary>
        /// <param name="id">Bit id from the results.</param>
        /// <param name="fields">Field indexes; updated in place.</param>
        public void AddFields(int id, HashSet<int> fields)
        {
            if (id >= MethodCount)
            {
                Walk(id - MethodCount, built: null, fields);
            }
        }

        /// <summary>
        /// Follows the builders of <paramref name="start"/>. A value built while reading another field's
        /// value depends on what built that one too: the time zone cache built from the parsed TZDB data
        /// (NodaTime). Fields are followed transitively, each once.
        /// </summary>
        /// <param name="start">Field index.</param>
        /// <param name="built">Builder methods reached, or null.</param>
        /// <param name="fields">Fields reached, <paramref name="start"/> included, or null.</param>
        private void Walk(int start, SortedSet<int>? built, HashSet<int>? fields)
        {
            var pending = new Stack<int>([start]);
            var seen = fields ?? [];
            while (pending.TryPop(out var field))
            {
                if (!seen.Add(field) || !Builders.TryGetValue(field, out var builders))
                {
                    continue;
                }

                foreach (var builder in builders)
                {
                    if (builder < MethodCount)
                    {
                        built?.Add(builder);
                    }
                    else
                    {
                        pending.Push(builder - MethodCount);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Credits the files or outputs used while building a tracked field's value (the <c>fields</c> of
    /// <paramref name="uses"/>) to every bucket of <paramref name="root"/> that read the field, or a field
    /// whose value was built from it (docs/design/runtime-file-reads.md, initialization).
    /// </summary>
    /// <param name="root">One process's results.</param>
    /// <param name="uses">Its <c>fileReads</c> or <c>outputUses</c>.</param>
    /// <param name="fieldReads">Its tracked fields.</param>
    /// <param name="classes">Uses per test class; updated in place.</param>
    /// <param name="fixtures">Uses per fixture; updated in place.</param>
    /// <param name="ambient">Uses outside every test; updated in place.</param>
    private static void AddFieldUses(JsonElement root, JsonElement uses, FieldReads fieldReads, Dictionary<string, HashSet<string>> classes, Dictionary<string, HashSet<string>> fixtures, HashSet<string> ambient)
    {
        if (!uses.TryGetProperty("fields", out var element))
        {
            return;
        }

        var byField = new Dictionary<int, string[]>();
        foreach (var field in element.EnumerateObject())
        {
            byField[int.Parse(field.Name, System.Globalization.CultureInfo.InvariantCulture)] = [.. field.Value.EnumerateArray().Select(static p => p.GetString()!)];
        }

        if (byField.Count == 0)
        {
            return;
        }

        foreach (var (name, buckets) in (ReadOnlySpan<(string, Dictionary<string, HashSet<string>>)>)[("classes", classes), ("fixtures", fixtures)])
        {
            foreach (var bucket in root.GetProperty(name).EnumerateObject())
            {
                if (Used(bucket.Value) is not { } used)
                {
                    continue;
                }

                if (!buckets.TryGetValue(bucket.Name, out var set))
                {
                    buckets[bucket.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.UnionWith(used);
            }
        }

        ambient.UnionWith(Used(root.GetProperty("ambient")) ?? []);

        // What the fields a bucket read, directly or through the values built from them, were built with; null for none.
        HashSet<string>? Used(JsonElement ids)
        {
            var fields = new HashSet<int>();
            foreach (var id in ids.EnumerateArray())
            {
                fieldReads.AddFields(id.GetInt32(), fields);
            }

            HashSet<string>? used = null;
            foreach (var field in fields)
            {
                if (byField.TryGetValue(field, out var values))
                {
                    (used ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(values);
                }
            }

            return used;
        }
    }

    /// <summary>
    /// The <c>outputLoadsUnseen</c> of every process's results, merged and sorted (<see cref="CoverageMap.OutputLoadsUnseen"/>);
    /// null when a process wrote none, whose loads are then unknown.
    /// </summary>
    /// <param name="resultsFiles">Every process's results.</param>
    internal static List<string>? LoadsUnseen(IReadOnlyList<string> resultsFiles)
    {
        var merged = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in resultsFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!document.RootElement.TryGetProperty("outputLoadsUnseen", out var element))
            {
                return null;
            }

            foreach (var name in element.EnumerateArray())
            {
                merged.Add(name.GetString()!);
            }
        }

        return [.. merged];
    }

    /// <summary>
    /// The <c>leakedReads</c> of every process's results, merged per test class and named by field
    /// (<see cref="CoverageMap.LeakedReads"/>).
    /// </summary>
    /// <param name="resultsFiles">Every process's results.</param>
    /// <param name="fields">Tracked fields by index, as <c>Type::Field</c>.</param>
    internal static Dictionary<string, IReadOnlyList<string>> LeakedReads(IReadOnlyList<string> resultsFiles, IReadOnlyList<string> fields)
    {
        var merged = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in resultsFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!document.RootElement.TryGetProperty("leakedReads", out var element))
            {
                continue;
            }

            foreach (var testClass in element.EnumerateObject())
            {
                foreach (var index in testClass.Value.EnumerateArray())
                {
                    var field = index.GetInt32();
                    if ((uint)field >= (uint)fields.Count)
                    {
                        continue;
                    }

                    if (!merged.TryGetValue(testClass.Name, out var names))
                    {
                        merged[testClass.Name] = names = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    names.Add(fields[field]);
                }
            }
        }

        return merged.ToDictionary(static m => m.Key, static IReadOnlyList<string> (m) => [.. m.Value], StringComparer.Ordinal);
    }

    /// <summary>The <c>fieldBuilders</c> of one process's results; empty for results without them.</summary>
    /// <param name="root">One process's results.</param>
    private static Dictionary<int, int[]> Builders(JsonElement root)
    {
        var builders = new Dictionary<int, int[]>();
        if (root.TryGetProperty("fieldBuilders", out var element))
        {
            foreach (var field in element.EnumerateObject())
            {
                builders[int.Parse(field.Name, System.Globalization.CultureInfo.InvariantCulture)] = [.. field.Value.EnumerateArray().Select(static i => i.GetInt32())];
            }
        }

        return builders;
    }

    /// <summary>
    /// Adds the named id arrays of <paramref name="element"/> to <paramref name="buckets"/>. A fixture
    /// can appear in several shards, so ids are unioned.
    /// </summary>
    /// <param name="element">JSON object written by <see cref="TestDettaTracker.WriteResults()"/>.</param>
    /// <param name="buckets">Merged buckets; updated in place.</param>
    /// <param name="built">Per bucket, methods it depends on through values it read; updated in place.</param>
    /// <param name="fieldReads">Tracked fields of these results.</param>
    private static void MergeBuckets(JsonElement element, Dictionary<string, SortedSet<int>> buckets, Dictionary<string, SortedSet<int>> built, FieldReads fieldReads)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!buckets.TryGetValue(property.Name, out var ids))
            {
                ids = [];
                buckets[property.Name] = ids;
            }

            if (!built.TryGetValue(property.Name, out var values))
            {
                values = [];
                built[property.Name] = values;
            }

            foreach (var id in property.Value.EnumerateArray())
            {
                if (!fieldReads.Add(id.GetInt32(), values))
                {
                    ids.Add(id.GetInt32());
                }
            }
        }
    }

    /// <summary>Adds the seconds under <paramref name="property"/> of a shard's results to <paramref name="seconds"/>.</summary>
    /// <param name="root">Shard results.</param>
    /// <param name="property">Name of the object holding the seconds.</param>
    /// <param name="seconds">Merged seconds; updated in place.</param>
    private static void AddSeconds(JsonElement root, string property, Dictionary<string, double> seconds)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return;
        }

        foreach (var entry in element.EnumerateObject())
        {
            seconds[entry.Name] = seconds.GetValueOrDefault(entry.Name) + entry.Value.GetDouble();
        }
    }

    /// <summary>Parses one shard's results.</summary>
    /// <param name="file">Results file written by <see cref="TestDettaTracker.WriteResults()"/>.</param>
    /// <exception cref="InvalidOperationException">The file is empty or not JSON: its test host ended while writing.</exception>
    private static JsonDocument ReadResults(string file)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(file));
        }
        catch (JsonException ex)
        {
            // A map built without this shard would silently lack its test classes.
            throw new InvalidOperationException(
                $"The coverage results of a test process are unreadable ({Path.GetFileName(file)}); its test host probably ended while writing them. Record again.",
                ex);
        }
    }

    /// <summary>Converts merged buckets to the map's read-only shape.</summary>
    /// <param name="buckets">Merged buckets.</param>
    private static Dictionary<string, IReadOnlyList<int>> Freeze(Dictionary<string, SortedSet<int>> buckets)
    {
        var frozen = new Dictionary<string, IReadOnlyList<int>>(buckets.Count, StringComparer.Ordinal);
        foreach (var (name, ids) in buckets)
        {
            frozen[name] = [.. ids];
        }

        return frozen;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Recording {Project} in {ShardCount} process(es) for {ClassCount} known test class(es), plus a catch-all process")]
    private partial void LogShards(string project, int shardCount, int classCount);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Recording {Project} incrementally: {ClassCount} selected test class(es), plus a catch-all process for tests outside the {KnownCount} known class(es)")]
    private partial void LogClassShards(string project, int classCount, int knownCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The tests of {Project} crashed when run one at a time; recording them with their own parallelism, so code outside every test counts as ambient")]
    private partial void LogSequentialCrashed(string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A test process of {Project} crashed before writing coverage; recording it again in one process")]
    private partial void LogShardCrashed(string project);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Recording {Project} with the {Mode} runner")]
    private partial void LogMode(string project, DotnetTestMode mode);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{Project}: {Count} test class(es) failed while recording (-1: unknown, the map will not be used)")]
    private partial void LogFailedClasses(string project, int count);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{Project}: {Count} test class(es) had every test skipped (-1: unknown, no report); {Located} reported class(es) located in the PDB")]
    private partial void LogSkippedClasses(string project, int count, int located);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{TestClass} ran but left no coverage; it will always be selected")]
    private partial void LogUnrecordedClass(string testClass);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project}: {Count} generated file(s) read from PDBs")]
    private partial void LogGenerated(string project, int count);

    /// <summary>The project's <c>TargetFrameworks</c>, empty when it sets only <c>TargetFramework</c>.</summary>
    /// <param name="dotnet">Runs dotnet.</param>
    /// <param name="request">What is being recorded.</param>
    /// <param name="runDirectory">Where dotnet runs.</param>
    private static string[] TargetFrameworks(DotnetProcess dotnet, RecordRequest request, string runDirectory)
    {
        List<string> query = ["msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}", "-getProperty:TargetFrameworks"];
        return dotnet.Capture(query, runDirectory).Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{Project}: no build for {Framework}, so its generated code is not in the map; a changed source generator runs this project completely")]
    private partial void LogFrameworkNotBuilt(string project, string framework);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Instrumenting {Count} assembly(ies) in {Directory}")]
    private partial void LogAssemblies(int count, string directory);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Recorded {Project}: {ClassCount} test class(es), {FixtureCount} fixture(s), {MethodCount} method(s)")]
    private partial void LogRecorded(string project, int classCount, int fixtureCount, int methodCount);
}
