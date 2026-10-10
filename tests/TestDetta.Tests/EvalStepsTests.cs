using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// Choosing the test projects to evaluate. Slow: asks MSBuild for each project's frameworks.
/// </summary>
[Trait("Category", "Slow")]
[Trait("Category", "Eval")]
public sealed class EvalStepsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public void Test_runs_open_no_diff_tool()
    {
        // Verify's DiffEngine opened Rider for every snapshot a Mapperly mutant broke.
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("tests/Snap.Tests/Snap.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Snap.Tests/EnvironmentTests.cs", """
            namespace Snap.Tests;
            public class EnvironmentTests { [Xunit.Fact] public void No_diff_tool() => Xunit.Assert.Equal("true", System.Environment.GetEnvironmentVariable("DiffEngine_Disabled")); }
            """);
        _repo.Commit("base");
        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["build", "tests/Snap.Tests", "-nologo", "-v:q"]) { WorkingDirectory = _repo.Root })!;
        Assert.True(build.WaitForExit(TimeSpan.FromMinutes(5)) && build.ExitCode == 0);

        var settings = new RunSettings(Parallelism: 1, Mutants: 1, Projects: [], new RepoCommands(Setup: null, Cleanup: null), Framework: "net10.0");
        var executor = new TestExecutor(new TestDetta.Eval.ExecutionContext(_repo.Root, settings, _repo.Root), NullLogger.Instance);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

        var outcome = executor.RunAll([.. graph.Projects.Where(static p => p.IsTestProject)], "env");

        Assert.Equal(1, outcome.Total);
        Assert.Empty(outcome.FailedByProject.Values.SelectMany(static f => f));
    }

    [Fact]
    public void A_recording_leaves_no_file_its_tests_wrote_but_keeps_what_setup_made()
    {
        // protobuf-net's BuildToolsUnitTests writes Aot/Data/Extensible.output.txt into the clone; the
        // next analysis saw it as a changed file and selected that project whole.
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n");
        _repo.Write("tests/Out.Tests/Out.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Out.Tests/OutputTests.cs", """
            namespace Out.Tests;
            public class OutputTests
            {
                [Xunit.Fact]
                public void Writes_next_to_its_sources()
                {
                    var data = System.IO.Path.Combine(System.AppContext.BaseDirectory, "../../../Data");
                    System.IO.Directory.CreateDirectory(data);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(data, "out.txt"), "written by a test");
                }
            }
            """);
        _repo.Commit("base");
        var maps = Directory.CreateTempSubdirectory("testdetta-eval-maps-").FullName;
        try
        {
            var settings = new RunSettings(Parallelism: 1, Mutants: 1, Projects: [], new RepoCommands(Setup: "echo generated > generated.txt", Cleanup: null), Framework: "net10.0");
            var executor = new TestExecutor(new TestDetta.Eval.ExecutionContext(_repo.Root, settings, maps), NullLogger.Instance);
            var workspace = new EvalWorkspace(new EvalGit(_repo.Root), _repo.Root, maps, executor, NullLoggerFactory.Instance);

            var recorded = new EvalSteps(workspace, settings).RecordAt(new GitClient(NullLogger<GitClient>.Instance).Head(_repo.Root));

            Assert.Equal(["Out.Tests.OutputTests"], Assert.Single(recorded).Value.Classes.Keys);
            Assert.False(File.Exists(Path.Combine(_repo.Root, "tests/Out.Tests/Data/out.txt")));
            Assert.True(File.Exists(Path.Combine(_repo.Root, "generated.txt")));
        }
        finally
        {
            Directory.Delete(maps, recursive: true);
        }
    }

    [Fact]
    public void Test_project_MSBuild_cannot_evaluate_is_left_out_instead_of_ending_the_evaluation()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("tests/Good.Tests/Good.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        // Like NSubstitute's old specs project: it imports a file the repository no longer has.
        _repo.Write("tests/Stale.Tests/Stale.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="../../Build/Gone.targets" />
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("base");

        var settings = new RunSettings(Parallelism: 1, Mutants: 1, Projects: [], new RepoCommands(Setup: null, Cleanup: null), Framework: "net10.0");
        var executor = new TestExecutor(new TestDetta.Eval.ExecutionContext(_repo.Root, settings, _repo.Root), NullLogger.Instance);
        var workspace = new EvalWorkspace(new EvalGit(_repo.Root), _repo.Root, Path.Combine(_repo.Root, "maps"), executor, NullLoggerFactory.Instance);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        Assert.Equal(2, graph.Projects.Count(p => p.IsTestProject));

        var projects = new EvalSteps(workspace, settings).TestProjects(graph);

        Assert.Equal("Good.Tests.csproj", Path.GetFileName(Assert.Single(projects).Path));
    }

    [Fact]
    public void Assembly_copied_from_a_NuGet_package_is_not_a_repository_assembly()
    {
        // Carter's Newtonsoft negotiator tests use the published Carter package; mutating that copy
        // made them fail although no source change can reach them.
        var root = Path.Combine(Path.GetTempPath(), "testdetta-packaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            var packages = Path.Combine(root, "packages");
            var packageLib = Path.Combine(packages, "lib", "1.0.0", "lib", "net10.0");
            var fromPackage = Path.Combine(root, "UsesPackage");
            var fromProject = Path.Combine(root, "UsesProject");
            foreach (var directory in new[] { packageLib, fromPackage, fromProject })
            {
                Directory.CreateDirectory(directory);
            }

            // Two builds with different module ids under one file name.
            File.Copy(typeof(GitClient).Assembly.Location, Path.Combine(packageLib, "Lib.dll"));
            File.Copy(typeof(GitClient).Assembly.Location, Path.Combine(fromPackage, "Lib.dll"));
            File.Copy(typeof(EvalApp).Assembly.Location, Path.Combine(fromProject, "Lib.dll"));

            var copies = EvalSteps.RepositoryAssemblies(new HashSet<string>(["Lib.dll"]), [fromPackage, fromProject], packages);

            Assert.Equal([Path.Combine(fromProject, "Lib.dll")], copies);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public void Dispose() => _repo.Dispose();
}
