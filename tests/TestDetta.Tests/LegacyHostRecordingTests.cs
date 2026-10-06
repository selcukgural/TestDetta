using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Recording in test hosts older than .NET 8: .NET 6 (VSTest with Test SDK 17.13, the last for it) and
/// .NET Framework 4.8 (VSTest starts it with mono outside Windows), with xUnit 2, NUnit 3 and MSTest, for
/// code under test that targets netstandard2.0 and net35 (docs/design/framework-support.md). They need a
/// dotnet with the .NET 6 runtime (and mono for .NET Framework), so they run on their own:
/// <c>DOTNET_ROOT=&lt;dir&gt; &lt;dir&gt;/dotnet test --filter Category=Legacy</c>; elsewhere each one returns at once.
/// </summary>
[Trait("Category", "Legacy")]
public sealed class LegacyHostRecordingTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public LegacyHostRecordingTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calc.cs", "namespace Calc { public static class Calculator { public static int Add(int a, int b) => a + b; } }");
        _repo.Write("src/Old/Old.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net35</TargetFramework></PropertyGroup></Project>
            """);
        _repo.Write("src/Old/Legacy.cs", "namespace Old { public static class Legacy { public static int Twice(int a) { return a * 2; } } }");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Records_xunit2_tests_on_net6()
    {
        if (!HasRuntime("Microsoft.NETCore.App 6."))
        {
            return;
        }

        AssertRecords("net6.0", """
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
            """, "using Xunit; namespace Calc.Tests { public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calc.Calculator.Add(1, 2)); } }");
    }

    [Fact]
    public void Records_nunit3_tests_on_net6()
    {
        if (!HasRuntime("Microsoft.NETCore.App 6."))
        {
            return;
        }

        AssertRecords("net6.0", """
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
            <PackageReference Include="NUnit" Version="3.14.0" />
            <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
            """, "using NUnit.Framework; namespace Calc.Tests { public class AddTests { [Test] public void Adds() => Assert.AreEqual(3, Calc.Calculator.Add(1, 2)); } }");
    }

    [Fact]
    public void Records_xunit2_tests_on_net48_with_net35_code_under_test()
    {
        if (!OperatingSystem.IsWindows() && !OnPath("mono"))
        {
            return;
        }

        AssertRecords("net48", """
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
            <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
            """, "using Xunit; namespace Calc.Tests { public class AddTests { [Fact] public void Adds() { Assert.Equal(3, Calc.Calculator.Add(1, 2)); Assert.Equal(4, Old.Legacy.Twice(2)); } } }",
            "Old.Legacy");
    }

    [Fact]
    public void Records_mstest_tests_on_net48()
    {
        if (!OperatingSystem.IsWindows() && !OnPath("mono"))
        {
            return;
        }

        AssertRecords("net48", """
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
            <PackageReference Include="MSTest.TestFramework" Version="3.6.4" />
            <PackageReference Include="MSTest.TestAdapter" Version="3.6.4" />
            <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
            """, "using Microsoft.VisualStudio.TestTools.UnitTesting; namespace Calc.Tests { [TestClass] public class AddTests { [TestMethod] public void Adds() => Assert.AreEqual(3, Calc.Calculator.Add(1, 2)); } }");
    }

    [Fact]
    public void Records_xunit2_tests_on_net462_without_netstandard_facades()
    {
        if (!OperatingSystem.IsWindows() && !OnPath("mono"))
        {
            return;
        }

        AssertRecords("net462", """
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
            <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
            """, "using Xunit; namespace Calc.Tests { public class AddTests { [Fact] public void Adds() { Assert.Equal(4, Old.Legacy.Twice(2)); } } }",
            "Old.Legacy", calc: false);
    }

    [Fact]
    public void Records_mstest1_tests_on_net48()
    {
        if (!OperatingSystem.IsWindows() && !OnPath("mono"))
        {
            return;
        }

        AssertRecords("net48", """
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
            <PackageReference Include="MSTest.TestFramework" Version="1.4.0" />
            <PackageReference Include="MSTest.TestAdapter" Version="1.4.0" />
            <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
            """, "using Microsoft.VisualStudio.TestTools.UnitTesting; namespace Calc.Tests { [TestClass] public class AddTests { [TestMethod] public void Adds() { Assert.AreEqual(3, Calc.Calculator.Add(1, 2)); } } }");
    }

    [Fact]
    public void Records_mstest3_tests_on_net6()
    {
        if (!HasRuntime("Microsoft.NETCore.App 6."))
        {
            return;
        }

        AssertRecords("net6.0", """
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
            <PackageReference Include="MSTest.TestFramework" Version="3.6.4" />
            <PackageReference Include="MSTest.TestAdapter" Version="3.6.4" />
            """, "using Microsoft.VisualStudio.TestTools.UnitTesting; namespace Calc.Tests { [TestClass] public class AddTests { [TestMethod] public void Adds() => Assert.AreEqual(3, Calc.Calculator.Add(1, 2)); } }");
    }

    /// <summary>Writes a test project for <paramref name="framework"/>, records it, and checks the class ran the code under test.</summary>
    private void AssertRecords(string framework, string packages, string tests, string? oldType = null, bool calc = true)
    {
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>{framework}</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
              <ItemGroup>
                {packages}
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
                <ProjectReference Include="../../src/Old/Old.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.cs", tests);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();
        CoverageMap map;
        try
        {
            (map, var passed) = new CoverageRecorder(NullLoggerFactory.Instance)
                .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);
            Assert.True(passed, output.ToString());
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
        }

        Assert.True(map.Classes.ContainsKey("Calc.Tests.AddTests"), $"classes: {string.Join(", ", map.Classes.Keys)}; ambient: {map.Hits.Ambient.Count}\n{output}");
        var ran = map.Classes["Calc.Tests.AddTests"].Select(id => map.Methods[id]).ToList();
        if (calc)
        {
            Assert.Contains(new CoveredMethod("Calc.Calculator", "Add"), ran);
        }
        if (oldType is not null)
        {
            Assert.Contains(new CoveredMethod(oldType, "Twice"), ran);
        }
    }

    /// <summary>True when the dotnet that runs these tests has a runtime whose line starts with <paramref name="prefix"/>.</summary>
    private static bool HasRuntime(string prefix)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(host, "--list-runtimes") { RedirectStandardOutput = true })!;
        var runtimes = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return runtimes.Split('\n').Any(l => l.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>True when <paramref name="program"/> is on the PATH.</summary>
    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, program)));
}
