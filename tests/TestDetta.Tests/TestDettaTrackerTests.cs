using System.Text.Json;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Tests;

public sealed class TestDettaTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-tracker-test-" + Guid.NewGuid().ToString("N"));

    public TestDettaTrackerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Writing_twice_replaces_the_results_and_leaves_no_temporary_file()
    {
        // VSTest writes at session end and again at process exit; the second write replaces the first.
        var path = Path.Combine(_directory, "coverage-0.json");

        TestDettaTracker.WriteResults(path);
        TestDettaTracker.WriteResults(path);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("classes").ValueKind);
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void Each_process_writes_its_own_results_file()
    {
        // A testing-platform controller and its test host both load the tracker; neither may overwrite the other.
        Assert.Equal(Path.Combine("work", "coverage-2.4711.json"), TestDettaTracker.ProcessResultsPath(Path.Combine("work", "coverage-2.json"), 4711));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
