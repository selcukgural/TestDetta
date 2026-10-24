using TestDetta.Core;

namespace TestDetta.Tests;

public sealed class PathsTests
{
    [Theory]
    [InlineData("/repo/src/App", "/repo/src/App", true)]
    [InlineData("/repo/src/App/A.cs", "/repo/src/App", true)]
    [InlineData("/repo/src/AppTests/A.cs", "/repo/src/App", false)]
    [InlineData("/repo/src", "/repo/src/App", false)]
    public void IsUnder_requires_a_separator_after_the_directory(string path, string directory, bool expected)
    {
        Assert.Equal(expected, Paths.IsUnder(path, directory));
    }

    [Fact]
    public void ResolveLinks_replaces_every_link_along_the_path_and_keeps_missing_parts()
    {
        var real = Path.Combine(Path.GetTempPath(), "testdetta-real-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "testdetta-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(real, "src"));
        try
        {
            Directory.CreateSymbolicLink(link, real);

            // The temporary directory itself may sit behind a link (macOS: /var -> /private/var).
            var expected = Paths.ResolveLinks(real) + "/src/Missing.cs";
            Assert.Equal(expected, Paths.ResolveLinks(Path.Combine(link, "src", "Missing.cs")));
            Assert.Equal(Paths.ResolveLinks(real), Paths.ResolveLinks(Paths.ResolveLinks(real)));
        }
        finally
        {
            Directory.Delete(link);
            Paths.DeleteTree(real);
        }
    }

    [Fact]
    public void DeleteTree_removes_read_only_files_as_git_writes_its_objects()
    {
        var root = Path.Combine(Path.GetTempPath(), "testdetta-tree-" + Guid.NewGuid().ToString("N"));
        var objects = Path.Combine(root, ".git", "objects", "11");
        Directory.CreateDirectory(objects);
        var blob = Path.Combine(objects, "9ccbabf5097c452fc3d129f91a35b707fd4d");
        File.WriteAllText(blob, "blob");
        File.SetAttributes(blob, FileAttributes.ReadOnly);

        Paths.DeleteTree(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void DeleteTree_leaves_the_target_of_a_link_inside_the_tree_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "testdetta-tree-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "testdetta-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var kept = Path.Combine(outside, "kept.txt");
        File.WriteAllText(kept, "kept");
        File.SetAttributes(kept, FileAttributes.ReadOnly);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

            Paths.DeleteTree(root);

            Assert.False(Directory.Exists(root));
            Assert.True(File.Exists(kept));
            Assert.True(File.GetAttributes(kept).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            Paths.DeleteTree(outside);
        }
    }

    [Fact]
    public void DeleteTree_of_a_missing_directory_throws_as_Directory_Delete_does()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Paths.DeleteTree(Path.Combine(Path.GetTempPath(), "testdetta-missing-" + Guid.NewGuid().ToString("N"))));
    }
}
