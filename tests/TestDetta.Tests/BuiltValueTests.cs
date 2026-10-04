using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A value built once and kept in a field (a static initializer, <c>_x ??= new X()</c> on a shared
/// object) runs its builder in whichever test class needed it first. Every class that reads the
/// field depends on that builder too, and the map must say so. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class BuiltValueTests : IDisposable
{
    private static readonly string[] Users = ["A", "B", "C"];

    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Code_that_built_a_value_counts_for_every_class_reading_it()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Shop/Shop.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Shop/Shop.cs", """
            namespace Shop;
            // A lazily built static value behind a property.
            public sealed class Catalog { public Catalog() => Items = Load(); public string[] Items { get; } static string[] Load() => ["a", "b"]; }
            public static class Registry { private static Catalog? _catalog; public static Catalog Catalog => _catalog ??= new Catalog(); }
            // A lazily built instance field of a shared object.
            public sealed class Formatter { public Formatter() => Prefix = Prefixes.Make(); public string Prefix { get; } public string Format(int i) => Prefix + i; }
            public static class Prefixes { public static string Make() => "#"; }
            public sealed class Store { private Formatter? _formatter; public string Show(int i) => (_formatter ??= new Formatter()).Format(i); }
            public static class Shared { public static readonly Store Store = new(); }
            // A static readonly initializer read directly by another type's code.
            public sealed class Options { public int Size; public static Options Create() => new() { Size = Sizes.Default() }; }
            public static class Sizes { public static int Default() => 3; }
            public static class Defaults { public static readonly Options Value = Options.Create(); }
            public static class Reader { public static int Size() => Defaults.Value.Size; }
            """);
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        var tests = new System.Text.StringBuilder("using Xunit;\nnamespace Shop.Tests;\n");
        foreach (var n in new[] { "A", "B", "C" })
        {
            tests.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $$"""
                public class Catalog{{n}}Tests { [Fact] public void Items() => Assert.Equal(2, Registry.Catalog.Items.Length); }
                public class Store{{n}}Tests { [Fact] public void Shows() => Assert.Equal("#1", Shared.Store.Show(1)); }
                public class Size{{n}}Tests { [Fact] public void Size() => Assert.Equal(3, Reader.Size()); }
                """);
        }

        _repo.Write("tests/Shop.Tests/Tests.cs", tests.ToString());
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);

        Assert.True(passed);
        var index = new CoverageIndex(map, static _ => []);
        foreach (var (type, member, prefix) in new[] { ("Shop.Catalog", "Load", "Catalog"), ("Shop.Prefixes", "Make", "Store"), ("Shop.Sizes", "Default", "Size") })
        {
            var classes = index.ClassesRunning(type, member);
            Assert.NotNull(classes);
            Assert.All(Users, n => Assert.Contains($"Shop.Tests.{prefix}{n}Tests", classes));
            // Where the builder really ran is unchanged: one class, so the run-once rules still apply.
            Assert.Equal(1, index.BucketsRunning(type, member));
        }
    }

    public void Dispose() => _repo.Dispose();
}
