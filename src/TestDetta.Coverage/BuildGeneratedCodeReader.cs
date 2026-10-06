using Microsoft.Extensions.Logging;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>How <see cref="BuildGeneratedCodeReader"/> gets a build of the current sources.</summary>
/// <param name="Build">
/// True to build the test project (<c>td test</c>: <c>dotnet test</c> would build it anyway, and an
/// up-to-date build costs MSBuild seconds); false to use only a build that is newer than the sources
/// (<c>td affected</c>, which never builds unless asked).
/// </param>
/// <param name="Configuration">Configuration to build and read, as <c>dotnet test -c</c> gets it.</param>
public sealed record GeneratedCodeBuild(bool Build, string Configuration = "Debug")
{
    /// <summary>More <c>dotnet build</c> arguments, such as MSBuild properties; none by default.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>
/// Reads what source generators write into a test project's build of the current sources, for the
/// selection to compare with the map when a change reaches a generator in the repository
/// (docs/design/generator-output-changes.md). It reads the same files the recorder reads, through
/// <see cref="GeneratedSourceReader"/>, for the framework the map was recorded for.
/// </summary>
/// <param name="loggerFactory">Creates the logger of each read and of the dotnet runs.</param>
/// <param name="build">Whether to build, and the configuration.</param>
public sealed partial class BuildGeneratedCodeReader(ILoggerFactory loggerFactory, GeneratedCodeBuild build) : IGeneratedCodeReader
{
    /// <summary>Longest a build may take; past it the build counts as failed.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Files whose change makes a build out of date.</summary>
    private static readonly string[] InputExtensions = [".cs", ".csproj", ".props", ".targets"];

    private readonly ILogger _logger = loggerFactory.CreateLogger<BuildGeneratedCodeReader>();

    /// <inheritdoc />
    public GeneratedCodeRead Read(ProjectGraph graph, string repoRoot, ProjectNode testProject, string? framework)
    {
        var name = Path.GetFileName(testProject.Path);
        var dotnet = new DotnetProcess(_logger);
        var directory = DotnetTestLocation.For(repoRoot, testProject.Path).FullDirectory(repoRoot);
        var targetPath = TargetPath(dotnet, testProject, framework, directory);
        if (targetPath is null)
        {
            return GeneratedCodeRead.NotAvailable($"MSBuild did not say where {name} builds to");
        }

        if (build.Build)
        {
            LogBuilding(testProject.Path, build.Configuration, framework ?? "its only framework");
            List<string> arguments = ["build", testProject.Path, "-c", build.Configuration];
            if (framework is not null)
            {
                arguments.AddRange(["-f", framework]);
            }

            arguments.AddRange(build.Arguments);
            using var output = new StringWriter();
            var exitCode = dotnet.Run(arguments, directory, new Dictionary<string, string>(), output, BuildTimeout);
            if (exitCode != 0)
            {
                return GeneratedCodeRead.Failed(Errors(output.ToString(), exitCode));
            }
        }
        else if (!IsCurrent(graph, testProject, targetPath))
        {
            return GeneratedCodeRead.NotAvailable(
                $"no build of it is newer than its sources (build it with -c {build.Configuration} first, or run with --build)");
        }
        else
        {
            LogUsingBuild(testProject.Path, targetPath);
        }

        if (!GeneratedSources.HasPdb(targetPath))
        {
            return GeneratedCodeRead.NotAvailable($"{Path.GetFileName(targetPath)} has no portable PDB to read generated code from (set DebugType to portable or embedded)");
        }

        var owners = new ProjectAssemblies(_logger).Owners(graph, repoRoot);
        var assemblies = GeneratedSourceReader.RepositoryAssemblies(GeneratedSourceReader.CompiledAgainst(graph, testProject, owners), Path.GetDirectoryName(targetPath)!);
        var sources = GeneratedSourceReader.Read(repoRoot, testProject, targetPath, owners, assemblies);
        LogRead(testProject.Path, sources.Count, assemblies.Count + 1);
        var tracked = new GitClient(loggerFactory.CreateLogger<GitClient>()).TrackedFiles(repoRoot).ToHashSet(StringComparer.Ordinal);
        return GeneratedCodeRead.Read(sources) with
        {
            BuildInputs = GeneratedSourceReader.ReadBuildInputs(repoRoot, testProject, targetPath, owners, assemblies, tracked),
        };
    }

    /// <summary>The test assembly's path for the configuration and framework, asked from MSBuild; null when it gives none.</summary>
    /// <param name="dotnet">Runs dotnet.</param>
    /// <param name="testProject">The test project.</param>
    /// <param name="framework">Target framework, or null for the project's only one.</param>
    /// <param name="directory">Where dotnet runs (the project's global.json).</param>
    private string? TargetPath(DotnetProcess dotnet, ProjectNode testProject, string? framework, string directory)
    {
        List<string> query = ["msbuild", testProject.Path, $"-p:Configuration={build.Configuration}", "-getProperty:TargetPath"];
        if (framework is not null)
        {
            query.Add($"-p:TargetFramework={framework}");
        }

        try
        {
            var path = dotnet.Capture(query, directory).Trim();
            return path.Length > 0 ? path : null;
        }
        catch (InvalidOperationException ex)
        {
            LogNoTargetPath(testProject.Path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// True when the test assembly exists and is newer than every source of the test project and of the
    /// projects it depends on: what the generators wrote into it is what they write now.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="testProject">The test project.</param>
    /// <param name="targetPath">Its test assembly.</param>
    private static bool IsCurrent(ProjectGraph graph, ProjectNode testProject, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return false;
        }

        var built = File.GetLastWriteTimeUtc(targetPath);
        var seen = new HashSet<string>(Paths.Comparer);
        var pending = new Stack<ProjectNode>([testProject]);
        while (pending.TryPop(out var project))
        {
            if (!seen.Add(project.Path))
            {
                continue;
            }

            if (NewestInput(project.Directory) > built)
            {
                return false;
            }

            foreach (var reference in project.References)
            {
                if (graph.Find(reference) is { } node)
                {
                    pending.Push(node);
                }
            }
        }

        return true;
    }

    /// <summary>Last write time of the newest source or build file under a project's directory, outside <c>bin</c> and <c>obj</c>.</summary>
    /// <param name="directory">Project directory.</param>
    private static DateTime NewestInput(string directory)
    {
        var newest = DateTime.MinValue;
        var pending = new Stack<string>([directory]);
        while (pending.TryPop(out var current))
        {
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (Array.Exists(InputExtensions, extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    newest = written > newest ? written : newest;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(child);
                if (name is not ("bin" or "obj") && !name.StartsWith('.'))
                {
                    pending.Push(child);
                }
            }
        }

        return newest;
    }

    /// <summary>The compiler's errors from a build's output, each once; the end of the output when it names none.</summary>
    /// <param name="output">Everything the build wrote.</param>
    /// <param name="exitCode">Its exit code.</param>
    private static string Errors(string output, int exitCode)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var errors = lines.Where(static l => l.Contains(": error ", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(10).ToList();
        if (errors.Count == 0)
        {
            errors = exitCode == DotnetProcess.TimedOut
                ? [$"the build did not finish within {BuildTimeout.TotalMinutes:0} minutes"]
                : [.. lines.TakeLast(10)];
        }

        return string.Join(Environment.NewLine, errors);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Building {Project} ({Configuration}, {Framework}) to read what its source generators write now")]
    private partial void LogBuilding(string project, string configuration, string framework);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Project}: reading generated code from the existing build {TargetPath}, which is newer than its sources")]
    private partial void LogUsingBuild(string project, string targetPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project}: {Count} generated file(s) read from {Assemblies} assembly(ies)")]
    private partial void LogRead(string project, int count, int assemblies);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Project}: MSBuild did not give its target path: {Error}")]
    private partial void LogNoTargetPath(string project, string error);
}
