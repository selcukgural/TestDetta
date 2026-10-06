using TestDetta.Core;

namespace TestDetta.Mutants;

/// <summary>Reads TestDetta's decisions the way a mutant is judged against them, in the evaluator and in <c>td audit</c>.</summary>
internal static class Selections
{
    /// <summary>True when the decision for a test project runs <paramref name="testClass"/>: all of the project, or a filter naming it.</summary>
    /// <param name="selection">The project's decision; null when the change does not affect the project.</param>
    /// <param name="testClass">Runtime test class name.</param>
    public static bool Runs(TestProjectSelection? selection, string testClass) => selection?.Mode switch
    {
        TestRunMode.All => true,
        TestRunMode.Filtered => selection.TestClasses.Contains(testClass),
        _ => false,
    };

    /// <summary>
    /// Share of the recorded test classes the decisions run, from 0 to 1: a project run completely counts all its
    /// map's classes, a filtered one those of its classes the map knows.
    /// </summary>
    /// <param name="tests">Decisions per affected test project.</param>
    /// <param name="maps">Maps by normalized test project path.</param>
    public static double SelectedShare(IReadOnlyList<TestProjectSelection> tests, IReadOnlyDictionary<string, CoverageMap> maps)
    {
        var total = 0;
        var selected = 0;
        foreach (var (project, map) in maps)
        {
            total += map.Classes.Count;
            var decision = tests.FirstOrDefault(t => string.Equals(t.Project.Path, project, StringComparison.Ordinal));
            selected += decision?.Mode switch
            {
                TestRunMode.All => map.Classes.Count,
                TestRunMode.Filtered => decision.TestClasses.Count(map.Classes.ContainsKey),
                _ => 0,
            };
        }

        return total == 0 ? 0 : (double)selected / total;
    }

    /// <summary>Every member that ran in any test class, fixture or start-up of any map.</summary>
    /// <param name="maps">Coverage maps.</param>
    public static HashSet<CoveredMethod> CoveredMembers(IEnumerable<CoverageMap> maps)
    {
        var covered = new HashSet<CoveredMethod>();
        foreach (var map in maps)
        {
            foreach (var ids in map.Classes.Values.Concat(map.Fixtures.Values).Append(map.Ambient))
            {
                foreach (var id in ids)
                {
                    covered.Add(map.Methods[id]);
                }
            }
        }

        return covered;
    }
}
