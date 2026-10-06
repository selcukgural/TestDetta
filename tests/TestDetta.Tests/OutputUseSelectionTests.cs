using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A project referenced without compiling against it (<c>ReferenceOutputAssembly="false"</c>) reaches
/// the tests only through its output: when the consumer's build does not use the output, a change to it
/// selects the classes that started or loaded it while the map was recorded, and nothing else
/// (docs/design/runtime-file-reads.md). When the build uses it, or no map says, the consumer runs as a whole.
/// </summary>
public sealed class OutputUseSelectionTests : IDisposable
{
    private const string Reference = """<ProjectReference Include="../../tools/Tool/Tool.csproj" ReferenceOutputAssembly="false" />""";

    private readonly GitRepoFixture _repo = new();

    public OutputUseSelectionTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
        _repo.Write("tools/Tool/Tool.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""");
        _repo.Write("tools/Tool/Program.cs", "namespace Tool; public static class Program { public static void Main() => System.Console.WriteLine(\"tool\"); }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                {Reference}
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", """
            namespace Lib.Tests;
            public class CalcTests { [Fact] public void Taxes() => Assert.Equal(2, new Lib.Calc().Tax()); }
            public class ToolTests { [Fact] public void Runs() => System.Diagnostics.Process.Start("dotnet", "Tool.dll")!.WaitForExit(); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_changed_tool_selects_only_the_classes_that_started_it()
    {
        WriteMap(Uses(classes: ["Tool", "dotnet"]));
        ChangeTool();

        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Lib.Tests.ToolTests"], selection.TestClasses);
        var decision = selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.ToolTests");
        Assert.Equal(ClassDecisionKind.FileRead, decision.Kind);
        Assert.Contains("started or loaded the output of Tool.csproj", decision.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_no_test_used_selects_nothing()
    {
        WriteMap(Uses(classes: ["dotnet"]));
        ChangeTool();

        Assert.Equal(TestRunMode.Skipped, Select().Mode);
    }

    [Fact]
    public void A_map_that_does_not_record_uses_runs_the_whole_project()
    {
        WriteMap(uses: null);
        ChangeTool();

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("no map says which classes do", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_use_outside_the_tests_runs_the_whole_project()
    {
        WriteMap(Uses(classes: [], ambient: ["Tool"]));
        ChangeTool();

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("started or loaded the output of Tool.csproj outside its tests", selection.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<Target Name="CopyTool" AfterTargets="Build"><Copy SourceFiles="../../tools/Tool/bin/Tool.dll" DestinationFolder="$(OutDir)" /></Target>""", "has a target (CopyTool) that names Tool")]
    [InlineData("""<Target Name="All" AfterTargets="ResolveProjectReferences"><Message Text="@(ProjectReference)" /></Target>""", "goes through the project references")]
    [InlineData("""<ItemGroup><EmbeddedResource Include="../../tools/Tool/bin/Tool.dll" /></ItemGroup>""", "compiles with Tool's output (EmbeddedResource)")]
    public void A_build_that_uses_the_output_runs_the_whole_project(string build, string reason)
    {
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", "</Project>", $"{build}\n</Project>");
        _repo.Commit("build use");
        WriteMap(Uses(classes: ["Tool"]));
        ChangeTool();

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains(reason, selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_item_type_on_the_reference_is_a_build_use()
    {
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", Reference, Reference.Replace("/>", """OutputItemType="ToolExe" />""", StringComparison.Ordinal));
        _repo.Commit("item type");
        WriteMap(Uses(classes: ["Tool"]));
        ChangeTool();

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("OutputItemType=\"ToolExe\"", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_in_a_file_the_project_does_not_import_is_not_a_build_use()
    {
        _repo.Write("build/tool.targets", """<Project><Target Name="RunTool"><Exec Command="dotnet Tool.dll" /></Target></Project>""");
        _repo.Commit("unrelated");
        WriteMap(Uses(classes: ["Tool"]));
        ChangeTool();

        Assert.Equal(TestRunMode.Filtered, Select().Mode);
    }

    [Fact]
    public void A_target_in_a_file_the_project_imports_is_a_build_use()
    {
        _repo.Write("build/tool.targets", """<Project><Target Name="RunTool"><Exec Command="dotnet Tool.dll" /></Target></Project>""");
        _repo.Write("Directory.Build.targets", """<Project><Import Project="$([MSBuild]::GetPathOfFileAbove('tool.targets', '$(MSBuildThisFileDirectory)build/'))" /></Project>""");
        _repo.Commit("imported");
        WriteMap(Uses(classes: ["Tool"]));
        ChangeTool();

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("build/tool.targets has a target (RunTool) that names Tool", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Packing_the_output_is_not_a_build_use()
    {
        // protobuf-net.Core packs BuildTools' output as an analyzer of its package.
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", "</Project>", """
            <ItemGroup><None Include="../../tools/Tool/bin/Release/Tool.dll" Pack="true" PackagePath="analyzers/dotnet/cs" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("pack");
        WriteMap(Uses(classes: ["Tool"]));
        ChangeTool();

        Assert.Equal(["Lib.Tests.ToolTests"], Select().TestClasses);
    }

    [Fact]
    public void A_library_that_references_the_tool_this_way_hands_the_question_to_its_tests()
    {
        // The library orders the tool's build; the tests below it start the tool.
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", Reference, string.Empty);
        _repo.Replace("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{Reference.Replace("../../", "../../", StringComparison.Ordinal)}</ItemGroup></Project>
            """);
        _repo.Commit("library reference");
        WriteMap(Uses(classes: ["Tool"]));
        ChangeTool();

        Assert.Equal(["Lib.Tests.ToolTests"], Select().TestClasses);
    }

    private void ChangeTool() => _repo.Replace("tools/Tool/Program.cs", "\"tool\"", "\"TOOL\"");

    private static FileReads Uses(string[] classes, string[]? ambient = null) => new(
        new Dictionary<string, IReadOnlyList<string>> { ["Lib.Tests.ToolTests"] = classes },
        new Dictionary<string, IReadOnlyList<string>>(),
        ambient ?? [],
        []);

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

    private void WriteMap(FileReads? uses)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0], ["Lib.Tests.ToolTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            FileReads = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
            OutputUses = uses,
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
