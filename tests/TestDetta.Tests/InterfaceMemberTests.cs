using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// An interface member the name spread reaches has no code of its own: a call through it runs one of
/// its implementations. When the maps know every implementation in the repository, their coverage
/// answers for it, instead of tracing its simple name into every call of an unrelated member with the
/// same name (AfterApply's IApplicationService.CreateAsync reached every CreateAsync of the codebase).
/// Without an implementation the repository declares, the name is traced as before. Slow (it runs
/// dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class InterfaceMemberTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_interface_member_is_answered_through_the_coverage_of_its_implementations(bool alsoUnimplemented)
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/App/Result.cs", "namespace App; public sealed record Result(int Value);");
        _repo.Write("src/App/Service.cs", $$"""
            namespace App;
            public interface IService { Result Create(int value); }
            public abstract class ServiceBase : IService { public abstract Result Create(int value); }
            public sealed class Service : ServiceBase { public override Result Create(int value) => new(value); }
            {{(alsoUnimplemented ? "public interface IOther { Result Create(int value); }" : "")}}

            // Unrelated, with the same simple name.
            public sealed class Store { public int Create() => 7; }
            """);
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
            public class ServiceTests { [Fact] public void Creates() => Assert.Equal(2, ((App.IService)new App.Service()).Create(2).Value); }
            public class StoreTests { [Fact] public void Creates() => Assert.Equal(7, new App.Store().Create()); }
            public class PlainTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        // Changed metadata: the record's name spreads to whatever names it, the interface member included.
        _repo.Replace("src/App/Result.cs", "int Value)", "int Value, string? Note = null)");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.Equal(
            alsoUnimplemented ? ["App.Tests.ServiceTests", "App.Tests.StoreTests"] : ["App.Tests.ServiceTests"],
            selection.TestClasses);
    }
}
