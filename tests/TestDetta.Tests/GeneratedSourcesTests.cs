using System.Diagnostics;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Source generator output exists only in the compiled assembly's PDB; recording reads it from
/// there. Slow: builds a project that uses the logging source generator.
/// </summary>
[Trait("Category", "Slow")]
public sealed class GeneratedSourcesTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Theory]
    [InlineData("portable")]
    [InlineData("embedded")]
    public void Generated_code_is_read_from_the_pdb(string debugType)
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write("src/Logs/Logs.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><DebugType>{debugType}</DebugType></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Logs/Log.cs", """
            using Microsoft.Extensions.Logging;
            namespace Logs;
            public static partial class Log
            {
                [LoggerMessage(Level = LogLevel.Information, Message = "Saved {Id}")]
                public static partial void Saved(ILogger logger, int id);
            }
            """);

        using var build = Process.Start(new ProcessStartInfo("dotnet", ["build", "src/Logs/Logs.csproj", "-c", "Debug", "-v", "q"])
        {
            WorkingDirectory = _repo.Root,
            RedirectStandardOutput = true,
        })!;
        var buildOutput = build.StandardOutput.ReadToEnd();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, buildOutput);

        var sources = GeneratedSources.Read(Path.Combine(_repo.Root, "src/Logs/bin/Debug/net10.0/Logs.dll"));
        var types = GeneratedCode.Collect("src/Logs/Logs.csproj", sources);

        // The generated body of Saved calls into the logging API; the hand-written file is not included.
        var log = Assert.Single(types, t => t.TypeName == "Logs.Log");
        Assert.Contains("ILogger", log.References);
        Assert.DoesNotContain(sources, s => s.Contains("public static partial void Saved(ILogger logger, int id);", StringComparison.Ordinal));
    }

    public void Dispose() => _repo.Dispose();
}
