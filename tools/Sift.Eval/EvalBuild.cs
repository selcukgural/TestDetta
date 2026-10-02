using System.Reflection;

namespace Sift.Eval;

/// <summary>Which build of Sift produced a report, so results of two runs can be told apart.</summary>
internal static class EvalBuild
{
    /// <summary>
    /// The commit this build was made from, as the SDK stamps it into the informational version
    /// (<c>1.0.0+&lt;sha&gt;</c>); <c>unknown</c> outside a git checkout. Changes not yet committed when
    /// the build ran are not visible here, so corpus builds are made from a clean, committed tree.
    /// </summary>
    public static string Commit { get; } =
        typeof(EvalBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version
        && version.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0
            ? version[(plus + 1)..]
            : "unknown";
}
