using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A project that compiles another's files a second time and that no test project references (Markdig.Signed beside
/// Markdig): its copy of a change has no test to answer for. Left unanswered, its copy kept spreading by name to an
/// abstract member, whose implementations are looked up in every project: a test helper overriding the other copy's
/// member was among them, and the classes that ran the helper were selected though the map says they never ran the change
/// (Markdig: 154 of 177 classes through <c>ParentContainerReplacementInlineParser.Match</c>; docs/design/twin-owners.md).
/// </summary>
public sealed class TwinOwnerTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public TwinOwnerTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", """
            namespace Lib;
            public abstract class Levy { public abstract int Tax(); }
            public class Calc : Levy { public override int Tax() => 2; }
            public abstract class Rule { public abstract int Apply(Calc calc); }
            """);
        _repo.Write("src/Lib.Signed/Lib.Signed.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Lib/**/*.cs" /></ItemGroup>
            </Project>
            """);
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
            public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2, new Lib.Calc().Tax()); }
            public class MyRule : Lib.Rule { public override int Apply(Lib.Calc calc) => 1; }
            public class RuleTests { [Fact] public void Applies() => Assert.Equal(1, new MyRule().Apply(null!)); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_copy_no_test_project_references_does_not_spread_to_the_tests()
    {
        WriteMap();
        _repo.Replace("src/Lib/Calc.cs", "Tax() => 2", "Tax() => 3");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.TaxTests"], selection.TestClasses);
    }

    /// <summary>A map in which TaxTests ran Calc.Tax and RuleTests the test helper MyRule.Apply, not the change.</summary>
    private void WriteMap()
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax"), new CoveredMethod("Lib.Tests.MyRule", "Apply")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.TaxTests"] = [0], ["Lib.Tests.RuleTests"] = [1] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
