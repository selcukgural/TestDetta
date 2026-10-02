using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// A hand-rolled pool keeps its objects in a collection: the first class that rents builds one, the
/// next ones take it from the collection without running the code that built it. A change to that
/// code must select every class that rents from the pool. Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class PooledObjectTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    /// <summary>Pool shapes: what <c>BufferPool.Shared.Rent()</c> does on a miss and a hit.</summary>
    public static TheoryData<string> Pools =>
    [
        // A stack rented with Count/Pop, filled by a factory object.
        """
            public sealed class BufferPool
            {
                public static readonly BufferPool Shared = new();
                private readonly Stack<Buffer> _items = new();
                private readonly Factory _factory = new();
                public Buffer Rent() => _items.Count > 0 ? _items.Pop() : _factory.Create();
                public void Return(Buffer buffer) => _items.Push(buffer);
            }
            """,
        // A hand-rolled dictionary cache.
        """
            public sealed class BufferPool
            {
                public static readonly BufferPool Shared = new();
                private readonly Dictionary<int, Buffer> _cache = new();
                private readonly Factory _factory = new();
                public Buffer Rent()
                {
                    if (!_cache.TryGetValue(16, out var buffer))
                    {
                        buffer = _factory.Create();
                        _cache[16] = buffer;
                    }

                    return buffer;
                }
                public void Return(Buffer buffer) { }
            }
            """,
        // A generic pool over a factory delegate.
        """
            public sealed class Pool<T>(Func<T> create) where T : class
            {
                private readonly System.Collections.Concurrent.ConcurrentBag<T> _items = new();
                public T Rent() => _items.TryTake(out var item) ? item : create();
                public void Return(T item) => _items.Add(item);
            }
            public sealed class BufferPool
            {
                public static readonly BufferPool Shared = new();
                private readonly Pool<Buffer> _pool = new(new Factory().Create);
                public Buffer Rent() => _pool.Rent();
                public void Return(Buffer buffer) => _pool.Return(buffer);
            }
            """,
    ];

    [Theory]
    [MemberData(nameof(Pools))]
    public void Change_to_code_that_fills_a_pool_on_a_miss_selects_every_renter(string pool)
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Pool.cs", """
            namespace App;
            public sealed class Buffer { public int Size; }
            public sealed class Factory
            {
                public Buffer Create() { var buffer = new Buffer(); buffer.Size = 16; return buffer; }
            }
            public static class Math2 { public static int Three() => 3; }
            """ + pool);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            public class AlphaTests { [Fact] public void Sixteen() { var b = BufferPool.Shared.Rent(); Assert.Equal(16, b.Size); BufferPool.Shared.Return(b); } }
            public class BetaTests { [Fact] public void Sixteen() { var b = BufferPool.Shared.Rent(); Assert.Equal(16, b.Size); BufferPool.Shared.Return(b); } }
            public class GammaTests { [Fact] public void Sixteen() { var b = BufferPool.Shared.Rent(); Assert.Equal(16, b.Size); BufferPool.Shared.Return(b); } }
            public class PlainTests { [Fact] public void Three() => Assert.Equal(3, Math2.Three()); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        _repo.Replace("src/App/Pool.cs", "buffer.Size = 16;", "buffer.Size = 17;");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        string[] renters = ["App.Tests.AlphaTests", "App.Tests.BetaTests", "App.Tests.GammaTests"];
        Assert.True(selection.Mode == TestRunMode.All || renters.All(selection.TestClasses.Contains),
            $"{selection.Mode}: {string.Join(", ", selection.TestClasses)}; create ran in: "
            + string.Join(", ", map.Classes.Where(c => c.Value.Any(id => map.Methods[id] == new CoveredMethod("App.Factory", "Create"))).Select(c => c.Key)));
    }

    public void Dispose() => _repo.Dispose();
}
