using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Two ways a test sees a change without running the changed member (found 2026-10-06 on AspNetCoreOData,
/// whose PublicApiTest loads the library by its path and dumps every public declaration and static field
/// value through reflection): an initializer belongs to the constructor it compiles into, and a test class
/// that loads a changed project's output from its path is selected for any change to that project.
/// </summary>
public sealed class LoadedOutputTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public LoadedOutputTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
        _repo.Write("src/Lib/Keys.cs", """namespace Lib; public static class Keys { public static readonly string ModelState = "ModelState"; }""");
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
            public class OtherTests { [Fact] public void Nothing() { } }
            public class PublicApiTests { [Fact] public void Matches() => System.Reflection.Assembly.LoadFrom("Lib.dll").GetExportedTypes(); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_class_that_loads_a_changed_project_by_its_path_is_selected()
    {
        WriteMap(Uses(new() { ["Lib.Tests.PublicApiTests"] = ["Lib"] }));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select();

        Assert.Equal(["Lib.Tests.CalcTests", "Lib.Tests.PublicApiTests"], selection.TestClasses);
        var decision = selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.PublicApiTests");
        Assert.Equal(ClassDecisionKind.FileRead, decision.Kind);
        Assert.Contains("loaded the output of Lib.csproj", decision.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_another_output_selects_nothing_more()
    {
        WriteMap(Uses(new() { ["Lib.Tests.PublicApiTests"] = ["Newtonsoft.Json"] }));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        Assert.Equal(["Lib.Tests.CalcTests"], Select().TestClasses);
    }

    [Fact]
    public void Loading_the_output_outside_any_test_runs_the_whole_project()
    {
        WriteMap(Uses([], ambient: ["Lib"]));
        _repo.Replace("src/Lib/Calc.cs", "=> 2", "=> 3");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("loaded the output of Lib.csproj outside its tests", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_static_initializer_change_selects_the_classes_that_ran_the_type()
    {
        // PublicApiTests read the field through reflection: no member of Keys ran but its static constructor.
        WriteMap(Uses([]), keysConstructorRanIn: "Lib.Tests.PublicApiTests");
        _repo.Replace("src/Lib/Keys.cs", "= \"ModelState\"", "= \"ModelStateX\"");

        var selection = Select();

        Assert.Contains("Lib.Tests.PublicApiTests", selection.TestClasses);
        Assert.DoesNotContain("Lib.Tests.OtherTests", selection.TestClasses);
    }

    [Theory]
    [InlineData("public static readonly string Key = \"a\";", "public static readonly string Key = \"b\";", ".cctor")]
    [InlineData("public string Key = \"a\";", "public string Key = \"b\";", ".ctor")]
    [InlineData("public static string Key { get; } = \"a\";", "public static string Key { get; } = \"b\";", ".cctor")]
    [InlineData("", "public static readonly int Added = 1;", ".cctor")]
    [InlineData("public const string Key = \"a\";", "public const string Key = \"b\";", null)]
    [InlineData("public static string Key;", "public static int Key;", null)]
    public void An_initializer_change_is_a_change_of_its_constructor(string before, string after, string? constructor)
    {
        var change = new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance)
            .Detect("Keys.cs", SourceText.From($"namespace Lib; public class Keys {{ {before} }}"), SourceText.From($"namespace Lib; public class Keys {{ {after} }}"));

        var constructors = change.Symbols.Where(static s => s.MemberName is ".cctor" or ".ctor").Select(static s => s.MemberName).ToList();
        Assert.Equal(constructor is null ? [] : [constructor], constructors);
    }

    [Theory]
    [InlineData("Public Class Keys\n  Public Shared ReadOnly Key As String = \"a\"\nEnd Class", ".cctor")]
    [InlineData("Public Module Keys\n  Public ReadOnly Key As String = \"a\"\nEnd Module", ".cctor")]
    [InlineData("Public Class Keys\n  Public Key As String = \"a\"\nEnd Class", ".ctor")]
    [InlineData("Public Class Keys\n  Public Const Key As String = \"a\"\nEnd Class", null)]
    public void A_visual_basic_initializer_change_is_a_change_of_its_constructor(string before, string? constructor)
    {
        var change = new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance)
            .Detect("Keys.vb", SourceText.From(before), SourceText.From(before.Replace("\"a\"", "\"b\"", StringComparison.Ordinal)));

        var constructors = change.Symbols.Where(static s => s.MemberName is ".cctor" or ".ctor").Select(static s => s.MemberName).ToList();
        Assert.Equal(constructor is null ? [] : [constructor], constructors);
    }

    private static FileReads Uses(Dictionary<string, IReadOnlyList<string>> classes, string[]? ambient = null) =>
        new(classes, new Dictionary<string, IReadOnlyList<string>>(), ambient ?? [], []);

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

    private void WriteMap(FileReads uses, string? keysConstructorRanIn = null)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var classes = new Dictionary<string, IReadOnlyList<int>>
        {
            ["Lib.Tests.CalcTests"] = [0],
            ["Lib.Tests.OtherTests"] = [],
            ["Lib.Tests.PublicApiTests"] = keysConstructorRanIn == "Lib.Tests.PublicApiTests" ? [1] : [],
        };
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax"), new CoveredMethod("Lib.Keys", ".cctor")],
            new CoverageHits(classes, new Dictionary<string, IReadOnlyList<int>>(), []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            FileReads = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
            OutputUses = uses,
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
