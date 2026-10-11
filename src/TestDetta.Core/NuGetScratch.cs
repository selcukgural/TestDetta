namespace TestDetta.Core;

/// <summary>
/// Keeps NuGet's cross-process locks shared when processes get temporary directories of their own. NuGet takes turns
/// writing its shared HTTP cache through lock files under <c>&lt;temporary directory&gt;/NuGetScratch/lock</c>; recording
/// shards, audit workers and evaluation workers each get their own temporary directory (suites claim fixed names
/// there), which split those locks, and the analyzer tests of two of them resolving reference packages wrote the same
/// cache file at once and failed ("being used by another process", Carter, 2026-10-11).
/// </summary>
public static class NuGetScratch
{
    /// <summary>The variable that moves NuGet's scratch folder.</summary>
    public const string Variable = "NUGET_SCRATCH";

    /// <summary>
    /// Sets <see cref="Variable"/> in <paramref name="environment"/> to the scratch folder NuGet would use without a
    /// temporary directory of the process's own, unless the environment or this process already names one.
    /// </summary>
    /// <param name="environment">Variables of the processes about to get their own temporary directory.</param>
    public static void KeepShared(IDictionary<string, string> environment)
    {
        if (environment.ContainsKey(Variable) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            return;
        }

        environment[Variable] = Path.Combine(Path.GetTempPath(), "NuGetScratch");
    }
}
