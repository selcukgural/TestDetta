using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// A DI extension of the test framework (Xunit.DependencyInjection and the like) builds services
/// between tests and injects them into test class constructors; a singleton is built once, for
/// whichever class needs it first, and handed to the others without running any code. A change to
/// what builds such a service must select every class it is injected into. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class InjectedServiceTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Change_to_an_injected_singleton_selects_every_class_it_is_injected_into()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Clock.cs", """
            namespace App;
            public interface IClock { int Now { get; } }
            public sealed class Clock : IClock
            {
                private readonly int _start;
                public Clock() { _start = 40 + Offset.Two(); }
                public int Now => _start;
            }
            public static class Offset { public static int Two() => 2; }
            public static class Math2 { public static int Three() => 3; }
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <PackageReference Include="Xunit.DependencyInjection" Version="9.9.0" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Startup.cs", """
            using Microsoft.Extensions.DependencyInjection;
            namespace App.Tests;
            public class Startup
            {
                public void ConfigureServices(IServiceCollection services) => services.AddSingleton<IClock, Clock>();
            }
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            public class AlphaTests(IClock clock) { [Fact] public void Is_42() => Assert.Equal(42, clock.Now); }
            public class BetaTests(IClock clock) { [Fact] public void Is_positive() => Assert.True(clock.Now > 0); }
            public class GammaTests(IClock clock) { [Fact] public void Is_even() => Assert.Equal(0, clock.Now % 2); }
            public class PlainTests { [Fact] public void Three() => Assert.Equal(3, Math2.Three()); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        _repo.Replace("src/App/Clock.cs", "_start = 40 + Offset.Two();", "_start = 41 + Offset.Two();");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        string[] users = ["App.Tests.AlphaTests", "App.Tests.BetaTests", "App.Tests.GammaTests"];
        Assert.True(selection.Mode == TestRunMode.All || users.All(selection.TestClasses.Contains),
            $"{selection.Mode}: {string.Join(", ", selection.TestClasses)}; map: "
            + string.Join("; ", map.Classes.Select(c => $"{c.Key}=[{string.Join(",", c.Value.Select(id => map.Methods[id]))}]"))
            + $"; ambient=[{string.Join(",", map.Ambient.Select(id => map.Methods[id]))}]; init=[{string.Join(",", map.Initialization.Select(id => map.Methods[id]))}]");
    }

    [Fact]
    public void Code_that_builds_a_transient_service_counts_for_the_class_it_is_built_for()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Formatter.cs", """
            namespace App;
            public interface IFormatter { string Format(string text); }
            public sealed class Prefixer { public string Prefix() => "> "; }
            public sealed class Formatter : IFormatter
            {
                private readonly string _prefix;
                public Formatter() => _prefix = new Prefixer().Prefix();
                public string Format(string text) => _prefix + text;
            }
            public static class Math2 { public static int Three() => 3; }
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <PackageReference Include="Xunit.DependencyInjection" Version="9.9.0" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Startup.cs", """
            using Microsoft.Extensions.DependencyInjection;
            namespace App.Tests;
            public class Startup
            {
                public void ConfigureServices(IServiceCollection services) => services.AddTransient<IFormatter, Formatter>();
            }
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            public class AlphaTests(IFormatter formatter)
            {
                [Fact] public async Task Formats() { await Task.Yield(); Assert.Equal("> a", formatter.Format("a")); }
            }
            public class BetaTests(IFormatter formatter) { [Fact] public void Formats() => Assert.Equal("> b", formatter.Format("b")); }
            public class GammaTests(IFormatter formatter) { [Fact] public void Formats() => Assert.StartsWith(">", formatter.Format("c")); }
            public class PlainTests { [Fact] public void Three() => Assert.Equal(3, Math2.Three()); }
            public class OtherTests { [Fact] public async Task Three() { await Task.Delay(1); Assert.Equal(3, Math2.Three()); } }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        // Each user's own instance was built for it: the code that built it is in its bucket (the
        // first one's may run before any test, as start-up), and never in a class that did not use it.
        var prefix = map.Methods.ToList().IndexOf(new CoveredMethod("App.Prefixer", "Prefix"));
        string[] users = ["App.Tests.AlphaTests", "App.Tests.BetaTests", "App.Tests.GammaTests"];
        Assert.All(users, user => Assert.True(map.Classes[user].Contains(prefix) || map.Ambient.Contains(prefix), user));
        Assert.True(users.Count(user => map.Classes[user].Contains(prefix)) >= 2, string.Join(", ", map.Classes.Where(c => c.Value.Contains(prefix)).Select(c => c.Key)));

        // Built between tests at least twice (at most the first instance is built before them): run-once evidence.
        Assert.Contains(prefix, map.Initialization);

        _repo.Replace("src/App/Formatter.cs", "public string Prefix() => \"> \";", "public string Prefix() => \">\" + \" \";");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.True(selection.Mode == TestRunMode.All || users.All(selection.TestClasses.Contains),
            $"{selection.Mode}: {string.Join(", ", selection.TestClasses)}");
    }

    [Fact]
    public void A_failing_test_ends_its_test_code_and_what_follows_goes_to_the_next_class()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Formatter.cs", """
            namespace App;
            public interface IFormatter { string Format(string text); }
            public sealed class Prefixer { public string Prefix() => "> "; }
            public sealed class Formatter : IFormatter
            {
                private readonly string _prefix;
                public Formatter() => _prefix = new Prefixer().Prefix();
                public string Format(string text) => _prefix + text;
            }
            public static class Math2 { public static int Three() => 3; }
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <PackageReference Include="Xunit.DependencyInjection" Version="9.9.0" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Startup.cs", """
            using Microsoft.Extensions.DependencyInjection;
            namespace App.Tests;
            public class Startup
            {
                public void ConfigureServices(IServiceCollection services) => services.AddTransient<IFormatter, Formatter>();
            }
            """);
        // Classes run in name order: one passing test first (tests have started), then a failing one,
        // then the user; its formatter is built between the failure and its own test.
        _repo.Write("tests/App.Tests/Order.cs", """
            using Xunit;
            using Xunit.Abstractions;
            [assembly: TestCollectionOrderer("App.Tests.ByName", "App.Tests")]
            [assembly: CollectionBehavior(DisableTestParallelization = true)]
            namespace App.Tests;
            public sealed class ByName : ITestCollectionOrderer
            {
                public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> collections) =>
                    collections.OrderBy(static c => c.DisplayName, StringComparer.Ordinal);
            }
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            public class A_PlainTests { [Fact] public void Three() => Assert.Equal(3, Math2.Three()); }
            public class B_FailingTests { [Fact] public void Fails() => Assert.Equal(4, Math2.Three()); }
            public class C_UserTests(IFormatter formatter) { [Fact] public void Formats() => Assert.Equal("> c", formatter.Format("c")); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.False(passed);
        Assert.Equal(["App.Tests.B_FailingTests"], map.FailedClasses!.Order(StringComparer.Ordinal));

        // The failing test's exception still ended its test code, so the formatter's construction is
        // between tests: credited to the user and marked as run-once.
        var prefix = map.Methods.ToList().IndexOf(new CoveredMethod("App.Prefixer", "Prefix"));
        Assert.Contains(prefix, map.Classes["App.Tests.C_UserTests"]);
        Assert.Contains(prefix, map.Initialization);
    }

    public void Dispose() => _repo.Dispose();
}
