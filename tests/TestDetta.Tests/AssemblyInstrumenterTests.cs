using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

public sealed class AssemblyInstrumenterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-instrument-" + Guid.NewGuid().ToString("N"));

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
    public void Instrumenting_adds_the_tracker_and_the_test_hosts_core_library_only()
    {
        // The library targets netstandard2.0, the "test assembly" net10.0. A core library reference at
        // the instrumenting process's version, or at any version but the test host's, makes Roslyn
        // report the assembly when tests compile against it (CS1705, CS1701; protobuf-net's BuildTools
        // tests). One at the test host's version is what Microsoft's coverage profiler needs (Polly).
        var library = Copy(Path.Combine(AppContext.BaseDirectory, "Mono.Cecil.dll"));
        var tests = Copy(typeof(GitClient).Assembly.Location);
        var (libraryBefore, testsBefore) = (References(library), References(tests));

        new AssemblyInstrumenter(NullLogger.Instance).Instrument([library, tests], new HashSet<string>([tests]), Path.Combine(_directory, "backup"));

        Assert.Equal([.. libraryBefore.Append("TestDetta.Coverage.Runtime").Append("System.Private.CoreLib 10.0.0.0").Order(StringComparer.Ordinal)], References(library));
        Assert.Equal([.. testsBefore.Append("TestDetta.Coverage.Runtime").Append("System.Private.CoreLib 10.0.0.0").Order(StringComparer.Ordinal)], References(tests));
    }

    [Fact]
    public void Without_a_test_assembly_no_core_library_is_added()
    {
        var library = Copy(Path.Combine(AppContext.BaseDirectory, "Mono.Cecil.dll"));
        var before = References(library);

        new AssemblyInstrumenter(NullLogger.Instance).Instrument([library], new HashSet<string>(), Path.Combine(_directory, "backup"));

        Assert.Equal([.. before.Append("TestDetta.Coverage.Runtime").Order(StringComparer.Ordinal)], References(library));
    }

    [Fact]
    public void Third_party_assemblies_get_read_wrappers_only_and_are_restored()
    {
        // Mono.Cecil opens files by path; Logging.Abstractions reads none; a native library is no assembly.
        var reader = Copy(Path.Combine(AppContext.BaseDirectory, "Mono.Cecil.dll"));
        var quiet = Copy(Path.Combine(AppContext.BaseDirectory, "Microsoft.Extensions.Logging.Abstractions.dll"));
        var native = Path.Combine(_directory, "libnative.dll");
        File.WriteAllText(native, "not an assembly");
        var originals = new[] { reader, quiet }.ToDictionary(static f => f, File.ReadAllBytes);
        var instrumenter = new AssemblyInstrumenter(NullLogger.Instance);

        var result = instrumenter.Instrument([], new HashSet<string>(), Path.Combine(_directory, "backup"), [reader, quiet, native]);

        Assert.Equal([], result.ReadsUnseen);
        Assert.Empty(result.Methods);
        using (var module = Mono.Cecil.ModuleDefinition.ReadModule(reader))
        {
            Assert.Contains(module.Types, static t => t.Name == "<TestDettaFileReads>");
            Assert.DoesNotContain(
                module.GetTypes().SelectMany(static t => t.Methods).Where(static m => m.HasBody).SelectMany(static m => m.Body.Instructions),
                static i => i.Operand is Mono.Cecil.MethodReference { Name: "Hit" });
        }

        Assert.Equal(originals[quiet], File.ReadAllBytes(quiet));
        Assert.DoesNotContain(quiet, result.Backups.Keys);

        instrumenter.Restore(result);
        Assert.Equal(originals[reader], File.ReadAllBytes(reader));
    }

    [Fact]
    public void Third_party_assemblies_are_the_outputs_dlls_but_the_repositorys_and_the_test_frameworks_and_platforms()
    {
        foreach (var name in (string[])["App.dll", "App.Tests.dll", "Tool.dll", "Verify.dll", "testhost.dll", "Microsoft.TestPlatform.CoreUtilities.dll",
                     "xunit.runner.visualstudio.testadapter.dll", "xunit.v3.mtp-v2.dll", "nunit.framework.dll", "Microsoft.ApplicationInsights.dll", "TestDetta.Coverage.Runtime.dll", "de/Verify.resources.dll", "runtimes/win/lib/net8.0/Snap.dll"])
        {
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        var found = ThirdPartyAssemblies.Find(_directory, ["App.dll", "App.Tests.dll", "Tool.dll"]);

        Assert.Equal(
            [Path.Combine(_directory, "Verify.dll"), Path.Combine(_directory, "runtimes/win/lib/net8.0/Snap.dll")],
            found.Order(StringComparer.Ordinal));
    }

    /// <summary>Names and versions of the assemblies <paramref name="path"/> references, sorted.</summary>
    private static List<string> References(string path)
    {
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(path);
        return [.. module.AssemblyReferences.Select(static r => r.Name == "TestDetta.Coverage.Runtime" ? r.Name : $"{r.Name} {r.Version}").Order(StringComparer.Ordinal)];
    }

    private string Copy(string path)
    {
        var target = Path.Combine(_directory, Path.GetFileName(path));
        File.Copy(path, target);
        return target;
    }

    public void Dispose() => Paths.DeleteTree(_directory);
}
