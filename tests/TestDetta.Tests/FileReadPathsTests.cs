using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>How the full paths a recording saw read turn into repository paths.</summary>
public sealed class FileReadPathsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_listing_of_a_file_system_root_stands_for_nothing()
    {
        // MSBuild's tests list "/": trimmed of its slash, the path was empty and the whole recording failed.
        _repo.Write("data/rates.json", "{}");
        var paths = new FileReadPaths(_repo.Root, ["data/rates.json"]);

        Assert.Empty(paths.Resolve("/"));
        Assert.Empty(paths.Resolve("C:/"));
        Assert.Empty(paths.Resolve("?/"));
        Assert.Equal(["data/rates.json"], paths.Resolve(Path.Combine(_repo.Root, "data", "rates.json").Replace('\\', '/')));
    }
}
