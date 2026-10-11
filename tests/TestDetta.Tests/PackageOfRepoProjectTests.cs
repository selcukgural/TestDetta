using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A project that takes one of the repository's own projects as a package depends on it: a restore holding both uses the
/// project in place of the package, so the code built against the package runs with the repository's build (AICentral,
/// which takes AICentral.Core as a package while its tests take the project).
/// </summary>
public sealed class PackageOfRepoProjectTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public PackageOfRepoProjectTests()
    {
        _repo.Write("src/Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Core/Wrapper.cs", """
            namespace Core;
            public interface IContext { string Name { get; } }
            public class Wrapper : IContext { public string Name => "w"; }
            """);
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Core" Version="0.21.0-pr" /><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/App/Factory.cs", "namespace App; public static class Factory { public static Core.IContext Wrap() => new Core.Wrapper(); }");
        _repo.Write("src/Named/Named.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>Acme.Named</PackageId></PropertyGroup></Project>""");
        _repo.Write("src/Uses/Uses.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="acme.named" Version="1.0.0" /></ItemGroup></Project>""");
        _repo.Write("tests/Uses.Tests/Uses.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /><ProjectReference Include="../../src/Uses/Uses.csproj" /><ProjectReference Include="../../src/Named/Named.csproj" /></ItemGroup>
            </Project>
            """);
        // Takes the package alone: no restore holds Core's project with it, so the published package is what runs.
        _repo.Write("tests/Consumer.Tests/Consumer.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /><PackageReference Include="Core" Version="0.21.0" /></ItemGroup></Project>
            """);
        _repo.Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/App/App.csproj" />
                <ProjectReference Include="../../src/Core/Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/App.Tests/Tests.cs", """
            using Xunit;
            namespace App.Tests;
            public class WrapTests { [Fact] public void Wraps() => Assert.Equal("w", App.Factory.Wrap().Name); }
            public class OtherTests { [Fact] public void Nothing() { } }
            """);
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_package_of_a_repository_project_is_a_reference_to_it()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        string PathOf(string name) => graph.Projects.Single(p => Path.GetFileNameWithoutExtension(p.Path) == name).Path;

        Assert.Contains(PathOf("App"), graph.DependentsOf(PathOf("Core")));
        // By its PackageId, whatever the case; a package no project makes adds nothing.
        Assert.Contains(PathOf("Uses"), graph.DependentsOf(PathOf("Named")));
        Assert.Equal([PathOf("Core")], graph[PathOf("App")].References);
        Assert.Empty(graph[PathOf("Consumer.Tests")].References);
    }

    [Fact]
    public void A_type_only_the_package_user_creates_is_named_so_the_change_is_traced_instead_of_running_everything()
    {
        _repo.Replace("src/Core/Wrapper.cs", "=> \"w\"", "=> \"v\"");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single(static t => t.Project.Path.EndsWith("App.Tests.csproj", StringComparison.Ordinal));

        Assert.True(selection.Mode == TestRunMode.Filtered, $"{selection.Mode}: {selection.Reason}");
        Assert.Equal(["App.Tests.WrapTests"], selection.TestClasses);
    }
}
