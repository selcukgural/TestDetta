using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>Coverage maps kept outside the repository, one folder per commit, for CI systems other than GitHub Actions.</summary>
public sealed class MapDirectoryTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly string _maps = Directory.CreateTempSubdirectory("testdetta-maps-").FullName;
    private readonly GitClient _git = new(NullLogger<GitClient>.Instance);

    public MapDirectoryTests()
    {
        _repo.Write("a.txt", "0");
        _repo.Commit("c0");
    }

    private string Head() => _git.Head(_repo.Root);

    private string CommitOnMain(string content)
    {
        _repo.Write("a.txt", content);
        _repo.Commit(content);
        return Head();
    }

    private void SaveMap(string commit, string project, string content)
    {
        var source = Path.Combine(_maps, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, project + ".json"), content);
        MapDirectory.Save(_maps, commit, [Path.Combine(source, project + ".json")], NullLogger.Instance);
        Directory.Delete(source, recursive: true);
    }

    private string Restored(string project) => File.ReadAllText(Path.Combine(_repo.Root, CoverageMapStore.Directory, project + ".json"));

    [Fact]
    public void The_nearest_map_at_or_before_the_merge_base_is_restored_never_a_newer_one()
    {
        var c1 = CommitOnMain("1");
        var c2 = CommitOnMain("2");
        SaveMap(c1, "App.Tests", "map of c1");
        // A pull request branches from c2; main moves on and records c3.
        _repo.Git("checkout", "-q", "-b", "feature");
        _repo.Write("b.txt", "feature");
        _repo.Commit("feature");
        _repo.Git("checkout", "-q", "main");
        var c3 = CommitOnMain("3");
        SaveMap(c3, "App.Tests", "map of c3");
        _repo.Git("checkout", "-q", "feature");

        var used = MapDirectory.Restore(_maps, _repo.Root, "main", 50, _git, NullLogger.Instance);

        Assert.Equal(c1, used);
        Assert.Equal("map of c1", Restored("App.Tests"));
        Assert.NotEqual(c2, used);
    }

    [Fact]
    public void Maps_older_than_the_age_limit_or_a_missing_folder_give_none()
    {
        var old = Head();
        SaveMap(old, "App.Tests", "old");
        CommitOnMain("1");
        CommitOnMain("2");

        Assert.Null(MapDirectory.Restore(_maps, _repo.Root, "main", 1, _git, NullLogger.Instance));
        Assert.Equal(old, MapDirectory.Restore(_maps, _repo.Root, "main", 2, _git, NullLogger.Instance));
        Assert.Null(MapDirectory.Restore(Path.Combine(_maps, "missing"), _repo.Root, "main", 50, _git, NullLogger.Instance));
    }

    [Fact]
    public void A_restore_replaces_every_local_map_and_a_save_replaces_its_folder()
    {
        var head = Head();
        Directory.CreateDirectory(Path.Combine(_repo.Root, CoverageMapStore.Directory));
        File.WriteAllText(Path.Combine(_repo.Root, CoverageMapStore.Directory, "Stale.Tests.json"), "stale");
        SaveMap(head, "App.Tests", "first");
        SaveMap(head, "App.Tests", "second");

        MapDirectory.Restore(_maps, _repo.Root, "main", 50, _git, NullLogger.Instance);

        Assert.Equal("second", Restored("App.Tests"));
        Assert.False(File.Exists(Path.Combine(_repo.Root, CoverageMapStore.Directory, "Stale.Tests.json")));
        Assert.Equal([head], Directory.GetDirectories(_maps).Select(Path.GetFileName));
    }

    [Fact]
    public void Maps_dir_is_an_option_of_every_command()
    {
        foreach (var command in (string[])["record", "affected", "test", "audit"])
        {
            Assert.True(CliOptions.TryParse([command, "--maps-dir", "/maps"], "/cwd", out var options, out var error), error);
            Assert.Equal("/maps", options.Analysis.MapsDir);
        }
    }

    public void Dispose()
    {
        _repo.Dispose();
        Directory.Delete(_maps, recursive: true);
    }
}
