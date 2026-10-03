namespace Sift.Coverage;

/// <summary>
/// Environment for test runs nobody watches: Sift's recordings and the evaluation's mutant runs.
/// A failing snapshot test must fail, not open the developer's diff tool. Verify's DiffEngine
/// launched Rider for every Mapperly snapshot a mutant broke.
/// </summary>
public static class UnattendedRun
{
    /// <summary>Variables every unattended test process gets, unless the caller sets them otherwise.</summary>
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // DiffEngine (used by Verify, ApprovalTests and Shouldly) opens no diff tool.
        ["DiffEngine_Disabled"] = "true",
    };
}
