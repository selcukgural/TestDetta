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

    [Fact]
    public void Analysis_timeout_takes_a_positive_time_span()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), EvalOptions.TryParse(["--repo", "r"], out var defaults, out _) ? defaults.Run.AnalysisTimeout : default);
        Assert.True(EvalOptions.TryParse(["--repo", "r", "--analysis-timeout", "00:02:30"], out var options, out _));
        Assert.Equal(TimeSpan.FromSeconds(150), options.Run.AnalysisTimeout);
        Assert.False(EvalOptions.TryParse(["--repo", "r", "--analysis-timeout", "0"], out _, out _));
    }
}
