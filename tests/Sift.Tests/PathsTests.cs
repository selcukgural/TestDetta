using Sift.Core;

namespace Sift.Tests;

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
        var real = Path.Combine(Path.GetTempPath(), "sift-real-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "sift-link-" + Guid.NewGuid().ToString("N"));
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
            Directory.Delete(real, recursive: true);
        }
    }
}
