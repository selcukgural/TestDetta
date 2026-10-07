using Microsoft.Extensions.Logging;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Assembly names of the repository's projects, used to find the repository's own assemblies in a
/// test output. Names the project graph could not read statically are asked from MSBuild.
/// </summary>
/// <param name="logger">Receives the MSBuild queries and their answers.</param>
internal sealed partial class ProjectAssemblies(ILogger logger)
{
    /// <summary>
    /// File names (<c>Name.dll</c>) of every project's assembly, compared case-insensitively. MSBuild
    /// is started once per project whose name needs evaluation, so repositories without such
    /// projects pay nothing.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root, the working directory of MSBuild.</param>
    public HashSet<string> FileNames(ProjectGraph graph, string repoRoot) =>
        new(Owners(graph, repoRoot).Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every project that compiles each assembly file name, in graph order. Several projects may share
    /// one name (variants of a library built against different dependencies, each with its own test
    /// project); which of them a test output holds depends on the test project
    /// (<see cref="GeneratedSourceReader.CompiledAgainst"/>). A project whose name needs evaluation is
    /// listed under both its file name (the default) and the evaluated name.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root, the working directory of MSBuild.</param>
    public Dictionary<string, List<ProjectNode>> Owners(ProjectGraph graph, string repoRoot)
    {
        var dotnet = new DotnetProcess(logger);
        var names = new Dictionary<string, List<ProjectNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in graph.Projects)
        {
            Add(names, project.AssemblyName + ".dll", project);
            if (!project.AssemblyNameUnresolved)
            {
                continue;
            }

            // The file name stays in the set: it is the default and costs nothing when no such file exists.
            string evaluated;
            try
            {
                evaluated = dotnet.Capture(["msbuild", project.Path, "-getProperty:AssemblyName"], repoRoot).Trim();
            }
            catch (InvalidOperationException ex)
            {
                LogQueryFailed(project.Path, ex.Message);
                continue;
            }

            LogEvaluated(project.Path, evaluated);
            if (evaluated.Length > 0)
            {
                Add(names, evaluated + ".dll", project);
            }
        }

        return names;
    }

    /// <summary>Lists <paramref name="project"/> under <paramref name="fileName"/>, once.</summary>
    /// <param name="names">Owners by file name.</param>
    /// <param name="fileName">Assembly file name.</param>
    /// <param name="project">A project that compiles it.</param>
    private static void Add(Dictionary<string, List<ProjectNode>> names, string fileName, ProjectNode project)
    {
        if (!names.TryGetValue(fileName, out var owners))
        {
            names[fileName] = owners = [];
        }

        if (!owners.Contains(project))
        {
            owners.Add(project);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "AssemblyName of {Project} evaluated by MSBuild: {Name}")]
    private partial void LogEvaluated(string project, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not evaluate the AssemblyName of {Project}; its assembly may not be recorded: {Reason}")]
    private partial void LogQueryFailed(string project, string reason);
}
