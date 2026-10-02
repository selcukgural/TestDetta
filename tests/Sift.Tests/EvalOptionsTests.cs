using Sift.Eval;

namespace Sift.Tests;

/// <summary>Options of the evaluation tool.</summary>
public sealed class EvalOptionsTests
{
    [Fact]
    public void Schema_is_on_unless_turned_off_and_the_flag_takes_no_value()
    {
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--synthetic", "3"], out var defaults, out _));
        Assert.True(defaults.Run.UseSchema);

        Assert.True(EvalOptions.TryParse(["--repo", "r", "--no-schema", "--synthetic", "3"], out var options, out var error), error);
        Assert.False(options.Run.UseSchema);
        Assert.Equal(3, options.Target.Synthetic);
    }
}
