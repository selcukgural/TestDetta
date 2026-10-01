using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// NUnit runs a <c>[SetUpFixture]</c> once before every test of its namespace, and MSTest an
/// <c>[AssemblyInitialize]</c> method before every test of its assembly; when it fails, all of them
/// fail. Code it runs must select every test class in its scope (MimeKit registers its cryptography
/// module this way). Slow: builds and records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class SetUpFixtureTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Code_run_by_a_setup_fixture_selects_every_class_in_its_namespace()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Mail/Mail.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Mail/Parser.cs", """
            namespace Mail;
            public static class Options { public static object? Factory; public static void Register(object factory) => Factory = factory; }
            public static class CryptoModule { public static void Initialize() => Options.Register(new object()); }
            public static class Parser { public static int Parse(string s) => s.Length; }
            """);
        _repo.Write("tests/Mail.Tests/Mail.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
                <PackageReference Include="NUnit" Version="4.4.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.0" />
                <ProjectReference Include="../../src/Mail/Mail.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Mail.Tests/Tests.cs", """
            using NUnit.Framework;
            namespace Mail.Tests
            {
                [SetUpFixture]
                public class TestHelper { [OneTimeSetUp] public static void Init() => CryptoModule.Initialize(); }

                public class ParserTests { [Test] public void Parses() => Assert.That(Parser.Parse("ab"), Is.EqualTo(2)); }
            }

            namespace Mail.Tests.Deep
            {
                public class DeepTests { [Test] public void Parses() => Assert.That(Parser.Parse("a"), Is.EqualTo(1)); }
            }

            namespace Other.Tests
            {
                public class OtherTests { [Test] public void Parses() => Assert.That(Mail.Parser.Parse(""), Is.EqualTo(0)); }
            }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);

        Assert.True(passed);
        Assert.Contains(new CoveredMethod("Mail.Options", "Register"), map.Fixtures.SelectMany(f => f.Value).Select(id => map.Methods[id]));
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        _repo.Replace("src/Mail/Parser.cs", "=> Factory = factory;", "=> Factory = factory ?? throw new ArgumentNullException();");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        // Both classes in the fixture's namespace depend on it; the class outside it does not.
        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Contains("Mail.Tests.ParserTests", selection.TestClasses);
        Assert.Contains("Mail.Tests.Deep.DeepTests", selection.TestClasses);
        Assert.DoesNotContain("Other.Tests.OtherTests", selection.TestClasses);
    }

    public void Dispose() => _repo.Dispose();
}
