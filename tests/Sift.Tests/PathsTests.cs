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
}
