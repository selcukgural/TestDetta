namespace Sift.Core;

/// <summary>One .csproj file and the projects it references.</summary>
/// <param name="Path">Normalized absolute path of the .csproj file.</param>
/// <param name="Directory">Normalized absolute path of the directory containing the .csproj file.</param>
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
}
