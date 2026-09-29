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
/// <param name="Configuration">Build configuration, such as <c>Debug</c>.</param>
/// <param name="Parallelism">
/// Number of test host processes to spread the test classes over. Each process still runs its
/// tests one at a time, which is what keeps attribution exact; the suite must tolerate several
/// processes at once (no fixed ports or shared local databases).
/// </param>
public sealed record RecordRequest(
    string RepoRoot,
    ProjectGraph Graph,
    ProjectNode TestProject,
    string Configuration,
    int Parallelism = 1);

/// <summary>
/// Builds a test project, instruments the repository's assemblies in its output, runs the tests
/// with <see cref="SiftInProcCollector"/> attached and turns the result into a <see cref="CoverageMap"/>.
/// The original assemblies are always restored.
/// </summary>
/// <param name="loggerFactory">Creates the loggers of the steps.</param>
public sealed partial class CoverageRecorder(ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CoverageRecorder>();

    /// <summary>Records the coverage map of one test project.</summary>
    /// <param name="request">What to record.</param>
    /// <param name="output">Receives build and test output.</param>
    /// <returns>The map, and whether all tests passed while recording.</returns>
    /// <exception cref="InvalidOperationException">The build failed, or the test host did not load the collector.</exception>
    public (CoverageMap Map, bool TestsPassed) Record(RecordRequest request, TextWriter output)
    {
        var dotnet = new DotnetProcess(_logger);
        var project = request.TestProject.Path;
        var root = request.RepoRoot;

        var buildExit = dotnet.Run(["build", project, "-c", request.Configuration], root, new Dictionary<string, string>(), output);
        if (buildExit != 0)
        {
            throw new InvalidOperationException($"Building {project} failed with exit code {buildExit}.");
        }

        var (targetPath, framework) = FindTargetPath(dotnet, request);
        var outputDirectory = Path.GetDirectoryName(targetPath)!;
        var assemblies = AssembliesToInstrument(request.Graph, outputDirectory);
        LogAssemblies(assemblies.Count, outputDirectory);

        var work = Path.Combine(Path.GetTempPath(), "sift-record-" + Guid.NewGuid().ToString("N"));
        var instrumenter = new AssemblyInstrumenter(_logger);
        var testAssemblies = new HashSet<string>(StringComparer.Ordinal) { targetPath };
        var instrumentation = instrumenter.Instrument(assemblies, testAssemblies, Path.Combine(work, "backup"));

        try
        {
            var settings = WriteRunSettings(work);
            List<string> testArguments = ["test", project, "--no-build", "-c", request.Configuration, "--settings", settings];
            if (framework is not null)
            {
                testArguments.AddRange(["-f", framework]);
            }

            var shards = Shards(request);
            var synchronizedOutput = TextWriter.Synchronized(output);
            var runs = new Task<(int ExitCode, string ResultsFile)>[shards.Count];
            for (var i = 0; i < shards.Count; i++)
            {
                var shard = new ShardRun(dotnet, testArguments, shards[i], i, shards.Count);
                runs[i] = Task.Run(() => RunShard(shard, request, instrumentation, work, synchronizedOutput));
            }

            Task.WaitAll(runs);

            var resultsFiles = new List<string>(runs.Length);
            foreach (var run in runs)
            {
                if (!File.Exists(run.Result.ResultsFile))
                {
                    throw new InvalidOperationException(
                        $"The test host did not write coverage for {project}. Only VSTest runs are supported for recording.");
                }

                resultsFiles.Add(run.Result.ResultsFile);
            }

            var map = BuildMap(request, instrumentation.Methods, resultsFiles);
            return (map, runs.All(static r => r.Result.ExitCode == 0));
        }
        finally
        {
            instrumenter.Restore(instrumentation);
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>One test host process of a sharded recording.</summary>
    /// <param name="Dotnet">Process runner.</param>
    /// <param name="Arguments">Shared <c>dotnet test</c> arguments.</param>
    /// <param name="Filter">Filter selecting this shard's tests, or null to run everything.</param>
    /// <param name="Index">Shard number, for output prefixes and file names.</param>
    /// <param name="Count">Number of shards.</param>
    private sealed record ShardRun(DotnetProcess Dotnet, List<string> Arguments, string? Filter, int Index, int Count);

    /// <summary>
    /// Filters for the shards: the known test classes dealt round-robin into
    /// <see cref="RecordRequest.Parallelism"/> groups, plus one shard for any test outside the known
    /// classes, so a class the source index misses is still recorded. A single shard runs unfiltered.
    /// </summary>
    /// <param name="request">What to record.</param>
    private List<string?> Shards(RecordRequest request)
    {
        if (request.Parallelism <= 1)
        {
            return [null];
        }

        var classes = new TestClassFinder(loggerFactory.CreateLogger<TestClassFinder>())
            .Find(request.Graph, request.RepoRoot, request.TestProject);
        if (classes.Count <= 1)
        {
            return [null];
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

        var filters = new List<string?>(groupCount + 1);
        foreach (var group in groups)
        {
            filters.Add(TestFilter.ForClasses(group));
        }

        filters.Add(TestFilter.ExcludingClasses(classes));
        LogShards(request.TestProject.Path, groupCount, classes.Count);
        return filters;
    }

    /// <summary>Runs one shard with its own results file, prefixing its output lines with the shard number.</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="request">What is recorded.</param>
    /// <param name="instrumentation">Instrumentation result, for the method count.</param>
    /// <param name="work">Work directory.</param>
    /// <param name="output">Shared, synchronized output.</param>
    private static (int ExitCode, string ResultsFile) RunShard(
        ShardRun shard,
        RecordRequest request,
        InstrumentationResult instrumentation,
        string work,
        TextWriter output)
    {
        var resultsFile = Path.Combine(work, $"coverage-{shard.Index}.json");
        List<string> arguments = [.. shard.Arguments];
        if (shard.Filter is not null)
        {
            arguments.AddRange(["--filter", shard.Filter]);
        }

        var environment = new Dictionary<string, string>
        {
            [SiftTracker.MethodCountVariable] = instrumentation.Methods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SiftTracker.OutputVariable] = resultsFile,
        };

        var writer = shard.Count == 1 ? output : new PrefixWriter(output, $"[shard {shard.Index + 1}/{shard.Count}] ");
        var exitCode = shard.Dotnet.Run(arguments, request.RepoRoot, environment, writer);

        // The catch-all shard legitimately matches no test when the source index knew every class,
        // and then the test host never starts the collector.
        var isCatchAll = shard.Count > 1 && shard.Index == shard.Count - 1;
        if (isCatchAll && !File.Exists(resultsFile))
        {
            File.WriteAllText(resultsFile, EmptyResults);
        }

        return (exitCode, resultsFile);
    }

    /// <summary>Results of a test host that ran no test.</summary>
    private const string EmptyResults = "{\"classes\":{},\"fixtures\":{},\"ambient\":[]}";

    /// <summary>
    /// Asks MSBuild for the test assembly path. A multi-targeted project has no single path, so
    /// the first target framework is used and returned for the test run.
    /// </summary>
    /// <param name="dotnet">Process runner.</param>
    /// <param name="request">What to record.</param>
    private static (string TargetPath, string? Framework) FindTargetPath(DotnetProcess dotnet, RecordRequest request)
    {
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

    /// <summary>The repository's own assemblies found in the test output, except Sift's runtime.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="outputDirectory">Test project output directory.</param>
    private static List<string> AssembliesToInstrument(ProjectGraph graph, string outputDirectory)
    {
        var runtimeName = typeof(SiftTracker).Assembly.GetName().Name;
        var assemblies = new List<string>();
        foreach (var project in graph.Projects)
        {
            if (project.AssemblyName == runtimeName)
            {
                continue;
            }

            var path = Path.Combine(outputDirectory, project.AssemblyName + ".dll");
            if (File.Exists(path))
            {
                assemblies.Add(path);
            }
        }

        assemblies.Sort(StringComparer.Ordinal);
        return assemblies;
    }

    /// <summary>Writes run settings that load <see cref="SiftInProcCollector"/> into the test host.</summary>
    /// <param name="directory">Where to write the file.</param>
    private static string WriteRunSettings(string directory)
    {
        // The collector type is named, not loaded: it depends on test platform assemblies that only
        // the test host has. SiftTracker lives in the same assembly and has no such dependency.
        var runtime = typeof(SiftTracker).Assembly;
        var collectorName = $"{typeof(SiftTracker).Namespace}.{nameof(SiftInProcCollector)}, {runtime.FullName}";
        // Tests run one at a time while recording: a hit can only be credited to the tests running at
        // that moment, and with parallel classes every class would get everyone's hits.
        var settings = new XDocument(
            new XElement("RunSettings",
                new XElement("RunConfiguration", new XElement("DisableParallelization", "true")),
                new XElement("xUnit",
                    new XElement("ParallelizeTestCollections", "false"),
                    new XElement("MaxParallelThreads", "1")),
                new XElement("NUnit", new XElement("NumberOfTestWorkers", "0")),
                new XElement("MSTest", new XElement("Parallelize", new XElement("Workers", "1"))),
                new XElement("InProcDataCollectionRunSettings",
                    new XElement("InProcDataCollectors",
                        new XElement("InProcDataCollector",
                            new XAttribute("friendlyName", "Sift"),
                            new XAttribute("uri", "InProcDataCollector://Sift/Coverage/1.0"),
                            new XAttribute("assemblyQualifiedName", collectorName),
                            new XAttribute("codebase", runtime.Location))))));

        var path = Path.Combine(directory, "sift.runsettings");
        settings.Save(path);
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

        foreach (var file in resultsFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = document.RootElement;
            MergeBuckets(root.GetProperty("classes"), merged);
            MergeBuckets(root.GetProperty("fixtures"), mergedFixtures);
            foreach (var id in root.GetProperty("ambient").EnumerateArray())
            {
                ambientIds.Add(id.GetInt32());
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
        return new CoverageMap(header, methods, new CoverageHits(classes, fixtures, ambient));
    }

    /// <summary>
    /// Adds the named id arrays of <paramref name="element"/> to <paramref name="buckets"/>. A fixture
    /// can appear in several shards, so ids are unioned.
    /// </summary>
    /// <param name="element">JSON object written by <see cref="SiftTracker.WriteResults"/>.</param>
    /// <param name="buckets">Merged buckets; updated in place.</param>
    private static void MergeBuckets(JsonElement element, Dictionary<string, SortedSet<int>> buckets)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!buckets.TryGetValue(property.Name, out var ids))
            {
                ids = [];
                buckets[property.Name] = ids;
            }

            foreach (var id in property.Value.EnumerateArray())
            {
                ids.Add(id.GetInt32());
            }
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Instrumenting {Count} assembly(ies) in {Directory}")]
    private partial void LogAssemblies(int count, string directory);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Recorded {Project}: {ClassCount} test class(es), {FixtureCount} fixture(s), {MethodCount} method(s)")]
    private partial void LogRecorded(string project, int classCount, int fixtureCount, int methodCount);
}
