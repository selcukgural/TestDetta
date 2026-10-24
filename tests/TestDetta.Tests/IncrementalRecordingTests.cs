using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// <c>td record --incremental</c> end to end (docs/design/incremental-recording.md): only the classes a commit can
/// affect run again, the others keep their entries, a reflection-found type records its project in full, and a full
/// recording checks the incremental map it replaces. Slow: builds and records several times.
/// </summary>
[Trait("Category", "Slow")]
public sealed class IncrementalRecordingTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    // No console settings from the machine.
    private readonly CliHost _host;

    public IncrementalRecordingTests()
    {
        _host = new CliHost(static _ => null);
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Lib/Calc.cs", """
            namespace Lib;
            public static class Calc
            {
                public static int Add(int a, int b) => a + b;
                public static int Mul(int a, int b) => a * b;
            }
            """);
        _repo.Write("src/Lib/Plugins.cs", """
            namespace Lib;
            public interface IPlugin { string Run(); }
            public sealed class EchoPlugin : IPlugin { public string Run() => "echo"; }
            """);
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/Tests.cs", """
            using Xunit;
            namespace Lib.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, Calc.Add(1, 2)); }
            public class MulTests { [Fact] public void Multiplies() => Assert.Equal(6, Calc.Mul(2, 3)); }
            public class ScanTests
            {
                [Fact]
                public void Every_plugin_answers() =>
                    Assert.All(
                        typeof(IPlugin).Assembly.GetTypes().Where(t => t.IsClass && typeof(IPlugin).IsAssignableFrom(t)),
                        t => Assert.NotEmpty(((IPlugin)Activator.CreateInstance(t)!).Run()));
            }
            """);
        _repo.Commit("base");
    }

    private string MapPath => CoverageMapStore.PathFor(_repo.Root, Path.Combine(_repo.Root, "tests/Lib.Tests/Lib.Tests.csproj"));

    private (int Exit, string Output) Record(params string[] extra)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(["record", "--repo", _repo.Root, .. extra], stdout, stderr, _host);
        return (exit, stdout + "\n" + stderr);
    }

    private string Head() => new GitClient(Microsoft.Extensions.Logging.Abstractions.NullLogger<GitClient>.Instance).Head(_repo.Root);

    private static HashSet<string> Ran(CoverageMap map, string testClass) =>
        map.Classes[testClass].Select(id => $"{map.Methods[id].TypeName}.{map.Methods[id].MemberName}").ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void A_body_change_rerecords_only_the_classes_that_ran_it_and_carries_the_others_over()
    {
        var (exit, output) = Record();
        Assert.True(exit == 0, output);
        var full = CoverageMapStore.Read(MapPath)!;
        var addEntry = Ran(full, "Lib.Tests.AddTests");

        _repo.Replace("src/Lib/Calc.cs", "a * b;", "b * a;");
        _repo.Commit("mul");
        (exit, output) = Record("--incremental");
        Assert.True(exit == 0, output);

        var merged = CoverageMapStore.Read(MapPath)!;
        Assert.Contains("partial", output, StringComparison.Ordinal);
        Assert.Contains("1 of 3 class(es)", output, StringComparison.Ordinal);
        Assert.Equal(Head(), merged.Commit);
        Assert.Equal(new MapLineage(full.Commit, full.Header.RecordedAt, 1), merged.Lineage);
        // Carried over under the new method ids, re-recorded, and untouched by the recording.
        Assert.Equal(addEntry, Ran(merged, "Lib.Tests.AddTests"));
        Assert.Equal(full.Timings!.ClassSeconds["Lib.Tests.AddTests"], merged.Timings!.ClassSeconds["Lib.Tests.AddTests"]);
        Assert.Contains("Lib.Calc.Mul", Ran(merged, "Lib.Tests.MulTests"));
        Assert.Contains("Lib.EchoPlugin.Run", Ran(merged, "Lib.Tests.ScanTests"));

        // A pull request after the step is answered by the merged map as by a full one.
        _repo.Replace("src/Lib/Calc.cs", "a + b;", "b + a;");
        var selection = new AnalysisPipeline(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).Run(_repo.Root, "HEAD").Tests.Single();
        Assert.Equal(["Lib.Tests.AddTests"], selection.TestClasses);
    }

    [Fact]
    public void A_commit_nothing_can_observe_moves_the_map_to_the_new_commit()
    {
        Assert.Equal(0, Record().Exit);
        var full = CoverageMapStore.Read(MapPath)!;

        _repo.Write("README.md", "docs only\n");
        _repo.Commit("docs");
        var (exit, output) = Record("--incremental");

        Assert.True(exit == 0, output);
        Assert.Contains("carry", output, StringComparison.Ordinal);
        var carried = CoverageMapStore.Read(MapPath)!;
        Assert.Equal(Head(), carried.Commit);
        Assert.Equal(full.Classes.Keys.Order(), carried.Classes.Keys.Order());
        Assert.Equal(1, carried.Lineage!.Steps);
    }

    [Fact]
    public void A_plugin_found_by_reflection_records_its_project_in_full_and_the_full_map_checks_the_incremental_one()
    {
        Assert.Equal(0, Record().Exit);
        _repo.Replace("src/Lib/Calc.cs", "a * b;", "b * a;");
        _repo.Commit("mul");
        Assert.Equal(0, Record("--incremental").Exit);
        Assert.NotNull(CoverageMapStore.Read(MapPath)!.Lineage);

        // Nothing names the new plugin; the scan finds it. Selection runs the project completely, so it is recorded in full.
        _repo.Write("src/Lib/Upper.cs", """
            namespace Lib;
            public sealed class UpperPlugin : IPlugin { public string Run() => "UPPER"; }
            """);
        _repo.Commit("plugin");
        var (exit, output) = Record("--incremental");

        Assert.True(exit == 0, output);
        Assert.Contains("full", output, StringComparison.Ordinal);
        Assert.Contains("no class ran code it lacked", output, StringComparison.Ordinal);
        var map = CoverageMapStore.Read(MapPath)!;
        Assert.Null(map.Lineage);
        Assert.Contains("Lib.UpperPlugin.Run", Ran(map, "Lib.Tests.ScanTests"));
    }

    [Fact]
    public void The_step_threshold_records_in_full()
    {
        Assert.Equal(0, Record().Exit);
        _repo.Replace("src/Lib/Calc.cs", "a * b;", "b * a;");
        _repo.Commit("mul");
        Assert.Equal(0, Record("--incremental").Exit);

        _repo.Replace("src/Lib/Calc.cs", "b * a;", "a * b;");
        _repo.Commit("mul back");
        var (exit, output) = Record("--incremental", "--full-record-every", "1");

        Assert.True(exit == 0, output);
        Assert.Contains("1 incremental step(s) since the last full map", output, StringComparison.Ordinal);
        Assert.Null(CoverageMapStore.Read(MapPath)!.Lineage);
    }

    public void Dispose() => _repo.Dispose();
}
