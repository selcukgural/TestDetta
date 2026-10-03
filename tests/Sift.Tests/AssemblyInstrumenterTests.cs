using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

public sealed class AssemblyInstrumenterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sift-instrument-" + Guid.NewGuid().ToString("N"));

    public AssemblyInstrumenterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Failure_halfway_restores_the_assemblies_already_rewritten()
    {
        var good = Copy(typeof(GitClient).Assembly.Location);
        var broken = Path.Combine(_directory, "Broken.dll");
        File.WriteAllText(broken, "not an assembly");
        var original = File.ReadAllBytes(good);

        var instrumenter = new AssemblyInstrumenter(NullLogger.Instance);

        Assert.ThrowsAny<Exception>(() =>
            instrumenter.Instrument([good, broken], new HashSet<string>(), Path.Combine(_directory, "backup")));
        Assert.Equal(original, File.ReadAllBytes(good));
    }

    [Fact]
    public void Instrumenting_and_restoring_round_trips_the_original_bytes()
    {
        var assembly = Copy(typeof(GitClient).Assembly.Location);
        var original = File.ReadAllBytes(assembly);
        var instrumenter = new AssemblyInstrumenter(NullLogger.Instance);

        var result = instrumenter.Instrument([assembly], new HashSet<string>(), Path.Combine(_directory, "backup"));
        Assert.NotEqual(original, File.ReadAllBytes(assembly));
        Assert.NotEmpty(result.Methods);

        instrumenter.Restore(result);
        Assert.Equal(original, File.ReadAllBytes(assembly));
    }

    [Fact]
    public void Instrumenting_adds_no_assembly_reference_but_the_tracker()
    {
        // A reference to the instrumenting process's own core library (System.Private.CoreLib 10.0)
        // makes Roslyn refuse the assembly as a metadata reference for an older target (CS1705), as
        // protobuf-net's BuildTools tests do when they compile code against it.
        var assembly = Copy(typeof(GitClient).Assembly.Location);
        var before = References(assembly);

        new AssemblyInstrumenter(NullLogger.Instance).Instrument([assembly], new HashSet<string>(), Path.Combine(_directory, "backup"));

        Assert.Equal([.. before.Append("Sift.Coverage.Runtime").Order(StringComparer.Ordinal)], References(assembly));
    }

    /// <summary>Names and versions of the assemblies <paramref name="path"/> references, sorted.</summary>
    private static List<string> References(string path)
    {
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(path);
        return [.. module.AssemblyReferences.Select(static r => r.Name == "Sift.Coverage.Runtime" ? r.Name : $"{r.Name} {r.Version}").Order(StringComparer.Ordinal)];
    }

    private string Copy(string path)
    {
        var target = Path.Combine(_directory, Path.GetFileName(path));
        File.Copy(path, target);
        return target;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
