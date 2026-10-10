namespace TestDetta.Core;

/// <summary>
/// What a changed non-code file inside a project folder (a README, a changelog, a JSON file) runs
/// (docs/design/non-code-files.md). The developer chooses with <c>--non-code-files</c>; the default is
/// <see cref="AnalysisOptions.DefaultNonCodeFiles"/>.
/// </summary>
public enum NonCodeFileRule
{
    /// <summary>The owning project and every project that depends on it run completely.</summary>
    Project,

    /// <summary>
    /// A file the build does not use (<see cref="ProjectNode.InertItems"/> and the repository's MSBuild
    /// files) runs only the test classes that read it, in test projects whose maps saw every read; a
    /// file the build uses, and a test project whose map cannot vouch for its reads, still run completely.
    /// </summary>
    Readers,
}
