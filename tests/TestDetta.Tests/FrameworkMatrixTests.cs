using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Records and selects for MSTest and NUnit projects under both dotnet test runners, with the
/// frameworks' own parallelization turned on to prove recording still runs one test at a time.
/// Slow: every case builds, records and analyzes a real project.
/// </summary>
[Trait("Category", "Slow")]
public sealed class FrameworkMatrixTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Theory]
    [InlineData("mstest", false)]
    [InlineData("mstest", true)]
    [InlineData("nunit", false)]
    [InlineData("nunit", true)]
    public void Records_each_class_separately_and_selects_only_the_affected_one(string framework, bool testingPlatform)
    {
        WriteSolution(framework, testingPlatform);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();

        CoverageMap map;
        bool passed;
        try
        {
            (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
                .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 2)), output);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
        }

        // BrokenTests fails on purpose: the map names it, so selection never trusts its coverage.
        // NUnit under the testing platform has no TRX extension; its own result file tells instead.
        Assert.False(passed, output.ToString());
        Assert.Equal(["Calc.Tests.BrokenTests"], map.FailedClasses);
        Assert.Equal(["Add"], CalculatorMembers(map, "Calc.Tests.AddTests"));
        Assert.Equal(["Mul"], CalculatorMembers(map, "Calc.Tests.MulTests"));

        // Class-level setup ([ClassInitialize], [OneTimeSetUp]) counts for its class.
        Assert.Equal(["Warm"], CalculatorMembers(map, "Calc.Tests.WarmTests"));

        // Test data ([DynamicData], [TestCaseSource]) runs before its class and counts for it.
        Assert.Equal(["Seed"], CalculatorMembers(map, "Calc.Tests.DataTests"));
        if (framework == "nunit")
        {
            // A static field as the source: the static constructor that builds it is the class's data.
            Assert.Equal(["Prime"], CalculatorMembers(map, "Calc.Tests.FieldDataTests"));
        }

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        _repo.Replace("src/Calc/Calculator.cs", "a * b", "b * a");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();
        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        // The class that failed while recording always runs, besides the one the change reaches.
        Assert.Equal(["Calc.Tests.BrokenTests", "Calc.Tests.MulTests"], selection.TestClasses);
    }

    /// <summary>Calculator members a test class ran, without constructors.</summary>
    private static string[] CalculatorMembers(CoverageMap map, string testClass) =>
    [
        .. map.Classes[testClass]
            .Select(id => map.Methods[id])
            .Where(m => m.TypeName == "Calc.Calculator" && m.MemberName != ".ctor")
            .Select(m => m.MemberName)
            .Order(),
    ];

    /// <summary>A library and a test project with Add, Mul and class-setup tests, committed on main.</summary>
    private void WriteSolution(string framework, bool testingPlatform)
    {
        _repo.Write("global.json", testingPlatform
            ? """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }"""
            : """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public class Calculator { public int Add(int a, int b) => a + b; public int Mul(int a, int b) => a * b; public int Warm() => 0; public int Seed() => 5; public int Prime() => 7; }
            """);

        if (framework == "mstest")
        {
            var runner = testingPlatform ? "<EnableMSTestRunner>true</EnableMSTestRunner><OutputType>Exe</OutputType>" : "";
            _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>{runner}</PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="MSTest" Version="4.4.1" />
                    <ProjectReference Include="../../src/Calc/Calc.csproj" />
                  </ItemGroup>
                </Project>
                """);
            _repo.Write("tests/Calc.Tests/Tests.cs", """
                using Microsoft.VisualStudio.TestTools.UnitTesting;
                [assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
                namespace Calc.Tests;
                [TestClass] public class AddTests { [TestMethod] public void Adds() => Assert.AreEqual(3, new Calculator().Add(1, 2)); }
                [TestClass] public class MulTests { [TestMethod] public void Muls() => Assert.AreEqual(6, new Calculator().Mul(2, 3)); }
                [TestClass] public class WarmTests { [ClassInitialize] public static void Init(TestContext _) => new Calculator().Warm(); [TestMethod] public void Warm() { } }
                [TestClass] public class BrokenTests { [TestMethod] public void Fails() => Assert.AreEqual(3, new Calculator().Add(1, 1)); }
                [TestClass] public class DataTests { public static IEnumerable<object[]> Rows => [[new Calculator().Seed()]]; [TestMethod, DynamicData(nameof(Rows))] public void Seeds(int n) => Assert.AreEqual(5, n); }
                """);
        }
        else
        {
            var runner = testingPlatform ? "<EnableNUnitRunner>true</EnableNUnitRunner><OutputType>Exe</OutputType>" : "";
            _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>{runner}</PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
                    <PackageReference Include="NUnit" Version="4.4.0" />
                    <PackageReference Include="NUnit3TestAdapter" Version="6.3.0" />
                    <ProjectReference Include="../../src/Calc/Calc.csproj" />
                  </ItemGroup>
                </Project>
                """);
            _repo.Write("tests/Calc.Tests/Tests.cs", """
                using NUnit.Framework;
                [assembly: Parallelizable(ParallelScope.Fixtures)]
                namespace Calc.Tests;
                public class AddTests { [Test] public void Adds() => Assert.That(new Calculator().Add(1, 2), Is.EqualTo(3)); }
                public class MulTests { [Test] public void Muls() => Assert.That(new Calculator().Mul(2, 3), Is.EqualTo(6)); }
                public class WarmTests { [OneTimeSetUp] public void Init() => new Calculator().Warm(); [Test] public void Warm() { } }
                public class BrokenTests { [Test] public void Fails() => Assert.That(new Calculator().Add(1, 1), Is.EqualTo(3)); }
                public class DataTests { static IEnumerable<int> Rows() { yield return new Calculator().Seed(); } [TestCaseSource(nameof(Rows))] public void Seeds(int n) => Assert.That(n, Is.EqualTo(5)); }
                public class FieldDataTests { static readonly int[] Primes = [new Calculator().Prime()]; [TestCaseSource(nameof(Primes))] public void Prime(int n) => Assert.That(n, Is.EqualTo(7)); }
                """);
        }

        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();
}
