using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
                <Reference Include="Vendor" HintPath="../../lib/Vendor.dll" />
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

            // Reads a file through a library no project of the repository builds, as a snapshot library does.
            public class VendorReadTests
            {
                [Fact]
                public void Reads()
                {
                    var root = AppContext.BaseDirectory;
                    while (!Directory.Exists(Path.Combine(root, "testdata"))) root = Path.GetDirectoryName(root)!;
                    Assert.Equal("# Expected", Vendor.Snapshots.Read(Path.Combine(root, "tests", "Rates.Tests", "Expected.md")).Trim());
                }
            }
            """);
        _repo.Write("tests/Rates.Tests/Expected.md", "# Expected\n");
        WriteVendorLibrary(Path.Combine(_repo.Root, "lib", "Vendor.dll"));
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

        // A third-party library's read shows under the class that ran it, and the map says it saw every library's reads.
        Assert.Contains("tests/Rates.Tests/Expected.md", reads.Classes["Rates.Tests.VendorReadTests"]);
        Assert.Equal([], map.FileReadsUnseen);
        Assert.True(map.KnowsAllFileReads);

        // Only repository paths; a probe in the build output for a file that is not there stands for nothing.
        Assert.All(reads.Classes.Values.SelectMany(static p => p), static p => Assert.False(Path.IsPathRooted(p), p));

        var stored = CoverageMapStore.Read(WriteAndPath(map));
        Assert.Equal(reads.Classes["Rates.Tests.OutputCopyTests"], stored!.FileReads!.Classes["Rates.Tests.OutputCopyTests"]);
        Assert.Equal([], stored.FileReadsUnseen);

        // The library is put back as it was.
        var library = Path.Combine(_repo.Root, "tests", "Rates.Tests", "bin", "Debug", "net10.0", "Vendor.dll");
        Assert.Equal(File.ReadAllBytes(Path.Combine(_repo.Root, "lib", "Vendor.dll")), File.ReadAllBytes(library));
    }

    /// <summary>Compiles a small library that reads a file, standing in for a package such as a snapshot library.</summary>
    private static void WriteVendorLibrary(string path)
    {
        // Against the reference pack, as a package is built: the test project compiles against it in turn.
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        var referencePack = Directory.GetDirectories(Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref"))
            .Select(static d => Path.Combine(d, "ref", "net10.0", "System.Runtime.dll"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .Last();
        var compilation = CSharpCompilation.Create(
            "Vendor",
            [CSharpSyntaxTree.ParseText("namespace Vendor; public static class Snapshots { public static string Read(string path) => System.IO.File.ReadAllText(path); }")],
            [MetadataReference.CreateFromFile(referencePack)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var pe = File.Create(path);
        var emitted = compilation.Emit(pe);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
    }

    private string WriteAndPath(CoverageMap map)
    {
        var path = Path.Combine(_repo.Root, ".testdetta", "roundtrip.json");
        CoverageMapStore.Write(path, map);
        return path;
    }
}
