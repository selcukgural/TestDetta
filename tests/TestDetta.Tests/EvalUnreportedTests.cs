using System.Text.Json;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// A test project that writes no test report hides its failures; the evaluation must leave it out
/// and say so, not count it as passing. Slow: builds, records and runs a small repository.
/// </summary>
[Trait("Category", "Slow")]
[Trait("Category", "Eval")]
public sealed class EvalUnreportedTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(), "testdetta-eval-unreported-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Nunit_under_the_testing_platform_reports_through_its_own_result_file()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\nTestResults/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public static class Calculator { public static int Add(int a, int b) => a + b; public static int Neg(int a) => -a; }
            """);
        // MSTest under the testing platform writes TRX reports.
        _repo.Write("tests/Add.Tests/Add.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableMSTestRunner>true</EnableMSTestRunner><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MSTest" Version="4.4.1" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Add.Tests/Tests.cs", """
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            namespace Add.Tests;
            [TestClass] public class AddTests { [TestMethod] public void Adds() => Assert.AreEqual(3, Calc.Calculator.Add(1, 2)); }
            """);
        // NUnit under the testing platform ships no TRX extension, like NSubstitute's test projects;
        // its adapter's own result file says which classes failed.
        _repo.Write("tests/Neg.Tests/Neg.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableNUnitRunner>true</EnableNUnitRunner><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
                <PackageReference Include="NUnit" Version="4.4.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.0" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Neg.Tests/Tests.cs", """
            using NUnit.Framework;
            namespace Neg.Tests;
            public class NegTests { [Test] public void Negates() => Assert.That(Calc.Calculator.Neg(2), Is.EqualTo(-2)); }
            """);
        _repo.Commit("base");

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "5", "--seed", "1", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        Assert.Empty(run.GetProperty("unreported").EnumerateArray());

        var mutants = run.GetProperty("mutants").EnumerateArray().ToDictionary(static m => m.GetProperty("target").GetString()!);
        Assert.Equal(["Add.Tests: Add.Tests.AddTests"], mutants["Calc.Calculator.Add"].GetProperty("killed").EnumerateArray().Select(static c => c.GetString()));
        Assert.Equal(["Neg.Tests: Neg.Tests.NegTests"], mutants["Calc.Calculator.Neg"].GetProperty("killed").EnumerateArray().Select(static c => c.GetString()));
        Assert.All(mutants.Values, static m => Assert.Empty(m.GetProperty("missed").EnumerateArray()));
    }

    [Fact]
    public void Project_whose_test_host_cannot_start_is_left_out_and_named()
    {
        // Built for the other architecture, the test host cannot start: VSTest writes an empty report
        // and "Test Run Aborted", in the run without mutants too (Vogen's SnapshotTests are x64-only).
        var other = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "x64" : "arm64";
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\nTestResults/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public static class Calculator { public static int Add(int a, int b) => a + b; public static int Neg(int a) => -a; }
            """);
        foreach (var (name, platform) in new[] { ("Add", ""), ("Neg", $"<PlatformTarget>{other}</PlatformTarget>") })
        {
            _repo.Write($"tests/{name}.Tests/{name}.Tests.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>{platform}</PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                    <ProjectReference Include="../../src/Calc/Calc.csproj" />
                  </ItemGroup>
                </Project>
                """);
        }

        _repo.Write("tests/Add.Tests/Tests.cs", """
            using Xunit;
            namespace Add.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calc.Calculator.Add(1, 2)); }
            """);
        _repo.Write("tests/Neg.Tests/Tests.cs", """
            using Xunit;
            namespace Neg.Tests;
            public class NegTests { [Fact] public void Negates() => Assert.Equal(-2, Calc.Calculator.Neg(2)); }
            """);
        _repo.Commit("base");

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "5", "--seed", "1", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        Assert.Equal(["tests/Neg.Tests/Neg.Tests.csproj"], run.GetProperty("unreported").EnumerateArray().Select(static p => p.GetString()));

        // The host that never starts is not a crash the mutant caused.
        var mutants = run.GetProperty("mutants").EnumerateArray().ToDictionary(static m => m.GetProperty("target").GetString()!);
        Assert.False(mutants["Calc.Calculator.Neg"].GetProperty("caughtByCrash").GetBoolean());
        Assert.Empty(mutants["Calc.Calculator.Neg"].GetProperty("killed").EnumerateArray());
    }

    [Fact]
    public void Project_whose_test_host_crashes_without_mutants_is_left_out_and_named()
    {
        // The host dies after a test passed, in the run without mutants too (NLog.UnitTests runs out
        // of memory when run in one process): a partial report, and every later crash says nothing.
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\nTestResults/\n");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.cs", """
            namespace Calc;
            public static class Calculator { public static int Add(int a, int b) => a + b; public static int Neg(int a) => -a; }
            """);
        foreach (var name in new[] { "Add", "Neg" })
        {
            _repo.Write($"tests/{name}.Tests/{name}.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                    <ProjectReference Include="../../src/Calc/Calc.csproj" />
                  </ItemGroup>
                </Project>
                """);
        }

        _repo.Write("tests/Add.Tests/Tests.cs", """
            using Xunit;
            namespace Add.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calc.Calculator.Add(1, 2)); }
            """);
        _repo.Write("tests/Neg.Tests/Tests.cs", """
            using Xunit;
            namespace Neg.Tests;
            public class NegTests
            {
                [Fact]
                public void Negates()
                {
                    Assert.Equal(-2, Calc.Calculator.Neg(2));
                    new Thread(static () => { Thread.Sleep(1500); Environment.FailFast("host dies"); }).Start();
                }
            }

            public class SlowTests { [Fact] public void Waits() => Thread.Sleep(5000); }
            """);
        _repo.Commit("base");

        var stdout = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            EvalApp.Run(["--repo", _repo.Root, "--synthetic", "5", "--seed", "1", "--out", _output]);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "synthetic.json"))).RootElement;
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        Assert.Equal(["tests/Neg.Tests/Neg.Tests.csproj"], run.GetProperty("unreported").EnumerateArray().Select(static p => p.GetString()));
        var mutants = run.GetProperty("mutants").EnumerateArray().ToDictionary(static m => m.GetProperty("target").GetString()!);
        Assert.False(mutants["Calc.Calculator.Neg"].GetProperty("caughtByCrash").GetBoolean());
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, recursive: true);
        }
    }
}
