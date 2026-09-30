using System.Text.Json;
using Sift.Coverage.Runtime;

namespace Sift.Tests;

public sealed class SiftTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sift-tracker-test-" + Guid.NewGuid().ToString("N"));

    public SiftTrackerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Writing_twice_replaces_the_results_and_leaves_no_temporary_file()
    {
        // VSTest writes at session end and again at process exit; the second write replaces the first.
        var path = Path.Combine(_directory, "coverage-0.json");

        SiftTracker.WriteResults(path);
        SiftTracker.WriteResults(path);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("classes").ValueKind);
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
