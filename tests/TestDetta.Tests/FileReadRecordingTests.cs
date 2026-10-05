using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Recording keeps which files each test class read by a path (docs/design/runtime-file-reads.md):
/// through the repository's own code, as repository paths, a copy in the build output standing for the
/// file it was copied from. Slow (it runs dotnet build and test).
/// </summary>
[Trait("Category", "Slow")]
public sealed class FileReadRecordingTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public FileReadRecordingTests()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Rates/Rates.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        _repo.Write("src/Rates/RateTable.cs", """
            namespace Rates;
            public static class RateTable
            {
                public static string Load(string path) => File.ReadAllText(path).Trim();
                public static int Count(string directory) => Directory.GetFiles(directory).Length;
            }
            """);
        _repo.Write("testdata/rates.txt", "42\n");
        _repo.Write("tests/Rates.Tests/Rates.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../../src/Rates/Rates.csproj" />
                <None Update="Data/**" Link="Data/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Rates.Tests/Data/euro.txt", "7\n");
        _repo.Write("tests/Rates.Tests/Tests.cs", """
            using Xunit;
            namespace Rates.Tests;

            // Reads a repository file by a path built in code, through the library.
            public class SourceFileTests
            {
                [Fact]
                public void Reads()
                {
                    var root = AppContext.BaseDirectory;
                    while (!File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root)!;
                    Assert.Equal("42", RateTable.Load(Path.Combine(root, "testdata", "rates.txt")));
                }
            }

            // Reads the copy in the build output, and lists the output's data folder.
            public class OutputCopyTests
            {
                [Fact] public void Reads() => Assert.Equal("7", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "euro.txt")).Trim());
                [Fact] public void Lists() => Assert.Equal(1, RateTable.Count(Path.Combine(AppContext.BaseDirectory, "Data")));
            }

            public class NoFileTests { [Fact] public void Adds() => Assert.Equal(2, 1 + 1); }
            """);
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Records_the_repository_files_each_class_read()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(p => p.IsTestProject);
        var output = new StringWriter();

        CoverageMap map;
        bool passed;
        try
        {
            (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
                .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions()), output);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n{output}", ex);
        }

        Assert.True(passed, output.ToString());
        var reads = Assert.IsType<FileReads>(map.FileReads);
        // The walk up for global.json looked in the repository's folders too; those probes are reads as well.
        Assert.Contains("testdata/rates.txt", reads.Classes["Rates.Tests.SourceFileTests"]);
        Assert.Contains("global.json", reads.Classes["Rates.Tests.SourceFileTests"]);

        // The output's copy and the listed output folder stand for the repository file and its folder.
        Assert.Equal(["tests/Rates.Tests/Data/", "tests/Rates.Tests/Data/euro.txt"], reads.Classes["Rates.Tests.OutputCopyTests"]);
        Assert.False(reads.Classes.ContainsKey("Rates.Tests.NoFileTests"));

        // Only repository paths; a probe in the build output for a file that is not there stands for nothing.
        Assert.All(reads.Classes.Values.SelectMany(static p => p), static p => Assert.False(Path.IsPathRooted(p), p));

        var stored = CoverageMapStore.Read(WriteAndPath(map));
        Assert.Equal(reads.Classes["Rates.Tests.OutputCopyTests"], stored!.FileReads!.Classes["Rates.Tests.OutputCopyTests"]);
    }

    private string WriteAndPath(CoverageMap map)
    {
        var path = Path.Combine(_repo.Root, ".testdetta", "roundtrip.json");
        CoverageMapStore.Write(path, map);
        return path;
    }
}
