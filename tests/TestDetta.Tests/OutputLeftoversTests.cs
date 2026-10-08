using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// What a test run leaves in its output folder goes before the next run (NLog's <c>NLog.config</c>, written by a test
/// whose host a mutant crashed, failed other classes in every later run): new files and folders go, what was there
/// stays.
/// </summary>
public sealed class OutputLeftoversTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "td-leftovers-" + Guid.NewGuid().ToString("N"));

    public OutputLeftoversTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "runtimes", "osx"));
        Directory.CreateDirectory(Path.Combine(_folder, "empty"));
        File.WriteAllText(Path.Combine(_folder, "App.Tests.dll"), "dll");
        File.WriteAllText(Path.Combine(_folder, "runtimes", "osx", "native.dylib"), "native");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Files_and_folders_a_run_created_are_removed_and_what_was_there_stays()
    {
        var leftovers = OutputLeftovers.Before(_folder)!;
        File.WriteAllText(Path.Combine(_folder, "NLog.config"), "<nlog />");
        Directory.CreateDirectory(Path.Combine(_folder, "logs", "today"));
        File.WriteAllText(Path.Combine(_folder, "logs", "today", "app.log"), "line");
        File.WriteAllText(Path.Combine(_folder, "runtimes", "osx", "cache.bin"), "cache");
        File.WriteAllText(Path.Combine(_folder, "App.Tests.dll"), "changed by the run");

        var (removed, kept) = leftovers.RemoveNew();

        Assert.Equal(
            ["NLog.config", Path.Combine("logs", "today", "app.log"), Path.Combine("runtimes", "osx", "cache.bin")],
            removed.Order(StringComparer.Ordinal));
        Assert.Empty(kept);
        Assert.False(Directory.Exists(Path.Combine(_folder, "logs")));
        Assert.True(Directory.Exists(Path.Combine(_folder, "empty")));
        Assert.True(File.Exists(Path.Combine(_folder, "runtimes", "osx", "native.dylib")));
        Assert.Equal("changed by the run", File.ReadAllText(Path.Combine(_folder, "App.Tests.dll")));
    }

    [Fact]
    public void An_unknown_or_missing_folder_is_not_watched()
    {
        Assert.Null(OutputLeftovers.Before(null));
        Assert.Null(OutputLeftovers.Before(Path.Combine(_folder, "missing")));
    }
}
