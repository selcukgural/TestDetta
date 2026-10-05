using System.Text.Json;
using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Eval;

/// <summary>
/// Synthetic changes to a project that other projects use as a source generator
/// (docs/design/generator-mutants.md): which they are, which consumers compile with the generator, and
/// which evaluated test projects see what it generates.
/// </summary>
/// <param name="graph">Projects of the clone (or of a copy of it, whose paths the targets are mapped to).</param>
internal sealed class GeneratorMutants(ProjectGraph graph)
{
    /// <summary>The marker class of a test project that does not build with a generator mutant, as <c>Project: (does not build)</c> spells it.</summary>
    public const string DoesNotBuild = "(does not build)";

    /// <summary>Consumers per generator project, from every project's <see cref="ProjectNode.GeneratorReferences"/>.</summary>
    private readonly Dictionary<string, List<ProjectNode>> _consumers = Consumers(graph);

    /// <summary>The projects compiling a file of the change that some project uses as a generator.</summary>
    /// <param name="files">The changed files, in the graph's repository.</param>
    public List<ProjectNode> Generators(IEnumerable<string> files) =>
        [.. files
            .SelectMany(f => graph.ResolveFile(Paths.Normalize(f)))
            .Where(o => o.Role == FileRole.Source && _consumers.ContainsKey(o.Project.Path))
            .Select(static o => o.Project)
            .DistinctBy(static p => p.Path, Paths.Comparer)];

    /// <summary>
    /// The test projects among <paramref name="testProjects"/> that are, or depend on, a consumer of a
    /// generator the change compiles into; empty for a change that reaches no generator.
    /// </summary>
    /// <param name="files">The changed files.</param>
    /// <param name="testProjects">Evaluated test projects.</param>
    public List<ProjectNode> TestProjectsReached(IEnumerable<string> files, IEnumerable<ProjectNode> testProjects)
    {
        var consumers = Generators(files).SelectMany(g => _consumers[g.Path]).Select(static c => c.Path);
        var downstream = graph.WithDependents(consumers);
        return [.. testProjects.Where(t => downstream.Contains(t.Path))];
    }

    /// <summary>
    /// The projects to build, dependencies first and <paramref name="testProject"/> last, for it to
    /// compile again with the generators of the change: the consumers it depends on (or is) and the
    /// projects between them and it. Built one by one without their dependencies, so nothing else
    /// compiles again: a source written back after the selection is newer than the build, and a full
    /// build would compile the generator again over the mutant in its assemblies.
    /// </summary>
    /// <param name="files">The changed files.</param>
    /// <param name="testProject">A test project among <see cref="TestProjectsReached"/>.</param>
    public List<ProjectNode> BuildOrder(IEnumerable<string> files, ProjectNode testProject)
    {
        var downstream = graph.WithDependents(Generators(files).SelectMany(g => _consumers[g.Path]).Select(static c => c.Path));
        var order = new List<ProjectNode>();
        var seen = new HashSet<string>(Paths.Comparer);
        Visit(testProject);
        return order;

        // Everything between a consumer and the test project depends on the consumer, so it is downstream too.
        void Visit(ProjectNode project)
        {
            if (!seen.Add(project.Path))
            {
                return;
            }

            foreach (var reference in project.References)
            {
                if (graph.Find(reference) is { } dependency && downstream.Contains(dependency.Path))
                {
                    Visit(dependency);
                }
            }

            order.Add(project);
        }
    }

    /// <summary>
    /// The built assembly of a generator project as its consumers' compilers load it (its
    /// <c>TargetPath</c>) and its intermediate copy in <c>obj</c>, which an incremental build copies
    /// over it, for each framework it targets; only files that exist.
    /// </summary>
    /// <param name="generator">The generator project.</param>
    /// <param name="logger">Receives the MSBuild runs.</param>
    public static List<string> Assemblies(ProjectNode generator, ILogger logger)
    {
        var dotnet = new DotnetProcess(logger);
        var found = new List<string>();
        var outer = Properties(dotnet, generator, framework: null);
        // A project with one framework has a TargetPath in its outer build; one with several, per framework.
        List<string?> frameworks = [];
        if (outer.GetValueOrDefault("TargetPath") is { Length: > 0 })
        {
            frameworks.Add(null);
        }
        else
        {
            frameworks.AddRange((outer.GetValueOrDefault("TargetFrameworks") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        foreach (var framework in frameworks)
        {
            var properties = framework is null ? outer : Properties(dotnet, generator, framework);
            if (properties.GetValueOrDefault("TargetPath") is not { Length: > 0 } targetPath)
            {
                continue;
            }

            var intermediate = Path.Combine(generator.Directory, properties.GetValueOrDefault("IntermediateOutputPath") ?? string.Empty, Path.GetFileName(targetPath));
            found.AddRange(new[] { targetPath, intermediate }.Select(Path.GetFullPath).Where(File.Exists));
        }

        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Gives the generator's assemblies the time of now, so every consumer compiles again with them.</summary>
    /// <param name="assemblies">The assemblies.</param>
    public static void Touch(IEnumerable<string> assemblies)
    {
        var now = DateTime.UtcNow;
        foreach (var assembly in assemblies)
        {
            File.SetLastWriteTimeUtc(assembly, now);
        }
    }

    /// <summary>
    /// <c>dotnet build</c> arguments that compile in a new process with the build's environment (the
    /// mutant switch): a compiler server would keep the generator it loaded before, since a rewritten
    /// assembly keeps its MVID, and a reused MSBuild node may start the compiler with an older environment.
    /// </summary>
    public static IReadOnlyList<string> NoCompilerServer { get; } = ["-p:UseSharedCompilation=false", "-nr:false"];

    /// <summary>The properties of the generator project the assemblies are found from, for one framework or the outer build.</summary>
    /// <param name="dotnet">Runs MSBuild.</param>
    /// <param name="project">The project.</param>
    /// <param name="framework">Framework, or null for the outer build.</param>
    private static Dictionary<string, string> Properties(DotnetProcess dotnet, ProjectNode project, string? framework)
    {
        List<string> query = ["msbuild", project.Path, "-p:Configuration=Debug", "-getProperty:TargetPath", "-getProperty:IntermediateOutputPath", "-getProperty:TargetFrameworks"];
        if (framework is not null)
        {
            query.Add($"-p:TargetFramework={framework}");
        }

        using var json = JsonDocument.Parse(dotnet.Capture(query, project.Directory));
        return json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(static p => p.Name, static p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    /// <summary>The projects that use each project as a generator.</summary>
    /// <param name="graph">The projects.</param>
    private static Dictionary<string, List<ProjectNode>> Consumers(ProjectGraph graph)
    {
        var consumers = new Dictionary<string, List<ProjectNode>>(Paths.Comparer);
        foreach (var project in graph.Projects)
        {
            foreach (var generator in project.GeneratorReferences.Where(g => graph.Find(g) is not null))
            {
                if (!consumers.TryGetValue(generator, out var list))
                {
                    consumers[generator] = list = [];
                }

                list.Add(project);
            }
        }

        return consumers;
    }
}
