using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>Lists the test classes of a test project from its source, without building or running it.</summary>
/// <param name="logger">Receives indexing details.</param>
public sealed class TestClassFinder(ILogger<TestClassFinder> logger)
{
    /// <summary>
    /// Runtime names (nested classes with '+') of the classes in <paramref name="testProject"/> that
    /// declare or inherit test methods, sorted.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="testProject">Test project to list.</param>
    public IReadOnlyList<string> Find(ProjectGraph graph, string repoRoot, ProjectNode testProject)
    {
        var index = new SourceIndexBuilder(logger).Build(graph, repoRoot, new HashSet<string>([testProject.Path], Paths.Comparer));
        var classes = new List<string>();
        foreach (var type in index.Types)
        {
            if (type.CanContainTests)
            {
                classes.Add(type.RuntimeTypeName);
            }
        }

        classes.Sort(StringComparer.Ordinal);
        return classes;
    }
}
