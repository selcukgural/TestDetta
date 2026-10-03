using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Sift.Core;
using Sift.Coverage.Runtime;

namespace Sift.Coverage;

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
}

/// <summary>
/// Builds a test project, instruments the repository's assemblies in its output, runs the tests
/// with <see cref="SiftInProcCollector"/> attached and turns the result into a <see cref="CoverageMap"/>.
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
    private (ProjectGraph Graph, Dictionary<string, ProjectNode> Owners)? _assemblyNames;

    /// <summary>Records the coverage map of one test project.</summary>
    /// <param name="request">What to record.</param>
    /// <param name="output">Receives build and test output.</param>
    /// <returns>The map, and whether all tests passed while recording.</returns>
    /// <exception cref="InvalidOperationException">The build failed.</exception>
    /// <exception cref="NoTestsRecordedException">No test ran, so nothing was recorded.</exception>
    public (CoverageMap Map, bool TestsPassed) Record(RecordRequest request, TextWriter output)
    {
        var dotnet = new DotnetProcess(_logger);
        var project = request.TestProject.Path;
        var root = request.RepoRoot;

        List<string> build = ["build", project, "-c", request.Configuration];
        if (request.Options.Framework is { } targetFramework)
        {
            build.AddRange(["-f", targetFramework]);
        }

        if (!request.Options.NoBuild)
        {
            var errors = new ErrorCollectingWriter(output);
            var buildExit = dotnet.Run(build, root, new Dictionary<string, string>(), errors);
            if (buildExit != 0)
            {
                var reason = errors.Errors.Count == 0 ? string.Empty : ": " + string.Join(" / ", errors.Errors);
                throw new InvalidOperationException($"Building {Path.GetFileName(project)} failed with exit code {buildExit}{reason}");
            }
        }

        var (targetPath, framework) = FindTargetPath(dotnet, request);
        if (request.Options.NoBuild && !File.Exists(targetPath))
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(project)} has no build output at {targetPath}; build it with -c {request.Configuration} first, or drop --no-build.");
        }
        var outputDirectory = Path.GetDirectoryName(targetPath)!;
        var assemblies = AssembliesToInstrument(new HashSet<string>(AssemblyOwners(request).Keys, StringComparer.OrdinalIgnoreCase), outputDirectory);
        var generated = ReadGenerated(request, targetPath, assemblies);
        LogAssemblies(assemblies.Count, outputDirectory);

        var work = Path.Combine(Path.GetTempPath(), "sift-record-" + Guid.NewGuid().ToString("N"));
        var instrumenter = new AssemblyInstrumenter(_logger);
        var testAssemblies = new HashSet<string>(StringComparer.Ordinal) { targetPath };
        var instrumentation = instrumenter.Instrument(assemblies, testAssemblies, Path.Combine(work, "backup"));
        var mode = DotnetTestModeDetector.Detect(root);
        RuntimeDeployment? deployment = null;

        try
        {
            // Under Microsoft.Testing.Platform the tests run in the test project's own executable,
            // where no VSTest collector loads the runtime; it goes into the output instead.
            // The runtime always goes into the output, so instrumented code finds it without the VSTest
            // collector: xUnit v3 runs tests in the test executable, where no collector loads, and old
            // test hosts (Microsoft.NET.Test.Sdk 16) do not load the collector at all.
            deployment = new RuntimeDeployment(targetPath);
            var testArguments = TestArguments(new TestRun(request, mode, targetPath, framework), work);
            LogMode(project, mode);

            // Told by the runner's assembly in the output, which also catches a framework the project
            // gets through another package.
            var binaries = Path.GetDirectoryName(targetPath)!;
            var syntax = File.Exists(Path.Combine(binaries, "TUnit.Engine.dll")) ? TestFilterSyntax.TUnit
                : mode == DotnetTestMode.TestingPlatform && File.Exists(Path.Combine(binaries, "xunit.v3.core.dll")) ? TestFilterSyntax.XunitV3
                : TestFilterSyntax.VSTest;
            var shards = Shards(request, syntax);
            var synchronizedOutput = TextWriter.Synchronized(output);
            var runs = RunShards(shards, new ShardContext(request, mode, outputDirectory, instrumentation, work), testArguments, synchronizedOutput);
            ThrowIfTimedOut(runs, project, request);

            // A shard that crashed before its process wrote results (NLog: a subset of its classes takes
            // the host down when they run without the others) would cost the project its whole map.
            // The suite passes in one process, so the project is recorded again the way dotnet test runs it.
            if (shards.Count > 1 && runs.Any(static r => r.Result.ResultsFiles.Count == 0))
            {
                LogShardCrashed(project);
                var single = Path.Combine(work, "single");
                Directory.CreateDirectory(single);
                runs = RunShards([[]], new ShardContext(request, mode, outputDirectory, instrumentation, single), testArguments, synchronizedOutput);
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
                var parallelArguments = TestArguments(new TestRun(request, mode, targetPath, framework, SuiteParallelism: true), parallel);
                runs = RunShards([[]], new ShardContext(request, mode, outputDirectory, instrumentation, parallel, SuiteParallelism: true), parallelArguments, synchronizedOutput);
                ThrowIfTimedOut(runs, project, request);
                suiteParallelism = true;
            }

            var resultsFiles = new List<string>(runs.Length);
            foreach (var run in runs)
            {
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
            map = map with
            {
                FailedClasses = Untrusted(runs, map),
                Generated = generated,
                Timings = suiteParallelism ? null : map.Timings,
                ExternalCallbacks = instrumentation.ExternalCallbacks,
            };
            LogFailedClasses(project, map.FailedClasses?.Count ?? -1);
            return (map, runs.All(static r => r.Result.ExitCode == 0));
        }
        finally
        {
            deployment?.Dispose();
            instrumenter.Restore(instrumentation);
            Directory.Delete(work, recursive: true);
        }
    }

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
                ? [.. testArguments, "--ignore-exit-code", TestingPlatformZeroTests.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                : testArguments;
            var shard = new ShardRun(dotnet, arguments, shards[i], i, shards.Count);
            runs[i] = Task.Run(() => RunShard(shard, context, output));
        }

        Task.WaitAll(runs);
        return runs;
    }

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
    private sealed record TestRun(RecordRequest Request, DotnetTestMode Mode, string TargetPath, string? Framework, bool SuiteParallelism = false);

    /// <summary>
    /// <c>dotnet test</c> arguments shared by all shards. Tests must run one at a time: VSTest and the
    /// VSTest bridge of MSTest and NUnit take that from run settings, xUnit v3 from <c>--parallel none</c>.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="work">Work directory for the run settings file.</param>
    private static List<string> TestArguments(TestRun run, string work)
    {
        var project = run.Request.TestProject.Path;
        List<string> arguments = run.Mode == DotnetTestMode.TestingPlatform
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
                arguments.AddRange(["--settings", WriteRunSettings(work, withCollector: true, sequential: false)]);
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
            arguments.AddRange(["--settings", WriteRunSettings(work, withCollector: run.Mode == DotnetTestMode.VSTest, sequential: true)]);
        }

        if (run.Framework is not null)
        {
            arguments.AddRange(["-f", run.Framework]);
        }

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

    /// <summary>What every shard of a recording shares.</summary>
    /// <param name="Request">What is recorded.</param>
    /// <param name="Mode">Runner <c>dotnet test</c> uses.</param>
    /// <param name="OutputDirectory">Output directory of the test project.</param>
    /// <param name="Instrumentation">Instrumentation result, for the method count.</param>
    /// <param name="Work">Work directory.</param>
    /// <param name="SuiteParallelism">True when the tests run with the suite's own parallelism.</param>
    private sealed record ShardContext(
        RecordRequest Request,
        DotnetTestMode Mode,
        string OutputDirectory,
        InstrumentationResult Instrumentation,
        string Work,
        bool SuiteParallelism = false);

    /// <summary>What one shard left behind.</summary>
    /// <param name="ExitCode">Exit code of <c>dotnet test</c>.</param>
    /// <param name="ResultsFiles">Coverage results written by the tracker, one per process that loaded it.</param>
    /// <param name="Report">The shard's TRX report, or null when the run could not write one.</param>
    private sealed record ShardResult(int ExitCode, IReadOnlyList<string> ResultsFiles, TrxOutcome? Report);

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

    /// <summary>Runs one shard with its own results file and report, prefixing its output lines with the shard number.</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="context">What every shard shares.</param>
    /// <param name="output">Shared, synchronized output.</param>
    private static ShardResult RunShard(ShardRun shard, ShardContext context, TextWriter output)
    {
        var (request, instrumentation, work) = (context.Request, context.Instrumentation, context.Work);
        var resultsFile = Path.Combine(work, $"coverage-{shard.Index}.json");
        var reportDirectory = Path.Combine(work, $"report-{shard.Index}");
        var reportName = $"sift-record-{Path.GetFileName(work)}-{shard.Index}.trx";
        List<string> arguments = [.. shard.Arguments];
        if (TestReports.UsesNUnitXml(context.Mode, context.OutputDirectory))
        {
            // NUnit names its result file after the assembly, so every shard writes its own folder,
            // through its own copy of the run settings.
            var at = arguments.IndexOf("--settings");
            var settings = at >= 0 ? XDocument.Load(arguments[at + 1]).Root! : new XElement("RunSettings");
            TestReports.AddNUnitXml(settings, reportDirectory);
            Directory.CreateDirectory(reportDirectory);
            var path = Path.Combine(reportDirectory, "sift.runsettings");
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

        arguments.AddRange(shard.Filter);

        var environment = new Dictionary<string, string>
        {
            [SiftTracker.MethodCountVariable] = instrumentation.Methods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SiftTracker.FieldCountVariable] = instrumentation.Fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SiftTracker.OutputVariable] = resultsFile,
        };
        if (context.SuiteParallelism)
        {
            environment[SiftTracker.ParallelFlowsVariable] = "1";
        }

        var writer = shard.Count == 1 ? output : new PrefixWriter(output, $"[shard {shard.Index + 1}/{shard.Count}] ");
        var exitCode = shard.Dotnet.Run(arguments, request.RepoRoot, environment, writer, request.Options.Timeout);

        // The catch-all shard legitimately matches no test when the source index knew every class,
        // and then the test host never starts the collector.
        var isCatchAll = shard.Count > 1 && shard.Index == shard.Count - 1;
        var written = Directory.GetFiles(work, $"{Path.GetFileNameWithoutExtension(resultsFile)}.*.json");
        if (isCatchAll && written.Length == 0)
        {
            var empty = SiftTracker.ProcessResultsPath(resultsFile, 0);
            File.WriteAllText(empty, EmptyResults);
            written = [empty];
        }

        // Microsoft.Testing.Platform exits with 8 when no test ran, which is what an empty catch-all should do.
        if (isCatchAll && exitCode == TestingPlatformZeroTests)
        {
            exitCode = 0;
        }

        TestReports.Collect(context.Mode, request.RepoRoot, reportName, reportDirectory);
        return new ShardResult(exitCode, written, TrxReport.Read(reportDirectory));
    }

    /// <summary>Exit code of Microsoft.Testing.Platform when zero tests ran.</summary>
    private const int TestingPlatformZeroTests = 8;

    /// <summary>Results of a test host that ran no test.</summary>
    private const string EmptyResults = "{\"classes\":{},\"fixtures\":{},\"ambient\":[],\"classSeconds\":{},\"fixtureSeconds\":{}}";

    /// <summary>
    /// Asks MSBuild for the test assembly path. A multi-targeted project has no single path, so
    /// the first target framework is used and returned for the test run.
    /// </summary>
    /// <param name="dotnet">Process runner.</param>
    /// <param name="request">What to record.</param>
    private static (string TargetPath, string? Framework) FindTargetPath(DotnetProcess dotnet, RecordRequest request)
    {
        if (request.Options.Framework is { } requested)
        {
            List<string> exact =
            [
                "msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}", $"-p:TargetFramework={requested}",
                "-getProperty:TargetPath",
            ];
            return (dotnet.Capture(exact, request.RepoRoot).Trim(), requested);
        }

        List<string> query =
        [
            "msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}",
            "-getProperty:TargetPath", "-getProperty:TargetFrameworks",
        ];

        using var properties = JsonDocument.Parse(dotnet.Capture(query, request.RepoRoot));
        var values = properties.RootElement.GetProperty("Properties");
        if (values.GetProperty("TargetPath").GetString() is { Length: > 0 } targetPath)
        {
            return (targetPath, null);
        }

        var framework = values.GetProperty("TargetFrameworks").GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? throw new InvalidOperationException($"Could not find the output of {request.TestProject.Path}.");

        query = ["msbuild", request.TestProject.Path, $"-p:Configuration={request.Configuration}", $"-p:TargetFramework={framework}", "-getProperty:TargetPath"];
        return (dotnet.Capture(query, request.RepoRoot).Trim(), framework);
    }

    /// <summary>The project behind each assembly file name of the request's repository, cached per graph.</summary>
    /// <param name="request">What is being recorded.</param>
    private Dictionary<string, ProjectNode> AssemblyOwners(RecordRequest request)
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
    /// <param name="assemblies">Repository assemblies in the test output.</param>
    private List<GeneratedType> ReadGenerated(RecordRequest request, string targetPath, List<string> assemblies)
    {
        var owners = AssemblyOwners(request);
        var generated = new List<GeneratedType>();
        foreach (var assembly in assemblies.Append(targetPath).Distinct(StringComparer.Ordinal))
        {
            var owner = string.Equals(assembly, targetPath, StringComparison.Ordinal)
                ? request.TestProject
                : owners.GetValueOrDefault(Path.GetFileName(assembly));
            var sources = owner is null ? [] : GeneratedSources.Read(assembly);
            if (sources.Count == 0)
            {
                continue;
            }

            var project = Path.GetRelativePath(request.RepoRoot, owner!.Path).Replace('\\', '/');
            generated.AddRange(GeneratedCode.Collect(project, sources));
        }

        LogGenerated(request.TestProject.Path, generated.Count);
        return generated;
    }

    /// <summary>The repository's own assemblies found in the test output, except Sift's runtime.</summary>
    /// <param name="fileNames">Assembly file names of the repository's projects.</param>
    /// <param name="outputDirectory">Test project output directory.</param>
    private static List<string> AssembliesToInstrument(HashSet<string> fileNames, string outputDirectory)
    {
        var runtimeFile = typeof(SiftTracker).Assembly.GetName().Name + ".dll";
        var assemblies = new List<string>();
        foreach (var fileName in fileNames)
        {
            if (string.Equals(fileName, runtimeFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = Path.Combine(outputDirectory, fileName);
            if (File.Exists(path))
            {
                assemblies.Add(path);
            }
        }

        assemblies.Sort(StringComparer.Ordinal);
        return assemblies;
    }

    /// <summary>
    /// Writes run settings that make tests run one at a time and, for VSTest, load
    /// <see cref="SiftInProcCollector"/> into the test host.
    /// </summary>
    /// <param name="directory">Where to write the file.</param>
    /// <param name="withCollector">
    /// True under VSTest, where the collector writes the results at session end, before the host may
    /// be killed. The runtime is in the test output as well; both are the same assembly, so the host
    /// loads it once. Microsoft.Testing.Platform has no collectors.
    /// </param>
    /// <param name="sequential">True to make the tests run one at a time.</param>
    private static string WriteRunSettings(string directory, bool withCollector, bool sequential)
    {
        // Tests run one at a time while recording: code outside every test flow is credited to the
        // test class entered last, which is only right when one class runs at a time.
        var root = new XElement("RunSettings");
        if (sequential)
        {
            root.Add(
                new XElement("RunConfiguration", new XElement("DisableParallelization", "true")),
                new XElement("xUnit",
                    new XElement("ParallelizeTestCollections", "false"),
                    new XElement("MaxParallelThreads", "1")),
                new XElement("NUnit", new XElement("NumberOfTestWorkers", "0")),
                new XElement("MSTest", new XElement("Parallelize", new XElement("Workers", "1"))));
        }

        if (withCollector)
        {
            // The collector type is named, not loaded: it depends on test platform assemblies that only
            // the test host has. SiftTracker lives in the same assembly and has no such dependency.
            var runtime = typeof(SiftTracker).Assembly;
            var collectorName = $"{typeof(SiftTracker).Namespace}.{nameof(SiftInProcCollector)}, {runtime.FullName}";
            root.Add(new XElement("InProcDataCollectionRunSettings",
                new XElement("InProcDataCollectors",
                    new XElement("InProcDataCollector",
                        new XAttribute("friendlyName", "Sift"),
                        new XAttribute("uri", "InProcDataCollector://Sift/Coverage/1.0"),
                        new XAttribute("assemblyQualifiedName", collectorName),
                        new XAttribute("codebase", runtime.Location)))));
        }

        var path = Path.Combine(directory, "sift.runsettings");
        new XDocument(root).Save(path);
        return path;
    }

    /// <summary>Combines the method table with the collectors' per-class hits, merging all shards.</summary>
    /// <param name="request">What was recorded.</param>
    /// <param name="methods">Method table from instrumentation.</param>
    /// <param name="resultsFiles">JSON files written by <see cref="SiftTracker.WriteResults"/>, one per shard.</param>
    private CoverageMap BuildMap(RecordRequest request, IReadOnlyList<CoveredMethod> methods, IReadOnlyList<string> resultsFiles)
    {
        var merged = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var mergedFixtures = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var ambientIds = new SortedSet<int>();
        var classSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var fixtureSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var startupSeconds = new List<double>();
        var initialization = new SortedSet<int>();
        var sharedInProcess = new SortedSet<int>();
        var builtClasses = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var builtFixtures = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var builtAmbient = new SortedSet<int>();

        foreach (var file in resultsFiles)
        {
            using var document = ReadResults(file);
            var root = document.RootElement;
            // Ids past the methods are tracked fields a bucket read; what built their values in this
            // process counts for the bucket through the value.
            var fieldReads = new FieldReads(root.TryGetProperty("methodCount", out var count) ? count.GetInt32() : methods.Count, Builders(root));
            MergeBuckets(root.GetProperty("classes"), merged, builtClasses, fieldReads);
            MergeBuckets(root.GetProperty("fixtures"), mergedFixtures, builtFixtures, fieldReads);
            AddShared(root, sharedInProcess, fieldReads.MethodCount);
            if (root.TryGetProperty("initialization", out var initializing))
            {
                foreach (var id in initializing.EnumerateArray())
                {
                    initialization.Add(id.GetInt32());
                }
            }
            foreach (var id in root.GetProperty("ambient").EnumerateArray())
            {
                if (!fieldReads.Add(id.GetInt32(), builtAmbient))
                {
                    ambientIds.Add(id.GetInt32());
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

        var classes = Freeze(merged);
        var fixtures = Freeze(mergedFixtures);
        int[] ambient = [.. ambientIds];

        var commit = new GitClient(loggerFactory.CreateLogger<GitClient>()).Head(request.RepoRoot);
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
            SharedInProcess = [.. sharedInProcess],
            BuiltValues = new CoverageHits(Freeze(builtClasses), Freeze(builtFixtures), [.. builtAmbient]),
        };
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

            if (Builders.TryGetValue(id - MethodCount, out var builders))
            {
                built.UnionWith(builders.Where(b => b < MethodCount));
            }

            return true;
        }
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
    /// Adds the methods that one process's results show in two or more test classes or fixtures.
    /// Counting per process matters: code that runs once and is cached shows up in one bucket of
    /// every process, which across several processes looks like shared code.
    /// </summary>
    /// <param name="root">One process's results.</param>
    /// <param name="shared">Methods shared by buckets of one process; updated in place.</param>
    /// <param name="methodCount">Ids from here on are fields, which are not code that ran.</param>
    private static void AddShared(JsonElement root, SortedSet<int> shared, int methodCount)
    {
        var seen = new HashSet<int>();
        foreach (var name in (ReadOnlySpan<string>)["classes", "fixtures"])
        {
            foreach (var bucket in root.GetProperty(name).EnumerateObject())
            {
                foreach (var id in bucket.Value.EnumerateArray())
                {
                    if (id.GetInt32() < methodCount && !seen.Add(id.GetInt32()))
                    {
                        shared.Add(id.GetInt32());
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adds the named id arrays of <paramref name="element"/> to <paramref name="buckets"/>. A fixture
    /// can appear in several shards, so ids are unioned.
    /// </summary>
    /// <param name="element">JSON object written by <see cref="SiftTracker.WriteResults"/>.</param>
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
    /// <param name="file">Results file written by <see cref="SiftTracker.WriteResults"/>.</param>
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{TestClass} ran but left no coverage; it will always be selected")]
    private partial void LogUnrecordedClass(string testClass);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project}: {Count} generated type(s) read from PDBs")]
    private partial void LogGenerated(string project, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Instrumenting {Count} assembly(ies) in {Directory}")]
    private partial void LogAssemblies(int count, string directory);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Recorded {Project}: {ClassCount} test class(es), {FixtureCount} fixture(s), {MethodCount} method(s)")]
    private partial void LogRecorded(string project, int classCount, int fixtureCount, int methodCount);
}
