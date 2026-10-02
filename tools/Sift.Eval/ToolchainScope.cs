namespace Sift.Eval;

/// <summary>
/// Points this process's environment at a toolchain while one repository is evaluated, so every
/// child inherits it: builds, test runs, recording, setup and cleanup commands. The previous values
/// come back on disposal. Repositories of a corpus are evaluated one after another, so one scope is
/// open at a time.
/// </summary>
internal sealed class ToolchainScope : IDisposable
{
    /// <summary>Values the variables had before the scope; null for unset.</summary>
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

    /// <summary>Sets the environment of <paramref name="directory"/>.</summary>
    /// <param name="directory">Installed toolchain, holding the <c>dotnet</c> host.</param>
    /// <param name="architecture">Its architecture: <c>arm64</c> or <c>x64</c>.</param>
    public ToolchainScope(string directory, string architecture)
    {
        Variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = directory,
            ["DOTNET_HOST_PATH"] = Path.Combine(directory, "dotnet"),
            ["PATH"] = directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            // Only installed toolchains, never one found next to the host or in a global location.
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            // No MSBuild node outlives its build and holds a pipe or a file of the next one.
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

        // An x64 app host on an arm64 machine looks for its runtime here first.
        if (architecture == "x64")
        {
            Variables["DOTNET_ROOT_X64"] = directory;
        }

        foreach (var (name, value) in Variables)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>The variables the scope sets.</summary>
    public Dictionary<string, string> Variables { get; }

    /// <summary>Restores the previous environment.</summary>
    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _previous.Clear();
    }
}
