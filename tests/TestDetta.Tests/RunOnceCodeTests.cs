using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Code that runs once per process and caches its result is credited only to the first test class
/// that triggers it. Recording must say so, and a change to it must select every class that uses the
/// cache, however the classes were spread over recording processes. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class RunOnceCodeTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public RunOnceCodeTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Cache/Cache.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Cache/Catalog.cs", """
            namespace Cache;
            // Filled by the static constructor, once per process.
            public static class Catalog
            {
                private static readonly int[] Items = Build();
                public static int Count => Items.Length;
                private static int[] Build() => [1, 2];
            }

            // Filled by cache factories; the instance method they call is only covered by the cache rule.
            public sealed class Maker { public int Make(string key) => key.Length; }
            public static class PatternCache
            {
                private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Cache = new();
                public static int Get(string key) => Cache.GetOrAdd(key, static k => new Maker().Make(k));
            }
            public sealed class Computer { public int Compute() => 3; }
            public static class LazyValue
            {
                private static readonly Lazy<int> Value = new(static () => new Computer().Compute());
                public static int Get() => Value.Value;
            }

            // A shared instance builds its member on first use (Open-XML-SDK's DefaultFeatures); the
            // instance method the constructor calls is only covered by the cache rule.
            public sealed class Sizer { public int Measure() => 4; }
            public sealed class Resolver { public Resolver() => Size = new Sizer().Measure(); public int Size { get; } }
            public sealed class Features
            {
                public static Features Shared { get; } = new();
                private Resolver? _resolver;
                public Resolver Resolver
                {
                    get
                    {
                        if (_resolver is null)
                        {
                            System.Threading.Interlocked.CompareExchange(ref _resolver, new Resolver(), null);
                        }

                        return _resolver;
                    }
                }
            }

            // An object pool calls its policy's Create only when it is empty (Meziantou.Analyzer's copy
            // of Microsoft.Extensions.ObjectPool); the instance method Create calls is only covered by the cache rule.
            public sealed class BufferMaker { public System.Text.StringBuilder Make() => new(16); }
            public abstract class PooledObjectPolicy<T> { public abstract T Create(); }
            public sealed class BufferPolicy : PooledObjectPolicy<System.Text.StringBuilder> { public override System.Text.StringBuilder Create() => new BufferMaker().Make(); }
            public sealed class Pool<T>(PooledObjectPolicy<T> policy) where T : class
            {
                private readonly Stack<T> _items = new();
                public T Get() => _items.TryPop(out var item) ? item : policy.Create();
                public void Return(T item) => _items.Push(item);
            }
            // Microsoft.Extensions.ObjectPool's DefaultObjectPool binds Create to a delegate in its
            // constructor and calls the delegate on a miss (Meziantou.Analyzer's copy too).
            public sealed class WideMaker { public System.Text.StringBuilder Make() => new(32); }
            public sealed class WidePolicy : PooledObjectPolicy<System.Text.StringBuilder> { public override System.Text.StringBuilder Create() => new WideMaker().Make(); }
            public sealed class DelegatePool<T> where T : class
            {
                private readonly Func<T> _create;
                private readonly Stack<T> _items = new();
                public DelegatePool(PooledObjectPolicy<T> policy) => _create = policy.Create;
                public T Get() => _items.TryPop(out var item) ? item : _create();
                public void Return(T item) => _items.Push(item);
            }
            public static class WideBuffers
            {
                private static readonly DelegatePool<System.Text.StringBuilder> Shared = new(new WidePolicy());
                public static int Capacity() { var b = Shared.Get(); var c = b.Capacity; Shared.Return(b); return c; }
            }
            public static class Buffers
            {
                private static readonly Pool<System.Text.StringBuilder> Shared = new(new BufferPolicy());
                public static int Capacity() { var b = Shared.Get(); var c = b.Capacity; Shared.Return(b); return c; }
            }

            // Filled on first use, once per process.
            public static class LazyCatalog
            {
                private static int[]? _cache;
                public static int Count() => (_cache ??= Load()).Length;
                public static int[] Load() => [1];
            }
            """);
        _repo.Write("tests/Cache.Tests/Cache.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Cache/Cache.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Cache.Tests/Tests.cs", """
            using Xunit;
            namespace Cache.Tests;
            public class AlphaTests { [Fact] public void Counts() { Assert.Equal(2, Catalog.Count); Assert.Equal(1, LazyCatalog.Count()); Assert.Equal(2, PatternCache.Get("ab")); Assert.Equal(3, LazyValue.Get()); Assert.Equal(4, Features.Shared.Resolver.Size); Assert.Equal(16, Buffers.Capacity()); Assert.Equal(32, WideBuffers.Capacity()); } }
            public class BetaTests { [Fact] public void Counts() { Assert.Equal(2, Catalog.Count); Assert.Equal(1, LazyCatalog.Count()); Assert.Equal(2, PatternCache.Get("ab")); Assert.Equal(3, LazyValue.Get()); Assert.Equal(4, Features.Shared.Resolver.Size); Assert.Equal(16, Buffers.Capacity()); Assert.Equal(32, WideBuffers.Capacity()); } }
            public class GammaTests { [Fact] public void Counts() { Assert.Equal(2, Catalog.Count); Assert.Equal(1, LazyCatalog.Count()); Assert.Equal(2, PatternCache.Get("ab")); Assert.Equal(3, LazyValue.Get()); Assert.Equal(4, Features.Shared.Resolver.Size); Assert.Equal(16, Buffers.Capacity()); Assert.Equal(32, WideBuffers.Capacity()); } }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Changes_to_cached_code_select_every_class_that_uses_the_cache()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);

        // Two processes for three classes: one process runs two classes, and the second finds the caches full.
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 2)), TextWriter.Null);

        Assert.True(passed);
        Assert.Contains(new CoveredMethod("Cache.Catalog", "Build"), map.Initialization.Select(id => map.Methods[id]));
        Assert.DoesNotContain(new CoveredMethod("Cache.LazyCatalog", "Load"), map.SharedInProcess!.Select(id => map.Methods[id]));
        Assert.Contains(new CoveredMethod("Cache.Maker", "Make"), map.Initialization.Select(id => map.Methods[id]));
        Assert.Contains(new CoveredMethod("Cache.Computer", "Compute"), map.Initialization.Select(id => map.Methods[id]));
        Assert.Contains(new CoveredMethod("Cache.Sizer", "Measure"), map.Initialization.Select(id => map.Methods[id]));
        Assert.Contains(new CoveredMethod("Cache.BufferMaker", "Make"), map.Initialization.Select(id => map.Methods[id]));
        Assert.Contains(new CoveredMethod("Cache.WideMaker", "Make"), map.Initialization.Select(id => map.Methods[id]));

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        string[] all = ["Cache.Tests.AlphaTests", "Cache.Tests.BetaTests", "Cache.Tests.GammaTests"];

        _repo.Replace("src/Cache/Catalog.cs", "=> [1, 2];", "=> [2, 1];");
        Assert.Equal(TestRunMode.All, Select().Mode);

        _repo.Replace("src/Cache/Catalog.cs", "=> [2, 1];", "=> [1, 2];");
        _repo.Replace("src/Cache/Catalog.cs", "Load() => [1];", "Load() => [1 + 0];");
        AssertSelectsAll(all);

        _repo.Replace("src/Cache/Catalog.cs", "Load() => [1 + 0];", "Load() => [1];");
        _repo.Replace("src/Cache/Catalog.cs", "=> key.Length;", "=> key.Length + 0;");
        AssertSelectsAll(all);

        _repo.Replace("src/Cache/Catalog.cs", "=> key.Length + 0;", "=> key.Length;");
        _repo.Replace("src/Cache/Catalog.cs", "Compute() => 3;", "Compute() => 1 + 2;");
        AssertSelectsAll(all);

        _repo.Replace("src/Cache/Catalog.cs", "Compute() => 1 + 2;", "Compute() => 3;");
        _repo.Replace("src/Cache/Catalog.cs", "Measure() => 4;", "Measure() => 2 + 2;");
        AssertSelectsAll(all);

        _repo.Replace("src/Cache/Catalog.cs", "Measure() => 2 + 2;", "Measure() => 4;");
        _repo.Replace("src/Cache/Catalog.cs", "Make() => new(16);", "Make() => new(8 + 8);");
        AssertSelectsAll(all);

        _repo.Replace("src/Cache/Catalog.cs", "Make() => new(8 + 8);", "Make() => new(16);");
        _repo.Replace("src/Cache/Catalog.cs", "Make() => new(32);", "Make() => new(16 + 16);");
        AssertSelectsAll(all);
    }

    /// <summary>Asserts that the current change runs every one of <paramref name="classes"/>.</summary>
    private void AssertSelectsAll(string[] classes)
    {
        var selection = Select();
        Assert.True(selection.Mode == TestRunMode.All || classes.All(selection.TestClasses.Contains), string.Join(", ", selection.TestClasses));
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

    public void Dispose() => _repo.Dispose();
}
