using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A changed file that no project owns selects the test classes that read it while the map was
/// recorded, and nothing else (docs/design/runtime-file-reads.md).
/// </summary>
public sealed class FileReadSelectionTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public FileReadSelectionTests()
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
            public class ReaderTests { [Fact] public void Reads() => Assert.NotEmpty(System.IO.File.ReadAllText("../../testdata/rates.txt")); }
            public class ListerTests { [Fact] public void Lists() => Assert.NotEmpty(System.IO.Directory.GetFiles("../../testdata/more")); }
            """);
        _repo.Write("testdata/rates.txt", "42");
        _repo.Write("testdata/more/a.txt", "a");
        _repo.Write("README.md", "readme");
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_changed_data_file_selects_only_the_classes_that_read_it()
    {
        WriteMap(Reads());
        _repo.Write("testdata/rates.txt", "43");

        var selection = Select().Single();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Lib.Tests.ReaderTests"], selection.TestClasses);
        var decision = selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.ReaderTests");
        Assert.Equal(ClassDecisionKind.FileRead, decision.Kind);
        Assert.Contains("read testdata/rates.txt", decision.Evidence, StringComparison.Ordinal);
        Assert.False(selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.CalcTests").Selected);
    }

    [Fact]
    public void A_new_file_in_a_listed_directory_selects_the_class_that_listed_it()
    {
        WriteMap(Reads());
        _repo.Write("testdata/more/b.txt", "b");

        Assert.Equal(["Lib.Tests.ListerTests"], Select().Single().TestClasses);
    }

    [Fact]
    public void A_file_nobody_read_selects_nothing()
    {
        WriteMap(Reads());
        _repo.Write("README.md", "changed");

        Assert.Empty(Select());
    }

    [Fact]
    public void A_read_outside_the_tests_runs_the_whole_project()
    {
        WriteMap(Reads() with { Ambient = ["testdata/rates.txt"] });
        _repo.Write("testdata/rates.txt", "43");

        var selection = Select().Single();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("outside its tests", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_recorded_before_reads_were_knows_none_and_selects_nothing()
    {
        WriteMap(reads: null);
        _repo.Write("testdata/rates.txt", "43");

        Assert.Empty(Select());
    }

    [Fact]
    public void Readers_are_added_to_what_a_code_change_selects()
    {
        WriteMap(Reads());
        _repo.Write("testdata/rates.txt", "43");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 3; }");

        Assert.Equal(["Lib.Tests.CalcTests", "Lib.Tests.ReaderTests"], Select().Single().TestClasses);
    }

    [Fact]
    public void The_result_tells_which_files_outside_every_project_some_test_read()
    {
        WriteMap(Reads());
        _repo.Write("testdata/rates.txt", "43");
        _repo.Write("README.md", "changed");

        var result = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions());

        Assert.Equal(["README.md", "testdata/rates.txt"], result.Impact.UnmappedFiles.Order(StringComparer.Ordinal));
        Assert.Equal(["testdata/rates.txt"], result.UnmappedFilesRead);
    }

    private static FileReads Reads() => new(
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["Lib.Tests.ReaderTests"] = ["testdata/rates.txt"],
            ["Lib.Tests.ListerTests"] = ["testdata/more/"],
        },
        new Dictionary<string, IReadOnlyList<string>>(),
        [],
        []);

    private IReadOnlyList<TestProjectSelection> Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests;

    private void WriteMap(FileReads? reads)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0], ["Lib.Tests.ReaderTests"] = [], ["Lib.Tests.ListerTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            FileReads = reads,
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
