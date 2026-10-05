using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// The selection reads what source generators write now from a build of the current sources
/// (docs/design/generator-output-changes.md). Slow: builds a test project that uses the logging
/// source generator.
/// </summary>
[Trait("Category", "Slow")]
public sealed class BuildGeneratedCodeReaderTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public BuildGeneratedCodeReaderTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("tests/Logs.Tests/Logs.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" /></ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Logs.Tests/Log.cs", """
            using Microsoft.Extensions.Logging;
            namespace Logs.Tests;
            public static partial class Log
            {
                [LoggerMessage(Level = LogLevel.Information, Message = "Saved {Id}")]
                public static partial void Saved(ILogger logger, int id);
            }
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void A_build_of_the_current_sources_gives_the_generated_files_of_the_test_project()
    {
        var read = Read(new GeneratedCodeBuild(Build: true));

        Assert.Null(read.BuildErrors);
        Assert.Null(read.Unavailable);
        var log = Assert.Single(read.Sources!, static s => s.Text.Contains("partial class Log", StringComparison.Ordinal));
        Assert.Equal("tests/Logs.Tests/Logs.Tests.csproj", log.Project);

        // Built now, so a reader that does not build may use it; a later edit makes it stale.
        Assert.NotNull(Read(new GeneratedCodeBuild(Build: false)).Sources);
        File.SetLastWriteTimeUtc(Path.Combine(_repo.Root, "tests/Logs.Tests/Log.cs"), DateTime.UtcNow.AddMinutes(5));
        Assert.Contains("no build of it is newer than its sources", Read(new GeneratedCodeBuild(Build: false)).Unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_build_nothing_is_read_and_the_reason_says_how_to_get_one()
    {
        var read = Read(new GeneratedCodeBuild(Build: false));

        Assert.Null(read.Sources);
        Assert.Contains("run with --build", read.Unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public void Sources_that_do_not_compile_give_the_compiler_errors()
    {
        _repo.Write("tests/Logs.Tests/Broken.cs", "namespace Logs.Tests; public class Broken { int X() => }");

        var read = Read(new GeneratedCodeBuild(Build: true));

        Assert.Null(read.Sources);
        Assert.Contains("error CS", read.BuildErrors, StringComparison.Ordinal);
    }

    private GeneratedCodeRead Read(GeneratedCodeBuild build)
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var project = graph[Paths.Normalize(Path.Combine(_repo.Root, "tests/Logs.Tests/Logs.Tests.csproj"))];
        return new BuildGeneratedCodeReader(NullLoggerFactory.Instance, build).Read(graph, _repo.Root, project, framework: null);
    }

    public void Dispose() => _repo.Dispose();
}
