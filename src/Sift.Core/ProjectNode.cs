namespace Sift.Core;

/// <summary>Language of a project, from its file extension.</summary>
public enum ProjectLanguage
{
    /// <summary><c>.csproj</c>: analysed down to members.</summary>
    CSharp,

    /// <summary><c>.fsproj</c>: Sift cannot read its code, so its impact is traced at project level only.</summary>
    FSharp,

    /// <summary><c>.vbproj</c>: Sift cannot read its code, so its impact is traced at project level only.</summary>
    VisualBasic,
}

/// <summary>One project file and the projects it references.</summary>
/// <param name="Path">Normalized absolute path of the project file.</param>
/// <param name="Directory">Normalized absolute path of the directory containing the project file.</param>
/// <param name="IsTestProject">
/// True when the project sets <c>IsTestProject</c>, references <c>Microsoft.NET.Test.Sdk</c>,
/// or its file name ends with <c>Test</c> or <c>Tests</c>.
/// </param>
/// <param name="References">Normalized absolute paths of the projects named in <c>ProjectReference</c> items.</param>
public sealed record ProjectNode(string Path, string Directory, bool IsTestProject, IReadOnlyList<string> References)
{
    /// <summary>
    /// Name of the compiled assembly without extension: the <c>AssemblyName</c> property when the
    /// project sets it, otherwise the project file name.
    /// </summary>
    public string AssemblyName { get; init; } = System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Language of the project.</summary>
    public ProjectLanguage Language { get; init; } = ProjectLanguage.CSharp;

    /// <summary>
    /// Files outside <see cref="Directory"/> that the project compiles or ships, such as
    /// <c>&lt;Compile Include="../Shared/*.cs" /&gt;</c> or the items of a shared <c>.projitems</c>:
    /// normalized absolute paths, possibly with wildcards.
    /// </summary>
    public IReadOnlyList<string> ExternalItems { get; init; } = [];

    /// <summary>
    /// MSBuild files the project imports beyond the implicit <c>Directory.Build.*</c> files (a shared
    /// <c>build/common.props</c>, a <c>.projitems</c>), found through its own imports and theirs.
    /// A change to one of them changes how the project builds.
    /// </summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>
    /// True when an import or an item outside the project directory is built from properties that
    /// cannot be known without evaluating MSBuild. Changed build or source files that no project
    /// claims are then assumed to affect this project.
    /// </summary>
    public bool HasUnresolvedInputs { get; init; }

    /// <summary>
    /// Packages the project references with <c>PackageReference</c>, in its own file, the files it
    /// imports or the <c>Directory.Build.*</c> files above it. Used to tell which projects a central
    /// package version change reaches.
    /// </summary>
    public IReadOnlySet<string> PackageReferences { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
