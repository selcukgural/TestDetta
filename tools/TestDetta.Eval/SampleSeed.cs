using System.Globalization;
using System.Security.Cryptography;

namespace TestDetta.Eval;

/// <summary>
/// The seed of the synthetic sample (<c>--seed</c>). Without one, every run draws a new seed, so repeated
/// evaluations keep finding changes no earlier run tried, and the seed it drew goes into the reports, so any run can
/// be repeated exactly. A comparison of two TestDetta builds passes the same seed to both, or the difference would
/// come from the sample (docs/evaluation.md, "Seeds and replays").
/// </summary>
internal static class SampleSeed
{
    /// <summary>The value that asks for a new seed.</summary>
    public const string Random = "random";

    /// <summary>Largest seed drawn: six digits, easy to read back from a report and type again.</summary>
    private const int MaxDrawn = 999_999;

    /// <summary>Reads <c>--seed</c>: a number of 0 or more, or <see cref="Random"/> for null.</summary>
    /// <param name="value">The option's value.</param>
    /// <param name="seed">The seed; null for a new one each run.</param>
    public static bool TryParse(string value, out int? seed)
    {
        seed = null;
        if (string.Equals(value, Random, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            return false;
        }

        seed = number;
        return true;
    }

    /// <summary>Draws a seed from 1 to <see cref="MaxDrawn"/>; 0 and the default 1 of older reports stay distinguishable from chance only by the report's own flag.</summary>
    public static int Draw() => RandomNumberGenerator.GetInt32(1, MaxDrawn + 1);
}
