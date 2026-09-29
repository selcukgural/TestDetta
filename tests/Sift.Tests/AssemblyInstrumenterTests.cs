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

    private string Copy(string path)
    {
        var target = Path.Combine(_directory, Path.GetFileName(path));
        File.Copy(path, target);
        return target;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
