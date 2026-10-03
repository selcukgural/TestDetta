using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>
/// A third-party library that calls repository code back and keeps the result per type, as Castle
/// DynamicProxy does with NSubstitute's proxy generation hook: only the first test class to need a
/// type runs the callback, every later one uses what the library kept.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ExternalCallbackTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _vendor = Directory.CreateTempSubdirectory("sift-vendor-").FullName;

    [Fact]
    public void Change_to_code_a_callback_runs_selects_every_class_that_uses_what_the_library_cached()
    {
        const string globalJson = """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""";
        // Built outside the repository and referenced as a file: third-party code Sift does not instrument.
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
        BuildVendor();
        Directory.CreateDirectory(Path.Combine(_repo.Root, "lib"));
        File.Copy(Path.Combine(_vendor, "out", "Vendor.dll"), Path.Combine(_repo.Root, "lib", "Vendor.dll"));

        _repo.Write("global.json", globalJson);
        _repo.Write(".gitignore", "bin/\nobj/\n.sift/\n");
        _repo.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><Reference Include="Vendor"><HintPath>../../lib/Vendor.dll</HintPath></Reference></ItemGroup>
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
        Assert.DoesNotContain(new CoveredMethod("App.ByLength", "Compare"), callbacks);
        Assert.DoesNotContain(new CoveredMethod("App.Square", "Corners"), callbacks);

        _repo.Replace("src/App/Names.cs", "\"[\" + name + \"]\"", "\"<\" + name + \">\"");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.Equal(["App.Tests.AlphaTests", "App.Tests.BetaTests"], selection.TestClasses);
    }

    private void BuildVendor()
    {
        var psi = new ProcessStartInfo("dotnet", ["build", "-c", "Release", "-o", "out", "-nologo", "-v:q"])
        {
            WorkingDirectory = _vendor,
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
    }
}
