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
public sealed record RecordRequest(string RepoRoot, ProjectGraph Graph, ProjectNode TestProject, string Configuration);

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
            var resultsFile = Path.Combine(work, "coverage.json");
            List<string> testArguments = ["test", project, "--no-build", "-c", request.Configuration, "--settings", settings];
            if (framework is not null)
            {
                testArguments.AddRange(["-f", framework]);
            }

            var testExit = dotnet.Run(
                testArguments,
                root,
                new Dictionary<string, string>
                {
                    [SiftTracker.MethodCountVariable] = instrumentation.Methods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [SiftTracker.OutputVariable] = resultsFile,
                },
                output);

            if (!File.Exists(resultsFile))
            {
                throw new InvalidOperationException(
                    $"The test host did not write coverage for {project}. Only VSTest runs are supported for recording.");
            }

            var map = BuildMap(request, instrumentation.Methods, resultsFile);
            return (map, testExit == 0);
        }
        finally
        {
            instrumenter.Restore(instrumentation);
            Directory.Delete(work, recursive: true);
        }
    }

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

    /// <summary>Combines the method table with the collector's per-class hits.</summary>
    /// <param name="request">What was recorded.</param>
    /// <param name="methods">Method table from instrumentation.</param>
    /// <param name="resultsFile">JSON written by <see cref="SiftTracker.WriteResults"/>.</param>
    private CoverageMap BuildMap(RecordRequest request, IReadOnlyList<CoveredMethod> methods, string resultsFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(resultsFile));
        var classes = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.GetProperty("classes").EnumerateObject())
        {
            classes[property.Name] = [.. property.Value.EnumerateArray().Select(static e => e.GetInt32())];
        }

        int[] ambient = [.. document.RootElement.GetProperty("ambient").EnumerateArray().Select(static e => e.GetInt32())];
        var commit = new GitClient(loggerFactory.CreateLogger<GitClient>()).Head(request.RepoRoot);
        LogRecorded(request.TestProject.Path, classes.Count, methods.Count);

        return new CoverageMap(
            Path.GetRelativePath(request.RepoRoot, request.TestProject.Path).Replace('\\', '/'),
            commit,
            DateTimeOffset.UtcNow,
            methods,
            classes,
            ambient);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Instrumenting {Count} assembly(ies) in {Directory}")]
    private partial void LogAssemblies(int count, string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Recorded {Project}: {ClassCount} test class(es), {MethodCount} method(s)")]
    private partial void LogRecorded(string project, int classCount, int methodCount);
}
