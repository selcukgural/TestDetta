using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// xUnit sets a collection fixture up for every class of its collections, also for those that never ask for it and only
/// depend on what its setup did (Wolverine's NATS tests find the container through an environment variable). Such a class
/// names nothing of the fixture, so before 2026-10-19 a change to the setup, or to code only the setup runs, missed it
/// (a reader's question on the dev.to introduction). The recorder now reads the collections from the test assembly.
/// Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class CollectionFixtureTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("xunit")]
    [InlineData("xunit.v3")]
    public void Every_class_of_a_collection_uses_its_fixture(string framework)
    {
        var v3 = framework == "xunit.v3";
        _repo.Write("global.json", v3
            ? """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }"""
            : """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Db.cs", Db("""Db.Rows.Add("a"); Db.Rows.Add("b");"""));
        var packages = v3
            ? """<PackageReference Include="xunit.v3" Version="4.0.1" />"""
            : """
              <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
              <PackageReference Include="xunit" Version="2.9.3" />
              <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              """;
        _repo.Write("tests/App.Tests/App.Tests.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>{{(v3 ? "<OutputType>Exe</OutputType>" : "")}}</PropertyGroup>
              <ItemGroup>
                {{packages}}
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/DbFixture.cs", Fixture("Seeder.Seed();"));
        _repo.Write("tests/App.Tests/Tests.cs", $$"""
            using Xunit;
            namespace App.Tests;
            public static class Names { public const string Db = "Db"; }
            [Collection(Names.Db)] public class WithParamTests(DbFixture db) { [Fact] public void Seeded() { Assert.NotNull(db); Assert.Equal(2, Db.Count()); } }
            // Asks for nothing, reads what the setup left behind.
            [Collection(Names.Db)] public class NoParamTests { [Fact] public void Seeded() => Assert.Equal(2, Db.Rows.Count); }
            [Collection(Names.Db)] public abstract class DbTestsBase { [Fact] public void Seeded() => Assert.Equal(2, Db.Count()); }
            public class DerivedTests : DbTestsBase;
            public class OutsideTests { [Fact] public void Two() => Assert.Equal(2, Math2.Two()); }
            {{(v3 ? """
                [Collection(typeof(TypedCollection))] public class ByTypeTests { [Fact] public void Seeded() => Assert.Equal(2, Db.Count()); }
                [Collection<TypedCollection>] public class ByGenericTests { [Fact] public void Seeded() => Assert.Equal(2, Db.Count()); }
                """ : "")}}
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        string[] members = v3
            ? ["App.Tests.ByGenericTests", "App.Tests.ByTypeTests", "App.Tests.DerivedTests", "App.Tests.NoParamTests", "App.Tests.WithParamTests"]
            : ["App.Tests.DerivedTests", "App.Tests.NoParamTests", "App.Tests.WithParamTests"];
        Assert.Equal(members, map.CollectionFixtureUsers!["App.Tests.DbFixture"].Order(StringComparer.Ordinal));
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        Assert.Equal(members, CoverageMapStore.Read(CoverageMapStore.PathFor(_repo.Root, testProject.Path))!.CollectionFixtureUsers!["App.Tests.DbFixture"]);

        // Code only the fixture's setup runs.
        _repo.Write("src/App/Db.cs", Db("""Db.Rows.Add("a"); Db.Rows.Add("b"); Db.Rows.Add("c");"""));
        Assert.Equal(members, Selected());
        _repo.Write("src/App/Db.cs", Db("""Db.Rows.Add("a"); Db.Rows.Add("b");"""));

        // The fixture's own setup.
        _repo.Write("tests/App.Tests/DbFixture.cs", Fixture("""Seeder.Seed(); Db.Rows.Add("z");"""));
        Assert.Equal(members, Selected());

        string Db(string seed) => $$"""
            namespace App;
            public static class Db { public static readonly List<string> Rows = []; public static int Count() => Rows.Count; }
            // Idempotent: v3 may set the fixture up once per collection, and two collections share it here.
            public static class Seeder { public static void Seed() { Db.Rows.Clear(); {{seed}} } }
            public static class Math2 { public static int Two() => 2; }
            """;

        string Fixture(string setup) => $$"""
            using Xunit;
            namespace App.Tests;
            public sealed class DbFixture
            {
                public DbFixture()
                {
                    {{setup}}
                }
            }
            [CollectionDefinition(Names.Db)] public sealed class DbCollection : ICollectionFixture<DbFixture>;
            {{(v3 ? "[CollectionDefinition] public sealed class TypedCollection : ICollectionFixture<DbFixture>;" : "")}}
            """;

        IOrderedEnumerable<string> Selected()
        {
            var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();
            Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
            return selection.TestClasses.Order(StringComparer.Ordinal);
        }
    }
}
