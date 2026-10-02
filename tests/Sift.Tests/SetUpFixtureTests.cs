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

    // Inherited from a base in another namespace: the fixture's own constructor already enters the
    // scope and NUnit calls the inherited setup in that flow; the instrumenter also marks the base's
    // setup with the fixture's scope, for setups that run in a flow of their own.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Code_run_by_a_setup_fixture_selects_every_class_in_its_namespace(bool inheritedFromAnotherNamespace)
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
        var helper = inheritedFromAnotherNamespace
            ? "[SetUpFixture] public class TestHelper : Shared.SetUpBase { }"
            : "[SetUpFixture] public class TestHelper { [OneTimeSetUp] public static void Init() => CryptoModule.Initialize(); }";
        _repo.Write("tests/Mail.Tests/Tests.cs", $$"""
            using NUnit.Framework;
            namespace Shared
            {
                public abstract class SetUpBase { [OneTimeSetUp] public void Init() => Mail.CryptoModule.Initialize(); }
            }

            namespace Mail.Tests
            {
                {{helper}}

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

    [Theory]
    [InlineData("xunit")]
    [InlineData("tunit")]
    public void Assembly_wide_setup_of_xunit_v3_and_tunit_records_under_the_assembly_scope(string framework)
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/Mail/Mail.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Mail/Setup.cs", """
            namespace Mail;
            public static class Setup
            {
                public static int Calls;
                public static void Fixture() => Calls++;
                public static void Module() => Calls++;
                public static void Each() => Calls++;
                public static int Parse(string s) => s.Length;
            }
            """);
        var package = framework == "xunit" ? """<PackageReference Include="xunit.v3" Version="4.0.1" />""" : """<PackageReference Include="TUnit" Version="1.70.1" />""";
        _repo.Write("tests/Mail.Tests/Mail.Tests.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup>{{package}}<ProjectReference Include="../../src/Mail/Mail.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Mail.Tests/Tests.cs", framework == "xunit"
            ? """
              using Xunit;
              [assembly: AssemblyFixture(typeof(Mail.Tests.Host))]
              namespace Mail.Tests;
              public sealed class Host { public Host() => Setup.Fixture(); }
              public static class Init { [System.Runtime.CompilerServices.ModuleInitializer] public static void Run() => Setup.Module(); }
              public class ATests { [Fact] public void Parses() => Assert.Equal(2, Setup.Parse("ab")); }
              public class BTests { [Fact] public void Parses() => Assert.Equal(1, Setup.Parse("a")); }
              """
            : """
              using System.Threading.Tasks;
              using TUnit.Core;
              namespace Mail.Tests;
              public static class Hooks
              {
                  [Before(HookType.Assembly)] public static void Start(AssemblyHookContext context) => Setup.Fixture();
                  [BeforeEvery(HookType.Test)] public static void Each(TestContext context) => Setup.Each();
              }
              public class ATests { [Test] public async Task Parses() => await Assert.That(Setup.Parse("ab")).IsEqualTo(2); }
              public class BTests { [Test] public async Task Parses() => await Assert.That(Setup.Parse("a")).IsEqualTo(1); }
              """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        using var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        var scope = Assert.Contains(CoverageMap.SetUpScopePrefix, map.Fixtures).Select(id => map.Methods[id]).ToList();
        Assert.Contains(new CoveredMethod("Mail.Setup", "Fixture"), scope);
        Assert.Contains(new CoveredMethod("Mail.Setup", framework == "xunit" ? "Module" : "Each"), scope);
    }

    public void Dispose() => _repo.Dispose();
}
