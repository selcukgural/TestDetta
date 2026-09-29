namespace Sift.Core;

/// <summary>Why a project was selected by <see cref="ImpactAnalyzer"/>.</summary>
public enum ImpactReason
{
    /// <summary>A file inside the project's directory changed.</summary>
    DirectChange,

    /// <summary>A project this one references, directly or transitively, was affected.</summary>
    Dependency,

    /// <summary>A build-wide file such as Directory.Build.props changed in or above the project's directory.</summary>
    BuildConfiguration,
}

/// <summary>A project selected by <see cref="ImpactAnalyzer"/> together with the reason it was selected.</summary>
/// <param name="Project">The affected project.</param>
/// <param name="Reason">Why the project was selected.</param>
public sealed record AffectedProject(ProjectNode Project, ImpactReason Reason);

/// <summary>Outcome of an impact analysis.</summary>
/// <param name="Affected">Affected projects ordered by path.</param>
/// <param name="UnmappedFiles">Changed files that belong to no project, as given to the analyzer.</param>
public sealed record ImpactResult(IReadOnlyList<AffectedProject> Affected, IReadOnlyList<string> UnmappedFiles)
{
    /// <summary>The affected projects that are test projects.</summary>
    public IEnumerable<AffectedProject> Tests => Affected.Where(static a => a.Project.IsTestProject);
}
