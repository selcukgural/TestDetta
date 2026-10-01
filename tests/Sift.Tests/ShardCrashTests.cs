using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// A suite whose classes only pass together must still get a map when recorded in several
/// processes. Slow (it runs dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class ShardCrashTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Shard_that_takes_its_host_down_is_recorded_again_in_one_process()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Setup.cs", """
            namespace Calc;
            public static class Setup { public static int Prepare() => 1; public static int Use() => 2; }
            """);
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MSTest" Version="4.4.1" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        // Like NLog's suite: LateTests takes the test host down unless EarlyTests ran first in the same
        // process (MSTest runs classes in declaration order). Two shards split them apart.
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            namespace Calc.Tests;
            public static class Shared { public static bool Ready; }
            [TestClass] public class EarlyTests { [TestMethod] public void Prepares() { Assert.AreEqual(1, Calc.Setup.Prepare()); Shared.Ready = true; } }
            [TestClass] public class LateTests
            {
                [TestMethod]
                public void Uses()
                {
                    if (!Shared.Ready)
                    {
                        Environment.FailFast("not prepared");
                    }

                    Assert.AreEqual(2, Calc.Setup.Use());
                }
            }
            """);
        _repo.Commit("base");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);

        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance).Record(
            new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 2)), TextWriter.Null);

        Assert.True(passed);
        Assert.Equal(["Calc.Tests.EarlyTests", "Calc.Tests.LateTests"], map.Classes.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(map.Classes["Calc.Tests.LateTests"], id => map.Methods[id].MemberName == "Use");
    }

    public void Dispose() => _repo.Dispose();
}
