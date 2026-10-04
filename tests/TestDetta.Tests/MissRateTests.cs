using TestDetta.Eval;

namespace TestDetta.Tests;

public sealed class MissRateTests
{
    [Theory]
    // No misses: 1 - 0.05^(1/n), close to the "rule of three" 3/n.
    [InlineData(0, 100, 0.0295)]
    [InlineData(0, 1000, 0.0030)]
    // Clopper-Pearson reference values.
    [InlineData(1, 100, 0.0466)]
    [InlineData(5, 1000, 0.0105)]
    public void Upper_bound_matches_the_exact_binomial_bound(int misses, int trials, double expected) =>
        Assert.Equal(expected, MissRate.UpperBound(misses, trials), precision: 3);

    [Fact]
    public void Without_trials_nothing_is_known() => Assert.Equal(1, MissRate.UpperBound(0, 0));
}
