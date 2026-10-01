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

    /// <summary>Reads the assembly with its .pdb the way PublicApiGenerator does; throws on a mismatch.</summary>
    private static void AssertSymbolsMatch(string assembly)
    {
        using var module = ModuleDefinition.ReadModule(assembly, new ReaderParameters { ReadSymbols = true, InMemory = true });
        Assert.True(module.HasSymbols);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
