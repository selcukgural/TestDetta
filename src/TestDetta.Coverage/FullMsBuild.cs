using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Restores and builds legacy (non-SDK) projects with the full MSBuild, which `dotnet build` cannot do
/// for them: their <c>packages.config</c> restores only through it, and .NET Framework reference
/// assemblies come with Visual Studio, the Build Tools or Mono (docs/design/legacy-projects.md). The
/// tests then run from the compiled assembly with <c>dotnet test &lt;assembly&gt;</c>, the same VSTest
/// engine as <c>vstest.console.exe</c>.
/// </summary>
/// <param name="logger">Receives the command lines.</param>
/// <param name="msbuild">The MSBuild executable (<see cref="Locate"/>).</param>
internal sealed class FullMsBuild(ILogger logger, string msbuild)
{
    /// <summary>Environment variable naming the MSBuild executable to use; set by <c>--msbuild</c> too.</summary>
    public const string Variable = "TESTDETTA_MSBUILD";

    /// <summary>Longest a restore or build may take.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The executable this instance runs.</summary>
    public string Executable { get; } = msbuild;

    /// <summary>
    /// The full MSBuild: <see cref="Variable"/>, else on Windows the newest Visual Studio or Build Tools
    /// MSBuild that <c>vswhere</c> finds, else <c>msbuild</c> on the PATH (Mono's on macOS and Linux).
    /// Null when none is found.
    /// </summary>
    public static string? Locate()
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured)
        {
            return configured;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && VisualStudioMsBuild() is { } visualStudio)
        {
            return visualStudio;
        }

        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "msbuild.exe" : "msbuild";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Restores (<c>packages.config</c> included) and builds <paramref name="project"/> for
    /// <paramref name="configuration"/>; returns the exit code of the first step that failed, or 0.
    /// The restore runs on the solution that lists the project, as NuGet needs one to place the
    /// <c>packages</c> folder; without one, on the project with the repository as the solution folder.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="project">Project file.</param>
    /// <param name="configuration">Build configuration.</param>
    /// <param name="output">Receives MSBuild's output.</param>
    public int Build(string repoRoot, string project, string configuration, TextWriter output)
    {
        var process = new DotnetProcess(logger, Executable);
        var directory = Path.GetDirectoryName(project)!;
        var solution = SolutionOf(repoRoot, project);
        List<string> restore = solution is not null
            ? [solution, "-t:restore", "-p:RestorePackagesConfig=true", "-nologo", "-v:m"]
            : [project, "-t:restore", "-p:RestorePackagesConfig=true", $"-p:SolutionDir={repoRoot.TrimEnd('/', '\\')}{Path.DirectorySeparatorChar}", "-nologo", "-v:m"];
        var exit = process.Run(restore, directory, new Dictionary<string, string>(), output, BuildTimeout);
        if (exit != 0)
        {
            return exit;
        }

        return process.Run([project, $"-p:Configuration={configuration}", "-nologo", "-v:m"], directory, new Dictionary<string, string>(), output, BuildTimeout);
    }

    /// <summary>The nearest solution file at or above the project's folder that lists it; null for none.</summary>
    /// <param name="repoRoot">Repository root, where the search stops.</param>
    /// <param name="project">Project file.</param>
    internal static string? SolutionOf(string repoRoot, string project)
    {
        var root = Paths.Normalize(repoRoot);
        var fileName = Path.GetFileName(project);
        for (var directory = Paths.Normalize(Path.GetDirectoryName(project)!); Paths.IsUnder(directory, root); directory = Paths.Normalize(Path.GetDirectoryName(directory)!))
        {
            foreach (var solution in Directory.EnumerateFiles(directory, "*.sln").Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(directory, project);
                var text = File.ReadAllText(solution);
                if (text.Contains(relative.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
                    || text.Contains(relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    || text.Contains($"\"{fileName}\"", StringComparison.OrdinalIgnoreCase))
                {
                    // Enumerated under a normalized folder, the name joins it with '\' on Windows.
                    return Paths.Normalize(solution);
                }
            }

            if (directory.Length <= root.Length)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>MSBuild.exe of the newest Visual Studio or Build Tools, through vswhere; null when none.</summary>
    private static string? VisualStudioMsBuild()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                vswhere, "-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var found = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            process.WaitForExit();
            return found is not null && File.Exists(found) ? found : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
