using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Code under <c>#if</c> for other frameworks (Newtonsoft.Json's LinqBridge, compiled only without <c>HAVE_LINQ</c>):
/// when the tests run for one framework (<c>-f</c>) and every project between them and the change targets it, no build
/// that run loads compiles the change, so it selects nothing; without <c>-f</c>, or through a project built for another
/// framework, it counts as before (docs/design/framework-only-code.md).
/// </summary>
public sealed class FrameworkOnlyCodeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public FrameworkOnlyCodeTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Bridge.cs", """
            namespace Lib;
            #if !HAVE_LINQ
            public static class Bridge { public static int Max(int a, int b) => a > b ? a : b; }
            #endif
            public static class Calc { public static int Tax() => 2; }
            """);
        _repo.Write("src/Mid/Mid.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup></Project>""");
        _repo.Write("src/Mid/Mid.cs", "namespace Mid; public static class Relay { public static int Tax() => Lib.Calc.Tax(); }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", """
            namespace Lib.Tests;
            #if !HAVE_LINQ
            public class BridgeTests { [Fact] public void Maxes() => Assert.Equal(3, Lib.Bridge.Max(1, 3)); }
            #endif
            public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2, Lib.Calc.Tax()); }
            """);
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_change_compiled_only_for_other_frameworks_selects_nothing_in_a_run_for_one()
    {
        _repo.Replace("src/Lib/Bridge.cs", "a > b ? a : b", "a >= b ? a : b");

        var selection = Select(new Evaluations(Root), "net10.0");

        Assert.True(selection.Mode == TestRunMode.Skipped, $"{selection.Mode}: {selection.Reason}");
    }

    [Fact]
    public void Without_a_framework_the_change_counts_for_the_builds_that_compile_it()
    {
        _repo.Replace("src/Lib/Bridge.cs", "a > b ? a : b", "a >= b ? a : b");

        var selection = Select(new Evaluations(Root), framework: null);

        Assert.Contains("Lib.Tests.BridgeTests", selection.Mode == TestRunMode.All ? ["Lib.Tests.BridgeTests"] : selection.TestClasses);
    }

    [Fact]
    public void A_project_between_built_for_another_framework_keeps_the_change()
    {
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", "<ProjectReference Include=\"../../src/Lib/Lib.csproj\" />", "<ProjectReference Include=\"../../src/Lib/Lib.csproj\" /><ProjectReference Include=\"../../src/Mid/Mid.csproj\" />");
        _repo.Commit("through Mid");
        _repo.Replace("src/Lib/Bridge.cs", "a > b ? a : b", "a >= b ? a : b");

        var selection = Select(new Evaluations(Root) { MidFrameworks = ["netstandard2.0"], TestsReferenceMid = true }, "net10.0");

        Assert.NotEqual(TestRunMode.Skipped, selection.Mode);
    }

    [Fact]
    public void A_change_every_framework_compiles_still_selects_its_tests()
    {
        _repo.Replace("src/Lib/Bridge.cs", "Tax() => 2", "Tax() => 3");

        var selection = Select(new Evaluations(Root), "net10.0");

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.TaxTests"], selection.TestClasses);
    }

    /// <summary>The repository root as git reports it, which the evaluation's paths are keyed by.</summary>
    private string Root => new GitClient(NullLogger<GitClient>.Instance).TopLevel(_repo.Root);

    private TestProjectSelection Select(Evaluations evaluations, string? framework) =>
        new AnalysisPipeline(NullLoggerFactory.Instance, evaluations).Run(_repo.Root, "main", new AnalysisOptions { Framework = framework })
            .Tests.Single(static t => t.Project.IsTestProject);

    /// <summary>What MSBuild would report: Lib and Lib.Tests target net10.0 (with HAVE_LINQ) and net20 (without).</summary>
    /// <param name="root">Repository root as git reports it.</param>
    private sealed class Evaluations(string root) : IProjectEvaluator
    {
        /// <summary>Mid's target frameworks.</summary>
        public string[] MidFrameworks { get; init; } = ["net10.0", "net20"];

        /// <summary>True when Lib.Tests references Mid as well as Lib.</summary>
        public bool TestsReferenceMid { get; init; }

        public IReadOnlyDictionary<string, EvaluatedProject>? Evaluate(string repoRoot, IReadOnlyCollection<string> projects)
        {
            string Full(string relative) => Paths.Normalize(Path.Combine(root, relative));
            static IReadOnlyDictionary<string, IReadOnlyList<string>> Symbols(IEnumerable<string> frameworks) =>
                frameworks.ToDictionary(
                    static f => f,
                    static f => (IReadOnlyList<string>)(f == "net10.0" ? ["HAVE_LINQ", "NET10_0"] : f == "net20" ? ["NET20"] : ["NETSTANDARD2_0"]),
                    StringComparer.OrdinalIgnoreCase);
            EvaluatedProject Project(bool test, string[] references, string[] items, IEnumerable<string> frameworks)
            {
                var symbols = Symbols(frameworks);
                return new EvaluatedProject(test, null, [.. references.Select(Full)], items.Select(Full).ToHashSet(Paths.Comparer), new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                {
                    FrameworkSymbols = symbols,
                    SymbolSets = [.. symbols.Values],
                };
            }

            string[] testReferences = TestsReferenceMid ? ["src/Lib/Lib.csproj", "src/Mid/Mid.csproj"] : ["src/Lib/Lib.csproj"];
            return new Dictionary<string, EvaluatedProject>(Paths.Comparer)
            {
                [Full("src/Lib/Lib.csproj")] = Project(false, [], ["src/Lib/Bridge.cs"], ["net10.0", "net20"]),
                [Full("src/Mid/Mid.csproj")] = Project(false, ["src/Lib/Lib.csproj"], ["src/Mid/Mid.cs"], MidFrameworks),
                [Full("tests/Lib.Tests/Lib.Tests.csproj")] = Project(true, testReferences, ["tests/Lib.Tests/Tests.cs"], ["net10.0", "net20"]),
            };
        }
    }
}
