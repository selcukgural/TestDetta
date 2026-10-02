using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

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
        var cache = Path.Combine(_repo.Root, ".sift", "cache", "graph");
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
