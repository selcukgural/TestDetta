using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>Processes with temporary directories of their own keep NuGet's shared locks.</summary>
public sealed class NuGetScratchTests
{
    [Fact]
    public void The_scratch_folder_is_the_one_without_a_temporary_directory_of_its_own_and_a_given_one_is_kept()
    {
        var expected = Environment.GetEnvironmentVariable(NuGetScratch.Variable) is { Length: > 0 } set ? null : Path.Combine(Path.GetTempPath(), "NuGetScratch");
        var worker = new Dictionary<string, string> { ["TMPDIR"] = "/tmp/td1/" };

        NuGetScratch.KeepShared(worker);

        Assert.Equal(expected, worker.GetValueOrDefault(NuGetScratch.Variable));

        var named = new Dictionary<string, string> { [NuGetScratch.Variable] = "/team/scratch" };
        NuGetScratch.KeepShared(named);
        Assert.Equal("/team/scratch", named[NuGetScratch.Variable]);
    }
}
