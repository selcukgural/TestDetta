using TestDetta.Core;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Coverage;

/// <summary>
/// Reads the code source generators wrote into a test project's build: into the test assembly and
/// into the repository's assemblies in its output. Recording stores it in the map; the selection reads
/// it again from a current build to see what a changed generator writes now
/// (docs/design/generator-output-changes.md). Both must read the same files, so both use this.
/// </summary>
internal static class GeneratedSourceReader
{
    /// <summary>
    /// The assembly file names of <paramref name="owners"/> whose projects the test project compiles
    /// against, itself or through the projects it compiles against. A project referenced without
    /// compiling against it may still have its output copied next to the tests (LibGit2Sharp's test
    /// app), to run as another process: it is not instrumented, since an instrumented copy run outside
    /// the test host breaks, and its use is recorded as a process start (docs/design/runtime-file-reads.md).
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="testProject">The test project.</param>
    /// <param name="owners">Project of each repository assembly, by file name.</param>
    public static IEnumerable<string> CompiledAgainst(ProjectGraph graph, ProjectNode testProject, IReadOnlyDictionary<string, ProjectNode> owners)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal) { testProject.Path };
        var queue = new Queue<ProjectNode>([testProject]);
        while (queue.TryDequeue(out var project))
        {
            foreach (var reference in project.References.Where(r => !project.OpaqueReferences.Contains(r)))
            {
                if (closure.Add(reference) && graph.Find(reference) is { } node)
                {
                    queue.Enqueue(node);
                }
            }
        }

        return owners.Where(o => closure.Contains(o.Value.Path)).Select(static o => o.Key);
    }

    /// <summary>The repository's own assemblies found in a test output directory, except TestDetta's runtime.</summary>
    /// <param name="fileNames">Assembly file names of the repository's projects.</param>
    /// <param name="outputDirectory">Test project output directory.</param>
    public static List<string> RepositoryAssemblies(IEnumerable<string> fileNames, string outputDirectory)
    {
        var runtimeFile = typeof(TestDettaTracker).Assembly.GetName().Name + ".dll";
        var assemblies = new List<string>();
        foreach (var fileName in fileNames.Distinct(StringComparer.OrdinalIgnoreCase))
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

    /// <summary>The generated files of the test assembly and of <paramref name="assemblies"/>, by the project that compiled each.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="testProject">The test project.</param>
    /// <param name="targetPath">Its compiled test assembly.</param>
    /// <param name="owners">Project of each repository assembly, by file name.</param>
    /// <param name="assemblies">Repository assemblies in the test output.</param>
    public static List<GeneratedSource> Read(
        string repoRoot,
        ProjectNode testProject,
        string targetPath,
        IReadOnlyDictionary<string, ProjectNode> owners,
        IReadOnlyList<string> assemblies)
    {
        var generated = new List<GeneratedSource>();
        foreach (var assembly in assemblies.Append(targetPath).Distinct(StringComparer.Ordinal))
        {
            var owner = string.Equals(assembly, targetPath, StringComparison.Ordinal)
                ? testProject
                : owners.GetValueOrDefault(Path.GetFileName(assembly));
            var sources = owner is null ? [] : GeneratedSources.Read(assembly);
            if (sources.Count == 0)
            {
                continue;
            }

            var project = Path.GetRelativePath(repoRoot, owner!.Path).Replace('\\', '/');
            generated.AddRange(sources.Select(source => new GeneratedSource(project, Name(repoRoot, source.Name), source.Text)));
        }

        return generated;
    }

    /// <summary>
    /// What the build fed the compiler of the test assembly and of <paramref name="assemblies"/> from
    /// outside the files git tracks (<see cref="GeneratedSources.ReadBuildInputs"/>), by project path
    /// relative to the repository. Recording and the selection read it the same way.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="testProject">The test project.</param>
    /// <param name="targetPath">Its compiled test assembly.</param>
    /// <param name="owners">Project of each repository assembly, by file name.</param>
    /// <param name="assemblies">Repository assemblies in the test output.</param>
    /// <param name="tracked">Files git tracks, relative, with '/' separators.</param>
    public static Dictionary<string, IReadOnlyDictionary<string, string>> ReadBuildInputs(
        string repoRoot,
        ProjectNode testProject,
        string targetPath,
        IReadOnlyDictionary<string, ProjectNode> owners,
        IReadOnlyList<string> assemblies,
        IReadOnlySet<string> tracked)
    {
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var assembly in assemblies.Append(targetPath).Distinct(StringComparer.Ordinal))
        {
            var owner = string.Equals(assembly, targetPath, StringComparison.Ordinal)
                ? testProject
                : owners.GetValueOrDefault(Path.GetFileName(assembly));
            if (owner is not null)
            {
                inputs[Path.GetRelativePath(repoRoot, owner.Path).Replace('\\', '/')] = GeneratedSources.ReadBuildInputs(assembly, repoRoot, tracked);
            }
        }

        return inputs;
    }

    /// <summary>
    /// A generated file's PDB path relative to the repository when it lies inside it (the compiler names
    /// it under the project's <c>obj</c> directory), so a map does not depend on where the clone was.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="name">Path of the document in the PDB.</param>
    private static string Name(string repoRoot, string name)
    {
        var relative = Path.GetRelativePath(repoRoot, name);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? name : relative.Replace('\\', '/');
    }
}
