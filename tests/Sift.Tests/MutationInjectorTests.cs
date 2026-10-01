using Mono.Cecil;
using Sift.Core;
using Sift.Eval;

namespace Sift.Tests;

/// <summary>
/// A mutant must leave the assembly's symbols matching it: tools that read the .pdb next to an
/// assembly (PublicApiGenerator in Serilog's approval test) otherwise fail for every mutant.
/// </summary>
public sealed class MutationInjectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sift-mutate-" + Guid.NewGuid().ToString("N"));

    public MutationInjectorTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Mutant_lift_and_restore_keep_the_symbols_matching()
    {
        var source = typeof(GitClient).Assembly.Location;
        var assembly = Path.Combine(_directory, Path.GetFileName(source));
        var pdb = Path.ChangeExtension(assembly, ".pdb");
        File.Copy(source, assembly);
        File.Copy(Path.ChangeExtension(source, ".pdb"), pdb);
        var originalPdb = File.ReadAllBytes(pdb);

        using (var mutation = MutationInjector.Inject([assembly], new CoveredMethod("Sift.Core.GitClient", "Head"), Path.Combine(_directory, "backup")))
        {
            Assert.Equal(1, mutation.MutatedMethods);
            AssertSymbolsMatch(assembly);

            using (mutation.Lift())
            {
                AssertSymbolsMatch(assembly);
                Assert.Equal(originalPdb, File.ReadAllBytes(pdb));
            }

            AssertSymbolsMatch(assembly);
        }

        AssertSymbolsMatch(assembly);
        Assert.Equal(originalPdb, File.ReadAllBytes(pdb));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Mutant_is_written_when_a_constant_has_a_type_from_a_shared_framework()
    {
        // A default parameter value of an ASP.NET Core enum: writing it back resolves the enum's
        // assembly, which lives in the shared framework, not in the output folder (FastEndpoints).
        var project = Path.Combine(_directory, "Web");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(_directory, "global.json"), """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        File.WriteAllText(Path.Combine(project, "Web.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "Cache.cs"), """
            using Microsoft.AspNetCore.Mvc;
            namespace Web;
            public static class Cache { public static int Where(ResponseCacheLocation location = ResponseCacheLocation.Client) => (int)location; }
            """);
        var output = Path.Combine(_directory, "out");
        using (var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["build", project, "-o", output, "-nologo", "-v", "q"]) { RedirectStandardOutput = true })!)
        {
            build.StandardOutput.ReadToEnd();
            build.WaitForExit();
            Assert.Equal(0, build.ExitCode);
        }

        using var mutation = MutationInjector.Inject([Path.Combine(output, "Web.dll")], new CoveredMethod("Web.Cache", "Where"), Path.Combine(_directory, "backup"));

        Assert.Equal(1, mutation.MutatedMethods);
    }

    /// <summary>Reads the assembly with its .pdb the way PublicApiGenerator does; throws on a mismatch.</summary>
    private static void AssertSymbolsMatch(string assembly)
    {
        using var module = ModuleDefinition.ReadModule(assembly, new ReaderParameters { ReadSymbols = true, InMemory = true });
        Assert.True(module.HasSymbols);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
