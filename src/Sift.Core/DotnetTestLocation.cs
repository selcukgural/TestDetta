namespace Sift.Core;

/// <summary>
/// Where <c>dotnet test</c> runs a test project and which runner it uses there. The SDK reads the
/// nearest <c>global.json</c> at or above the directory it runs in, for the SDK version and the
/// runner (<c>"test": { "runner": "Microsoft.Testing.Platform" }</c>). A repository may keep that file
/// next to its solution rather than at the root (Refit: <c>src/global.json</c>); run from the root,
/// the opt-in is not seen and the .NET 10 SDK refuses to run a testing-platform project. So each
/// project runs in the directory of the nearest <c>global.json</c> at or above it within the
/// repository, as a developer working on it would, or in the root when there is none.
/// </summary>
/// <param name="Directory">Run directory relative to the repository root, with '/' separators; empty for the root.</param>
/// <param name="Mode">Runner the <c>global.json</c> seen there selects.</param>
public sealed record DotnetTestLocation(string Directory, DotnetTestMode Mode)
{
    /// <summary>Finds the location for <paramref name="projectPath"/>.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="projectPath">Full path of the test project file.</param>
    /// <exception cref="InvalidDataException">The <c>global.json</c> in effect is not valid JSON.</exception>
    public static DotnetTestLocation For(string repoRoot, string projectPath)
    {
        var root = Path.GetFullPath(repoRoot);
        var normalizedRoot = Paths.Normalize(root);
        var directory = root;
        for (var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
             current is not null && Paths.IsUnder(Paths.Normalize(current.FullName), normalizedRoot);
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json")))
            {
                directory = current.FullName;
                break;
            }
        }

        var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
        return new DotnetTestLocation(relative == "." ? "" : relative, DotnetTestModeDetector.Detect(directory));
    }

    /// <summary>The run directory under <paramref name="repoRoot"/>, which may be a copy of the repository the location was found in.</summary>
    /// <param name="repoRoot">Root of the repository or of a copy of it.</param>
    public string FullDirectory(string repoRoot) => Directory.Length == 0 ? repoRoot : Path.Combine(repoRoot, Directory);

    /// <summary>
    /// <c>test</c> and the project argument, relative to the run directory: positional for VSTest,
    /// <c>--project</c> for Microsoft.Testing.Platform.
    /// </summary>
    /// <param name="repoRoot">Root of the repository the project path is in.</param>
    /// <param name="projectPath">Full path of the test project file.</param>
    public List<string> TestArguments(string repoRoot, string projectPath)
    {
        var project = Path.GetRelativePath(FullDirectory(repoRoot), projectPath).Replace('\\', '/');
        return Mode == DotnetTestMode.TestingPlatform ? ["test", "--project", project] : ["test", project];
    }
}
