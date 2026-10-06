using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Without <c>-f</c>, a multi-targeted test project is recorded for the first framework its tests can
/// run for on this machine: .NET Framework only on Windows (NodaTime.Test lists net48 first).
/// </summary>
public sealed class RecordingFrameworkTests
{
    [Theory]
    [InlineData("net48;net8.0;net10.0", false, "net8.0")]
    [InlineData("net48;net8.0;net10.0", true, "net48")]
    [InlineData("net472;netcoreapp3.1", false, "netcoreapp3.1")]
    [InlineData("net8.0;net48", false, "net8.0")]
    [InlineData("net48;net481", false, "net48")]
    [InlineData("net10.0", false, "net10.0")]
    public void Picks_the_first_framework_that_runs_here(string frameworks, bool windows, string expected) =>
        Assert.Equal(expected, CoverageRecorder.RecordingFramework(frameworks.Split(';'), windows));

    [Fact]
    public void No_framework_gives_none() => Assert.Null(CoverageRecorder.RecordingFramework([], windows: false));
}
