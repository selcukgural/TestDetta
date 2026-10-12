using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A class the map credits with changed code only because it read a value that code built (a field another class
/// filled, <c>builtValues</c>) is selected as before, but its evidence says so instead of claiming it ran the code
/// (Spectre.Console: 9 of 11 classes "ran Calendar.GetWeekdays()" while only CalendarTests and TreeTests did).
/// </summary>
public sealed class BuiltValueEvidenceTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public BuiltValueEvidenceTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
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
            public class CalcTests { [Fact] public void Taxes() => Assert.Equal(2, new Lib.Calc().Tax()); }
            public class ReaderTests { [Fact] public void Reads() => Assert.NotNull(Shared.Value); }
            public class OtherTests { [Fact] public void Runs() => Assert.True(true); }
            public static class Shared { public static object Value = new(); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_class_that_only_read_a_value_the_changed_code_built_says_it_read_it()
    {
        WriteMap();
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 3; }");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single();

        // The selection itself is what it was: both classes the map credits, nothing else.
        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Lib.Tests.CalcTests", "Lib.Tests.ReaderTests"], selection.TestClasses.Order(StringComparer.Ordinal));
        var ran = selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.CalcTests");
        var read = selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.ReaderTests");
        Assert.Equal(ClassDecisionKind.Coverage, ran.Kind);
        Assert.Equal(ClassDecisionKind.Coverage, read.Kind);
        Assert.StartsWith("ran Lib.Calc.Tax(), which changed", ran.Evidence, StringComparison.Ordinal);
        Assert.StartsWith("read a value built by Lib.Calc.Tax(), which changed", read.Evidence, StringComparison.Ordinal);
    }

    private void WriteMap()
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0], ["Lib.Tests.ReaderTests"] = [], ["Lib.Tests.OtherTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            // CalcTests ran Tax while building the value it stored; ReaderTests only read it.
            BuiltValues = new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0], ["Lib.Tests.ReaderTests"] = [0] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
