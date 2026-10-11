using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A type nothing names that a framework may create through reflection, in a project the test project references
/// without compiling against it (NLog's <c>ManuallyLoadedExtension</c>): it reaches a test process only when that
/// project's output is loaded, so the classes whose map recorded loading it are the ones that can use it, not the whole
/// test project (docs/design/loaded-outputs.md). So does a project the test project compiles against when no code outside
/// it names its types (NLog's auto-loaded <c>NLogAutoLoadExtension</c>). One whose types are named, or a map that cannot
/// say who loaded it, still runs completely.
/// </summary>
public sealed class ReflectionLoadedOutputTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public ReflectionLoadedOutputTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Target.cs", "namespace Lib; public abstract class Target { public abstract string Name(); public string Describe() => Name(); }");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
        _repo.Write("tests/Ext/Ext.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup></Project>""");
        _repo.Write("tests/Ext/ExtTarget.cs", "namespace Ext; public class ExtTarget : Lib.Target { public override string Name() => \"ext\"; }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                <ProjectReference Include="../Ext/Ext.csproj" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", """
            namespace Lib.Tests;
            public class CalcTests { [Fact] public void Taxes() => Assert.Equal(2, new Lib.Calc().Tax()); }
            public class ExtensionTests { [Fact] public void Loads() => Assert.NotNull(System.Reflection.Assembly.LoadFrom("Ext.dll")); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_reflection_found_type_in_a_loaded_output_selects_the_classes_that_load_it()
    {
        WriteMap(loadsTracked: true);
        _repo.Replace("tests/Ext/ExtTarget.cs", "\"ext\"", "\"ext2\"");

        var selection = Select();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.ExtensionTests"], selection.TestClasses);
        Assert.Contains("loaded the output of Ext.csproj", selection.Decisions.Single(static d => d.TestClass == "Lib.Tests.ExtensionTests").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reflection_found_type_reached_by_name_selects_the_classes_that_load_its_output()
    {
        // NLog: a change the maps cannot answer (Describe also ran outside any test) spreads by name to the base type,
        // whose derived declaration in the extension is reached through its base list.
        WriteMap(loadsTracked: true, describeOutsideTests: true);
        _repo.Replace("src/Lib/Target.cs", "Describe() => Name()", "Describe() => Name() + \"!\"");

        var selection = Select();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Contains("Lib.Tests.ExtensionTests", selection.TestClasses);
        Assert.DoesNotContain("Lib.Tests.CalcTests", selection.TestClasses);
    }

    [Fact]
    public void A_map_that_did_not_track_loads_runs_the_whole_project()
    {
        WriteMap(loadsTracked: false);
        _repo.Replace("tests/Ext/ExtTarget.cs", "\"ext\"", "\"ext2\"");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
    }

    [Fact]
    public void A_compile_reference_whose_types_nothing_names_selects_the_classes_that_load_it()
    {
        CompileAgainstTheExtension();
        WriteMap(loadsTracked: true);
        _repo.Replace("tests/Ext/ExtTarget.cs", "\"ext\"", "\"ext2\"");

        var selection = Select();

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.ExtensionTests"], selection.TestClasses);
    }

    [Fact]
    public void A_compile_reference_the_runtime_loaded_unseen_runs_the_whole_project()
    {
        CompileAgainstTheExtension();
        WriteMap(loadsTracked: true, unseen: ["Ext"]);
        _repo.Replace("tests/Ext/ExtTarget.cs", "\"ext\"", "\"ext2\"");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("loaded it without a call the recording saw", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_compile_reference_whose_types_are_named_runs_the_whole_project()
    {
        CompileAgainstTheExtension();
        _repo.Write("tests/Ext/Helper.cs", "namespace Ext; public static class Helper { public static int One() => 1; }");
        _repo.Write("tests/Lib.Tests/HelperTests.cs", "namespace Lib.Tests; public class HelperTests { [Fact] public void One() => Assert.Equal(1, Ext.Helper.One()); }");
        _repo.Commit("name a type of the extension");
        WriteMap(loadsTracked: true);
        _repo.Replace("tests/Ext/ExtTarget.cs", "\"ext\"", "\"ext2\"");

        var selection = Select();

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("may be created through reflection", selection.Reason, StringComparison.Ordinal);
    }

    private void CompileAgainstTheExtension()
    {
        _repo.Replace("tests/Lib.Tests/Lib.Tests.csproj", "<ProjectReference Include=\"../Ext/Ext.csproj\" ReferenceOutputAssembly=\"false\" />", "<ProjectReference Include=\"../Ext/Ext.csproj\" />");
        _repo.Commit("compile against the extension");
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

    /// <summary>A map in which ExtensionTests loaded Ext's output and CalcTests ran Calc.Tax; Target.Describe ran in no class, or outside the tests.</summary>
    private void WriteMap(bool loadsTracked, bool describeOutsideTests = false, string[]? unseen = null)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        var map = new CoverageMap(
            new CoverageMapHeader("tests/Lib.Tests/Lib.Tests.csproj", head, DateTimeOffset.UtcNow),
            [new CoveredMethod("Lib.Calc", "Tax"), new CoveredMethod("Lib.Target", "Describe")],
            new CoverageHits(
                new Dictionary<string, IReadOnlyList<int>> { ["Lib.Tests.CalcTests"] = [0], ["Lib.Tests.ExtensionTests"] = [] },
                new Dictionary<string, IReadOnlyList<int>>(),
                describeOutsideTests ? [1] : []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            FileReads = new FileReads(new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), [], []),
            OutputUses = new FileReads(
                new Dictionary<string, IReadOnlyList<string>> { ["Lib.Tests.ExtensionTests"] = ["Ext"] },
                new Dictionary<string, IReadOnlyList<string>>(),
                [],
                []),
            OutputLoadsUnseen = loadsTracked ? ["Lib", "Lib.Tests", .. unseen ?? []] : null,
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj")), map);
    }
}
