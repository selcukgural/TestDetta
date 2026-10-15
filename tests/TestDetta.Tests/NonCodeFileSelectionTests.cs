using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A changed non-code file inside a project folder runs the test classes that read it, when the build
/// does not use it and the maps saw every read; otherwise its project, as under the project rule
/// (docs/design/non-code-files.md).
/// </summary>
public sealed class NonCodeFileSelectionTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public NonCodeFileSelectionTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Calc.cs", "namespace Lib; public class Calc { public int Tax() => 2; }");
        _repo.Write("src/Lib/README.md", "readme");
        _repo.Write("src/Lib/seed.json", "{}");
        // A source generator the test project runs in its compiler.
        _repo.Write("src/Gen/Gen.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Gen/Generator.cs", "namespace Gen; public class Generator { public int Run() => 1; }");
        _repo.Write("src/Gen/README.md", "generator readme");
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
            public class DocsTests { [Fact] public void Reads() => Assert.NotEmpty(System.IO.File.ReadAllText("../../src/Lib/README.md")); }
            """);
        // Integration tests that read a file of a project they do not reference.
        _repo.Write("tests/Seed.Tests/Seed.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Seed.Tests/Tests.cs", """
            namespace Seed.Tests;
            public class SeedTests { [Fact] public void Seeds() => Assert.NotEmpty(System.IO.File.ReadAllText("../../src/Lib/seed.json")); }
            public class OtherTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void An_inert_file_runs_only_the_classes_that_read_it()
    {
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Lib.Tests.DocsTests"], selection.TestClasses);
        Assert.Contains("read src/Lib/README.md", selection.Decisions.Single(static d => d.Selected).Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inert_file_nobody_read_runs_nothing()
    {
        WriteMaps(docsReads: []);
        _repo.Write("src/Lib/README.md", "changed");

        Assert.All(Select(), static s => Assert.Equal(TestRunMode.Skipped, s.Mode));
    }

    [Fact]
    public void Under_the_project_rule_an_inert_file_runs_its_dependents_completely()
    {
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select(NonCodeFileRule.Project).Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("README.md is not C#, Visual Basic or F# code", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_that_did_not_see_third_party_reads_cannot_vouch_for_an_inert_file()
    {
        WriteMaps(thirdPartyRecorded: false);
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("before the reads of third-party libraries were", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_that_could_not_rewrite_a_library_cannot_vouch_for_an_inert_file()
    {
        WriteMaps(unseen: ["Native.Wrapper.dll"]);
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("could not see the reads of Native.Wrapper.dll", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_map_an_inert_file_runs_the_test_project_completely()
    {
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("no coverage map", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_project_without_a_map_runs_alone_and_its_dependents_keep_their_readers()
    {
        // A test helper project without tests: recognised as a test project, so it has no map.
        _repo.Write("tests/Helpers/Helpers.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                <ProjectReference Include="../Helpers/Helpers.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Commit("helpers");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selections = new AnalysisPipeline(NullLoggerFactory.Instance, new Evaluations(Root) { WithHelpers = true })
            .Run(_repo.Root, "main", new AnalysisOptions()).Tests;

        Assert.Equal(TestRunMode.All, selections.Single(static s => s.Project.Path.EndsWith("Helpers.csproj", StringComparison.Ordinal)).Mode);
        Assert.Equal(["Lib.Tests.DocsTests"], selections.Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal)).TestClasses);
    }

    [Fact]
    public void A_file_the_build_copies_runs_its_dependents_completely()
    {
        WriteMaps();
        _repo.Write("src/Lib/seed.json", """{ "a": 1 }""");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("the project's build uses it", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_an_msbuild_file_names_is_a_build_input()
    {
        _repo.Write("src/Lib/Directory.Build.targets", """
            <Project>
              <Target Name="Notes" BeforeTargets="Build"><ReadLinesFromFile File="README.md" /></Target>
            </Project>
            """);
        _repo.Commit("target");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("src/Lib/Directory.Build.targets names it", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_projects_file_naming_its_own_readme_names_no_other()
    {
        _repo.Write("src/Gen/Gen.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="Notes" BeforeTargets="Build"><ReadLinesFromFile File="README.md" /></Target>
            </Project>
            """);
        _repo.Commit("gen notes");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(["Lib.Tests.DocsTests"], selection.TestClasses);
    }

    [Fact]
    public void A_project_file_naming_its_file_by_a_relative_path_is_a_build_input()
    {
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="Notes" BeforeTargets="Build"><Exec Command="cat ./README.md" /></Target>
            </Project>
            """);
        _repo.Commit("lib notes");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("src/Lib/Lib.csproj names it", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_a_custom_item_type_can_include_is_a_build_input()
    {
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Templates Include="**/*.md" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("templates");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("the Templates items of src/Lib/Lib.csproj can include it", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_in_an_embedded_files_pattern_does_not_make_every_file_a_build_input()
    {
        // OpenTelemetry, 2026-10-15: EmbeddedFiles Include="$(GeneratedAssemblyInfoFile)" in src/Directory.Build.targets
        // counted as matching every file, so each pull request's CHANGELOG.md entry ran every test project completely.
        // MSBuild's evaluation reports the files EmbeddedFiles holds, as it does for Compile or Content.
        _repo.Write("src/Directory.Build.targets", """
            <Project>
              <ItemGroup><EmbeddedFiles Include="$(GeneratedAssemblyInfoFile)" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("embedded files");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.DocsTests"], selection.TestClasses);
    }

    [Fact]
    public void A_pattern_whose_literal_parts_cannot_match_the_file_is_no_build_input()
    {
        // OpenTelemetry, 2026-10-15: a target in build/Common.prod.props collects signature files from the artifacts folder;
        // the whole pattern counted as matching every file, so each pull request's CHANGELOG.md ran every test project.
        _repo.Write("build/Common.props", """
            <Project>
              <Target Name="IncludeSigningSignaturesInPackages">
                <ItemGroup>
                  <SigstoreBundle Include="$(ArtifactsPath)\bin\$(MSBuildProjectName)\$(Configuration.ToLower())_*\$(MSBuildProjectName).dll.sigstore.json" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        _repo.Commit("signatures");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["Lib.Tests.DocsTests"], selection.TestClasses);
    }

    [Theory]
    [InlineData("$(DocsDirectory)/*.md")]
    [InlineData("$(MSBuildThisFileDirectory)$(Docs)/**/*.md")]
    [InlineData("$(Readme)")]
    [InlineData("@(Docs->'%(FullPath)')")]
    public void A_pattern_with_a_property_that_can_match_the_file_is_a_build_input(string include)
    {
        _repo.Write("src/Lib/Lib.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Templates Include="{include}" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("templates");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
        Assert.Contains("the Templates items of src/Lib/Lib.csproj can include it", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_metadata_and_comments_do_not_make_a_file_a_build_input()
    {
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup>
              <!-- README.md goes into the package -->
              <ItemGroup><None Include="README.md" Pack="true" PackagePath="/" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("pack");
        WriteMaps();
        _repo.Write("src/Lib/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(["Lib.Tests.DocsTests"], selection.TestClasses);
    }

    [Fact]
    public void A_generator_whose_inert_files_alone_changed_generates_what_it_did()
    {
        WriteMaps();
        _repo.Write("src/Gen/README.md", "changed");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.Skipped, selection.Mode);
    }

    [Fact]
    public void A_generator_with_a_code_change_still_runs_its_consumers_when_its_output_cannot_be_compared()
    {
        WriteMaps();
        _repo.Write("src/Gen/README.md", "changed");
        _repo.Write("src/Gen/Generator.cs", "namespace Gen; public class Generator { public int Run() => 2; }");

        var selection = Select().Single(static s => s.Project.Path.EndsWith("Lib.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.All, selection.Mode);
    }

    [Theory]
    [InlineData(NonCodeFileRule.Readers)]
    [InlineData(NonCodeFileRule.Project)]
    public void Readers_in_a_test_project_that_does_not_depend_on_the_owner_run_under_both_rules(NonCodeFileRule rule)
    {
        WriteMaps();
        _repo.Write("src/Lib/seed.json", """{ "a": 1 }""");

        var selection = Select(rule).Single(static s => s.Project.Path.EndsWith("Seed.Tests.csproj", StringComparison.Ordinal));

        Assert.Equal(["Seed.Tests.SeedTests"], selection.TestClasses);
    }

    private IReadOnlyList<TestProjectSelection> Select(NonCodeFileRule rule = NonCodeFileRule.Readers) =>
        new AnalysisPipeline(NullLoggerFactory.Instance, new Evaluations(Root)).Run(_repo.Root, "main", new AnalysisOptions { NonCodeFiles = rule }).Tests;

    /// <summary>The repository root as git reports it, which is how the pipeline keys projects.</summary>
    private string Root => new GitClient(NullLogger<GitClient>.Instance).TopLevel(_repo.Root);

    private void WriteMaps(IReadOnlyList<string>? docsReads = null, bool thirdPartyRecorded = true, IReadOnlyList<string>? unseen = null)
    {
        var head = new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root);
        WriteMap(head, "tests/Lib.Tests/Lib.Tests.csproj", [new CoveredMethod("Lib.Calc", "Tax")], new Dictionary<string, IReadOnlyList<int>>
        {
            ["Lib.Tests.CalcTests"] = [0],
            ["Lib.Tests.DocsTests"] = [],
        }, new Dictionary<string, IReadOnlyList<string>> { ["Lib.Tests.DocsTests"] = docsReads ?? ["src/Lib/README.md"] }, thirdPartyRecorded ? unseen ?? [] : null);
        WriteMap(head, "tests/Seed.Tests/Seed.Tests.csproj", [], new Dictionary<string, IReadOnlyList<int>>
        {
            ["Seed.Tests.SeedTests"] = [],
            ["Seed.Tests.OtherTests"] = [],
        }, new Dictionary<string, IReadOnlyList<string>> { ["Seed.Tests.SeedTests"] = ["src/Lib/seed.json"] }, []);
    }

    private void WriteMap(
        string head,
        string project,
        IReadOnlyList<CoveredMethod> methods,
        Dictionary<string, IReadOnlyList<int>> classes,
        Dictionary<string, IReadOnlyList<string>> reads,
        IReadOnlyList<string>? unseen)
    {
        var map = new CoverageMap(
            new CoverageMapHeader(project, head, DateTimeOffset.UtcNow),
            methods,
            new CoverageHits(classes, new Dictionary<string, IReadOnlyList<int>>(), []),
            null)
        {
            FailedClasses = new HashSet<string>(),
            FileReads = new FileReads(reads, new Dictionary<string, IReadOnlyList<string>>(), [], []),
            FileReadsUnseen = unseen,
        };
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, project)), map);
    }

    /// <summary>What MSBuild would report: README.md is a None item nothing builds; seed.json is copied to the output.</summary>
    /// <param name="root">Repository root as git reports it.</param>
    private sealed class Evaluations(string root) : IProjectEvaluator
    {
        /// <summary>True when the repository has the test helper project, which Lib.Tests references.</summary>
        public bool WithHelpers { get; init; }

        public IReadOnlyDictionary<string, EvaluatedProject>? Evaluate(string repoRoot, IReadOnlyCollection<string> projects)
        {
            string Full(string relative) => Paths.Normalize(Path.Combine(root, relative));
            EvaluatedProject Project(bool test, string[] references, string[] items, string[] inert) =>
                new(test, null, [.. references.Select(Full)], items.Select(Full).ToHashSet(Paths.Comparer), new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                {
                    InertItems = inert.Select(Full).ToHashSet(Paths.Comparer),
                };

            var generator = new[] { Full("src/Gen/Gen.csproj") }.ToHashSet(Paths.Comparer);
            string[] libTestsReferences = WithHelpers ? ["src/Lib/Lib.csproj", "src/Gen/Gen.csproj", "tests/Helpers/Helpers.csproj"] : ["src/Lib/Lib.csproj", "src/Gen/Gen.csproj"];
            var evaluations = new Dictionary<string, EvaluatedProject>(Paths.Comparer)
            {
                [Full("src/Lib/Lib.csproj")] = Project(false, [], ["src/Lib/Calc.cs", "src/Lib/README.md", "src/Lib/seed.json"], ["src/Lib/README.md"]),
                [Full("src/Gen/Gen.csproj")] = Project(false, [], ["src/Gen/Generator.cs", "src/Gen/README.md"], ["src/Gen/README.md"]),
                [Full("tests/Lib.Tests/Lib.Tests.csproj")] = Project(true, libTestsReferences, ["tests/Lib.Tests/Tests.cs"], []) with
                {
                    OpaqueReferences = generator,
                    GeneratorReferences = generator,
                },
                [Full("tests/Seed.Tests/Seed.Tests.csproj")] = Project(true, [], ["tests/Seed.Tests/Tests.cs"], []),
            };
            if (WithHelpers)
            {
                evaluations[Full("tests/Helpers/Helpers.csproj")] = Project(true, ["src/Lib/Lib.csproj"], [], []);
            }

            return evaluations;
        }
    }
}
