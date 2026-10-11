using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A third-party library that calls repository code back and keeps the result per type, as Castle
/// DynamicProxy does with NSubstitute's proxy generation hook: only the first test class to need a
/// type runs the callback, every later one uses what the library kept.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ExternalCallbackTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _vendor = Directory.CreateTempSubdirectory("testdetta-vendor-").FullName;
    private readonly string _castle = Directory.CreateTempSubdirectory("testdetta-castle-").FullName;

    [Fact]
    public void Change_to_code_a_callback_runs_selects_every_class_that_uses_what_the_library_cached()
    {
        const string globalJson = """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""";
        // Built outside the repository and referenced as a file: third-party code TestDetta does not instrument.
        File.WriteAllText(Path.Combine(_vendor, "global.json"), globalJson);
        File.WriteAllText(Path.Combine(_vendor, "Vendor.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(_vendor, "Generator.cs"), """
            namespace Vendor;
            public interface IHook { string Decorate(string name); }
            public abstract class Plugin { public abstract string Run(); }
            public static class Generator
            {
                private static readonly Dictionary<Type, string> Cache = [];
                public static string NameFor<T>(IHook hook)
                {
                    lock (Cache)
                    {
                        if (!Cache.TryGetValue(typeof(T), out var name))
                        {
                            name = hook.Decorate(typeof(T).Name);
                            Cache[typeof(T)] = name;
                        }

                        return name;
                    }
                }
            }
            """);
        // Castle DynamicProxy's contracts, under its assembly name: interceptors run per call, a generation hook once per proxy type.
        var castle = _castle;
        File.WriteAllText(Path.Combine(castle, "global.json"), globalJson);
        File.WriteAllText(Path.Combine(castle, "Castle.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Castle.Core</AssemblyName></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(castle, "Contracts.cs"), """
            namespace Castle.DynamicProxy;
            public interface IInvocation { }
            public interface IInterceptor { void Intercept(IInvocation invocation); }
            public interface IProxyGenerationHook { bool ShouldInterceptMethod(string name); }
            """);
        BuildVendor(_vendor);
        BuildVendor(castle);
        Directory.CreateDirectory(Path.Combine(_repo.Root, "lib"));
        File.Copy(Path.Combine(_vendor, "out", "Vendor.dll"), Path.Combine(_repo.Root, "lib", "Vendor.dll"));
        File.Copy(Path.Combine(castle, "out", "Castle.Core.dll"), Path.Combine(_repo.Root, "lib", "Castle.Core.dll"));

        _repo.Write("global.json", globalJson);
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <Reference Include="Vendor"><HintPath>../../lib/Vendor.dll</HintPath></Reference>
                <Reference Include="Castle.Core"><HintPath>../../lib/Castle.Core.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);
        _repo.Write("src/App/Names.cs", """
            namespace App;
            public sealed class Widget;
            public sealed class Hook : Vendor.IHook
            {
                public string Decorate(string name) => Wrap(name);
                private string Wrap(string name) => "[" + name + "]";
            }
            public static class Names { public static string Of<T>() => Vendor.Generator.NameFor<T>(new Hook()); }
            public static class Math2 { public static int Three() => 3; }
            // Called back by the vendor: explicitly implemented, and an override of its base class.
            public sealed class QuietHook : Vendor.IHook { string Vendor.IHook.Decorate(string name) => name; }
            public sealed class Echo : Vendor.Plugin { public override string Run() => "echo"; }
            // Castle calls an interceptor for every intercepted call and keeps nothing it does; a hook once per proxy type.
            public sealed class Forwarder : Castle.DynamicProxy.IInterceptor { public void Intercept(Castle.DynamicProxy.IInvocation invocation) { } }
            public sealed class AllMethods : Castle.DynamicProxy.IProxyGenerationHook { public bool ShouldInterceptMethod(string name) => true; }
            // Not third-party contracts: the framework's and the repository's own.
            public sealed class ByLength : IComparer<string> { public int Compare(string? x, string? y) => (x?.Length ?? 0) - (y?.Length ?? 0); }
            public interface IShape { int Corners(); }
            public sealed class Square : IShape { public int Corners() => 4; }
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
            public class AlphaTests { [Fact] public void Names_widgets() => Assert.Equal("[Widget]", App.Names.Of<App.Widget>()); }
            public class BetaTests { [Fact] public void Names_widgets_too() => Assert.StartsWith("[", App.Names.Of<App.Widget>()); }
            public class PlainTests { [Fact] public void Three() => Assert.Equal(3, App.Math2.Three()); }
            // A recording opens no diff tool for a failing snapshot.
            public class UnattendedTests { [Fact] public void No_diff_tool() => Assert.Equal("true", System.Environment.GetEnvironmentVariable("DiffEngine_Disabled")); }

            // The test framework's own lifecycle: xUnit calls these per instance and keeps nothing they
            // build, and the class's or fixture's bucket already holds what they run.
            static class Prep { public static Task Run(int value) => Task.FromResult(value); }
            public sealed class SetupFixture : IAsyncLifetime
            {
                public Task InitializeAsync() => Prep.Run(1);
                public Task DisposeAsync() => Task.CompletedTask;
            }
            public class LifecycleTests(SetupFixture fixture) : IClassFixture<SetupFixture>, IAsyncLifetime
            {
                public Task InitializeAsync() => Prep.Run(2);
                public Task DisposeAsync() => Task.CompletedTask;
                [Fact] public void Has_a_fixture() => Assert.NotNull(fixture);
            }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), TextWriter.Null);
        Assert.True(passed);
        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);

        // Only the class that ran first called Decorate (and Wrap); the map credits it alone.
        var decorate = map.Methods.ToList().IndexOf(new CoveredMethod("App.Hook", "Decorate"));
        Assert.Single(map.Classes, c => c.Value.Contains(decorate));
        Assert.Contains(decorate, map.ExternalCallbacks!);
        // What the callback runs is part of what the library keeps: run-once code, like a cache fill.
        Assert.Contains(decorate, map.Initialization);
        Assert.Contains(map.Methods.ToList().IndexOf(new CoveredMethod("App.Hook", "Wrap")), map.Initialization);
        var callbacks = map.ExternalCallbacks!.Select(id => map.Methods[id]).ToHashSet();
        Assert.Contains(new CoveredMethod("App.QuietHook", "Decorate"), callbacks);
        Assert.Contains(new CoveredMethod("App.Echo", "Run"), callbacks);
        Assert.DoesNotContain(new CoveredMethod("App.Names", "Of"), callbacks);
        Assert.DoesNotContain(new CoveredMethod("App.Forwarder", "Intercept"), callbacks);
        Assert.Contains(new CoveredMethod("App.AllMethods", "ShouldInterceptMethod"), callbacks);
        Assert.DoesNotContain(new CoveredMethod("App.ByLength", "Compare"), callbacks);
        Assert.DoesNotContain(new CoveredMethod("App.Square", "Corners"), callbacks);
        Assert.DoesNotContain(new CoveredMethod("App.Tests.LifecycleTests", "InitializeAsync"), callbacks);
        Assert.DoesNotContain(new CoveredMethod("App.Tests.SetupFixture", "InitializeAsync"), callbacks);
        Assert.DoesNotContain(map.Methods.ToList().IndexOf(new CoveredMethod("App.Tests.Prep", "Run")), map.Initialization);

        _repo.Replace("src/App/Names.cs", "\"[\" + name + \"]\"", "\"<\" + name + \">\"");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.Equal(["App.Tests.AlphaTests", "App.Tests.BetaTests"], selection.TestClasses);
    }

    private static void BuildVendor(string directory)
    {
        var psi = new ProcessStartInfo("dotnet", ["build", "-c", "Release", "-o", "out", "-nologo", "-v:q"])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), "vendor build timed out");
        Assert.True(process.ExitCode == 0, output.Result + errors.Result);
    }

    public void Dispose()
    {
        _repo.Dispose();
        Directory.Delete(_vendor, recursive: true);
        Directory.Delete(_castle, recursive: true);
    }
}
