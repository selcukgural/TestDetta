using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// With MSBuild's evaluation the graph stops guessing: references built by property functions,
/// files split between projects of one directory with <c>Compile Remove</c>, linked files and
/// framework-conditioned items come out as MSBuild sees them. A project MSBuild cannot evaluate keeps
/// the XML reading.
/// </summary>
public sealed class ProjectEvaluationTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    private string Full(string relative) => Paths.Normalize(Path.Combine(_repo.Root, relative));

    [Fact]
    public void Evaluated_items_decide_which_project_of_a_shared_directory_owns_a_file()
    {
        _repo.Write("src/Lib/A.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/B.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/OnlyA.cs", "class OnlyA { }");
        _repo.Write("src/Lib/Notes.txt", "read by a target, no item");
        var evaluator = new FakeEvaluator
        {
            [Full("src/Lib/A.csproj")] = Evaluated(items: [Full("src/Lib/OnlyA.cs")]),
            [Full("src/Lib/B.csproj")] = Evaluated(),
        };

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, evaluator).Load(_repo.Root);

        Assert.Equal(["A.csproj"], Owners(graph, "src/Lib/OnlyA.cs"));
        // No evaluated project includes the file: every project of the directory may read it.
        Assert.Equal(["A.csproj", "B.csproj"], Owners(graph, "src/Lib/Notes.txt"));
    }

    [Fact]
    public void A_deleted_file_a_glob_brings_in_from_outside_the_folder_still_belongs_to_the_project()
    {
        _repo.Write("tests/Data.Tests/Data.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><None Include="..\..\testdata\**" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
            </Project>
            """);
        _repo.Write("testdata/kept.json", "{}");
        // MSBuild expands the glob on the files that exist, so a deleted file is in no evaluated item.
        var evaluator = new FakeEvaluator
        {
            [Full("tests/Data.Tests/Data.Tests.csproj")] = Evaluated(test: true, items: [Full("testdata/kept.json")]),
        };

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, evaluator).Load(_repo.Root);

        Assert.Equal(["Data.Tests.csproj"], Owners(graph, "testdata/kept.json"));
        Assert.Equal(["Data.Tests.csproj"], Owners(graph, "testdata/deleted.json"));
        Assert.Empty(Owners(graph, "docs/deleted.md"));
    }

    [Fact]
    public void Deleting_shared_test_data_runs_the_test_project_that_ships_it()
    {
        _repo.Write("tests/Data.Tests/Data.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <None Include="..\..\testdata\**" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Data.Tests/ReaderTests.cs", "namespace Data.Tests; public class ReaderTests { [Fact] public void Reads() => Assert.NotEmpty(System.IO.File.ReadAllText(\"deleted.json\")); }");
        _repo.Write("testdata/kept.json", "{}");
        _repo.Write("testdata/deleted.json", "{}");
        _repo.Commit("base");
        File.Delete(Full("testdata/deleted.json"));
        // The pipeline works from the root as git reports it (on macOS /private/var, not /var), so the
        // evaluations must be keyed the same way or the project would silently fall back to the XML reading.
        var root = new GitClient(NullLogger<GitClient>.Instance).TopLevel(_repo.Root);
        string Top(string relative) => Paths.Normalize(Path.Combine(root, relative));
        var evaluator = new FakeEvaluator
        {
            [Top("tests/Data.Tests/Data.Tests.csproj")] = Evaluated(test: true, items: [Top("tests/Data.Tests/ReaderTests.cs"), Top("testdata/kept.json")]),
        };

        var tests = new AnalysisPipeline(NullLoggerFactory.Instance, evaluator).Run(_repo.Root, "main").Tests;

        var data = tests.Single(t => Path.GetFileNameWithoutExtension(t.Project.Path) == "Data.Tests");
        Assert.Equal(TestRunMode.All, data.Mode);
        Assert.Contains("testdata/deleted.json", data.Reason, StringComparison.Ordinal);
        Assert.NotNull(data.Project.EvaluatedItems);
    }

    [Fact]
    public void Evaluated_references_replace_guessed_patterns_and_the_test_flag_is_united()
    {
        _repo.Write("src/Lib.v1/Lib.v1.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib.v2/Lib.v2.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("tests/Checks/Checks.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><V>$([System.String]::Concat('v', '2'))</V></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/Lib.$(V)/Lib.$(V).csproj" /></ItemGroup>
            </Project>
            """);
        var evaluator = new FakeEvaluator
        {
            [Full("tests/Checks/Checks.csproj")] = Evaluated(test: true, references: [Full("src/Lib.v2/Lib.v2.csproj")]),
            [Full("src/Lib.v1/Lib.v1.csproj")] = Evaluated(),
            [Full("src/Lib.v2/Lib.v2.csproj")] = Evaluated(),
        };

        var xml = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, evaluator).Load(_repo.Root);

        // The XML reading depends on both library versions; MSBuild knows it is v2.
        Assert.Contains(Full("tests/Checks/Checks.csproj"), xml.DependentsOf(Full("src/Lib.v1/Lib.v1.csproj")));
        Assert.Empty(graph.DependentsOf(Full("src/Lib.v1/Lib.v1.csproj")));
        Assert.Equal([Full("tests/Checks/Checks.csproj")], graph.DependentsOf(Full("src/Lib.v2/Lib.v2.csproj")));
        Assert.True(graph[Full("tests/Checks/Checks.csproj")].IsTestProject);
    }

    [Fact]
    public void A_project_msbuild_could_not_evaluate_keeps_the_xml_reading()
    {
        _repo.Write("src/Lib/A.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/B.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Shared.cs", "class Shared { }");
        var partial = new FakeEvaluator { [Full("src/Lib/A.csproj")] = Evaluated() };

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, partial).Load(_repo.Root);
        var none = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, new FakeEvaluator { Fails = true }).Load(_repo.Root);

        // A was evaluated without the file, but B was not evaluated: B keeps owning it by directory,
        // and A too, since no evaluated project includes the file.
        Assert.Equal(["A.csproj", "B.csproj"], Owners(graph, "src/Lib/Shared.cs"));
        Assert.Null(graph[Full("src/Lib/B.csproj")].EvaluatedItems);
        Assert.All(none.Projects, static p => Assert.Null(p.EvaluatedItems));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Msbuild_evaluates_removed_linked_computed_and_framework_conditioned_items()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        // Two projects split one directory, Meziantou style.
        _repo.Write("src/Lib/Lib.Old.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Remove="New*.cs" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Lib/Lib.New.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Remove="Old*.cs" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Lib/OldRule.cs", "class OldRule { }");
        _repo.Write("src/Lib/NewRule.cs", "class NewRule { }");
        // A linked file under a computed root, and an item only one target framework compiles.
        _repo.Write("shared/Linked.cs", "class Linked { }");
        _repo.Write("src/Multi/Multi.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                <Root>$([System.IO.Path]::GetFullPath('$(MSBuildThisFileDirectory)../..'))</Root>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(Root)/shared/Linked.cs" />
                <Compile Include="Net8Only.cs" Condition="'$(TargetFramework)' == 'net8.0'" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("src/Multi/Net8Only.cs", "class Net8Only { }");
        // A test project whose reference and test flag only evaluation knows, and a broken project.
        _repo.Write("tests/Checks/Checks.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Kind>$([System.String]::Concat('N', 'ew'))</Kind>
                <IsTestProject Condition="'$(Kind)' == 'New'">true</IsTestProject>
              </PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/Lib/Lib.$(Kind).csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Broken/Broken.csproj", """<Project Sdk="Microsoft.NET.Sdk"><Import Project="missing.props" /></Project>""");

        var evaluator = new MsBuildProjectEvaluator(NullLoggerFactory.Instance, diskCache: false);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, evaluator).Load(_repo.Root);

        Assert.Equal(["Lib.Old.csproj"], Owners(graph, "src/Lib/OldRule.cs"));
        Assert.Equal(["Lib.New.csproj"], Owners(graph, "src/Lib/NewRule.cs"));
        Assert.Contains("Multi.csproj", Owners(graph, "shared/Linked.cs"));
        Assert.Equal(["Multi.csproj"], Owners(graph, "src/Multi/Net8Only.cs"));
        var checks = graph[Full("tests/Checks/Checks.csproj")];
        Assert.True(checks.IsTestProject);
        Assert.Equal([Full("src/Lib/Lib.New.csproj")], checks.References);
        Assert.Null(graph[Full("src/Broken/Broken.csproj")].EvaluatedItems);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Evaluation_is_cached_until_a_file_is_added_or_a_project_changes()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("src/Lib/A.cs", "class A { }");
        var cache = Path.Combine(_repo.Root, ".testdetta", "cache", "graph");
        var lib = Full("src/Lib/Lib.csproj");

        new MsBuildProjectEvaluator(NullLoggerFactory.Instance).Evaluate(Paths.Normalize(_repo.Root), [lib]);
        _repo.Write("src/Lib/A.cs", "class A { int X; }");
        new MsBuildProjectEvaluator(NullLoggerFactory.Instance).Evaluate(Paths.Normalize(_repo.Root), [lib]);
        Assert.Single(Directory.GetFiles(cache));

        _repo.Write("src/Lib/B.cs", "class B { }");
        var added = new MsBuildProjectEvaluator(NullLoggerFactory.Instance).Evaluate(Paths.Normalize(_repo.Root), [lib]);
        Assert.Equal(2, Directory.GetFiles(cache).Length);
        Assert.Contains(Full("src/Lib/B.cs"), added![lib].Items);

        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Remove="B.cs" /></ItemGroup></Project>""");
        var removed = new MsBuildProjectEvaluator(NullLoggerFactory.Instance).Evaluate(Paths.Normalize(_repo.Root), [lib]);
        Assert.Equal(3, Directory.GetFiles(cache).Length);
        Assert.DoesNotContain(Full("src/Lib/B.cs"), removed![lib].Items.Where(static i => i.EndsWith(".cs", StringComparison.Ordinal)));
    }

    [Fact]
    public void References_not_compiled_against_are_reported_as_opaque()
    {
        var evaluated = MsBuildProjectEvaluator.Parse(
        [
            "Project\t/r/T.csproj\t/r/T.csproj\ttrue\tT\t",
            "Reference\t/r/T.csproj\t/r/Lib.csproj\t\t\t",
            "Reference\t/r/T.csproj\t/r/Gen.csproj\t\t\t",
            "OpaqueReference\t/r/T.csproj\t/r/Gen.csproj\t\t\t",
        ]);

        Assert.Equal(["/r/Gen.csproj", "/r/Lib.csproj"], evaluated["/r/T.csproj"].References);
        Assert.Equal(["/r/Gen.csproj"], evaluated["/r/T.csproj"].OpaqueReferences);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Msbuild_finds_opaque_references_whatever_sets_their_metadata()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("src/Gen/Gen.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("src/Tool/Tool.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        // Metadata from an Update item and from an item definition: only evaluation sees these.
        _repo.Write("tests/T/T.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemDefinitionGroup><ProjectReference><Private>false</Private></ProjectReference></ItemDefinitionGroup>
              <ItemGroup>
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                <ProjectReference Include="../../src/Gen/Gen.csproj" />
                <ProjectReference Update="../../src/Gen/Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                <ProjectReference Include="../../src/Tool/Tool.csproj" Condition="'$(TargetFramework)' == 'net10.0'" ReferenceOutputAssembly="$([System.String]::Concat('fal', 'se'))" />
              </ItemGroup>
            </Project>
            """);

        var evaluator = new MsBuildProjectEvaluator(NullLoggerFactory.Instance);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, evaluator).Load(_repo.Root);

        var tests = graph[Full("tests/T/T.csproj")];
        Assert.Equal([Full("src/Gen/Gen.csproj"), Full("src/Lib/Lib.csproj"), Full("src/Tool/Tool.csproj")], tests.References.Order(StringComparer.Ordinal));
        Assert.Equal([Full("src/Gen/Gen.csproj"), Full("src/Tool/Tool.csproj")], tests.OpaqueReferences.Order(StringComparer.Ordinal));

        // A copy elsewhere reads the evaluation from the disk cache, its paths relative to the repository.
        var copy = Directory.CreateTempSubdirectory("testdetta-opaque-copy-").FullName;
        try
        {
            CopyDirectory(_repo.Root, copy);
            var cacheFiles = Directory.GetFiles(Path.Combine(copy, ".testdetta", "cache", "graph"));
            var fromCache = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, evaluator).Load(copy);
            string InCopy(string relative) => Paths.Normalize(Path.Combine(copy, relative));
            Assert.Equal([InCopy("src/Gen/Gen.csproj"), InCopy("src/Tool/Tool.csproj")], fromCache[InCopy("tests/T/T.csproj")].OpaqueReferences.Order(StringComparer.Ordinal));
            Assert.Equal(cacheFiles, Directory.GetFiles(Path.Combine(copy, ".testdetta", "cache", "graph")));
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }

    [Fact]
    public void Each_target_framework_reports_its_preprocessor_symbols_once()
    {
        var evaluated = MsBuildProjectEvaluator.Parse(
        [
            "Project\t/r/Lib.csproj\t/r/Lib.csproj\tfalse\tLib\t",
            "Framework\t/r/Lib.csproj\t/r/Lib.csproj\t\t\tnet8.0",
            "Define\t/r/Lib.csproj\tNET8_0_OR_GREATER\t\t\tnet8.0",
            "Define\t/r/Lib.csproj\tNET\t\t\tnet8.0",
            "Framework\t/r/Lib.csproj\t/r/Lib.csproj\t\t\tnetstandard2.0",
            "Define\t/r/Lib.csproj\tNETSTANDARD2_0\t\t\tnetstandard2.0",
            "Framework\t/r/Lib.csproj\t/r/Lib.csproj\t\t\tnet9.0",
            "Define\t/r/Lib.csproj\tNET\t\t\tnet9.0",
            "Define\t/r/Lib.csproj\tNET8_0_OR_GREATER\t\t\tnet9.0",
        ]);

        // net9.0 compiles with the same symbols as net8.0 here: one set.
        Assert.Equal(["NET;NET8_0_OR_GREATER", "NETSTANDARD2_0"], evaluated["/r/Lib.csproj"].SymbolSets.Select(static s => string.Join(';', s)));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void A_test_class_under_a_mix_of_symbols_is_traced_with_the_frameworks_real_symbols()
    {
        // Newtonsoft.Json's DateOnlyTests: #if NET6_0_OR_GREATER && !NETSTANDARD2_0 is entered neither
        // with no symbols nor with every named one, only with what net10.0 really defines.
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public static class Calc { public static int Tax() => 2; }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit.v3" /><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/ModernTests.cs", """
            #if NET6_0_OR_GREATER && !NETSTANDARD2_0
            namespace Lib.Tests;
            public class ModernTests { [Xunit.Fact] public void Taxes() => Xunit.Assert.Equal(2, Lib.Calc.Tax()); }
            #endif
            """);
        _repo.Write("tests/Lib.Tests/OtherTests.cs", "namespace Lib.Tests; public class OtherTests { [Xunit.Fact] public void Two() => Xunit.Assert.Equal(2, 1 + 1); }");
        _repo.Commit("base");
        _repo.Replace("src/Lib/Calc.cs", "=> 2;", "=> 1 + 1;");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance, new MsBuildProjectEvaluator(NullLoggerFactory.Instance, diskCache: false))
            .Run(_repo.Root, "main").Tests.Single();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Lib.Tests.ModernTests"], selection.TestClasses);
    }

    [Fact]
    public void Driver_escapes_msbuild_characters_in_paths() =>
        Assert.Contains("a%25b%40c%24d", MsBuildProjectEvaluator.Driver(["/repo/a%b@c$d/P.csproj"], "/t/x.targets", "/t/out.txt"), StringComparison.Ordinal);

    private List<string> Owners(ProjectGraph graph, string file) =>
        [.. graph.ResolveFile(Full(file)).Where(static o => o.Role == FileRole.Source).Select(static o => Path.GetFileName(o.Project.Path)).Order(StringComparer.Ordinal)];

    private static EvaluatedProject Evaluated(bool test = false, IReadOnlyList<string>? references = null, IReadOnlyList<string>? items = null) =>
        new(test, null, references ?? [], new HashSet<string>(items ?? [], Paths.Comparer), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public void Dispose() => _repo.Dispose();

    /// <summary>Answers with fixed evaluations; a project without one was not evaluated.</summary>
    private sealed class FakeEvaluator : Dictionary<string, EvaluatedProject>, IProjectEvaluator
    {
        public FakeEvaluator()
            : base(Paths.Comparer)
        {
        }

        public bool Fails { get; init; }

        public IReadOnlyDictionary<string, EvaluatedProject>? Evaluate(string repoRoot, IReadOnlyCollection<string> projects) => Fails ? null : this;
    }
}
