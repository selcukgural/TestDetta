using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sift.Eval;

/// <summary>
/// The .NET a repository is evaluated with: an exact SDK, extra runtimes and an architecture. See
/// docs/design/hermetic-toolchains.md.
/// </summary>
/// <param name="Sdk">Exact SDK version, such as <c>10.0.401</c>; it brings its own runtime.</param>
/// <param name="Runtimes">Extra ASP.NET Core runtime versions (each holds the .NET runtime of the same version).</param>
/// <param name="Architecture"><c>arm64</c> or <c>x64</c>; null for the machine's.</param>
internal sealed partial record Toolchain(string Sdk, IReadOnlyList<string> Runtimes, string? Architecture = null)
{
    /// <summary>Runtime identifiers a lock entry is written for, so a lock written on one machine serves the others.</summary>
    public static readonly IReadOnlyList<string> SupportedRids = ["osx-arm64", "osx-x64", "linux-x64", "linux-arm64"];

    /// <summary>Architecture the toolchain runs as.</summary>
    public string EffectiveArchitecture =>
        Architecture ?? (RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");

    /// <summary>Runtime identifier of the archives, such as <c>osx-arm64</c>.</summary>
    public string Rid => RidFor(OperatingSystem.IsMacOS(), EffectiveArchitecture);

    /// <summary>The runtime identifier for an operating system and architecture; one of <see cref="SupportedRids"/>.</summary>
    /// <param name="macOS">True on macOS, false on Linux.</param>
    /// <param name="architecture"><c>arm64</c> or <c>x64</c>.</param>
    internal static string RidFor(bool macOS, string architecture) => $"{(macOS ? "osx" : "linux")}-{architecture}";

    /// <summary>Directory name of the installed toolchain, such as <c>sdk-10.0.401+8.0.31</c>; runtimes in a fixed order.</summary>
    public string DirectoryName =>
        string.Concat(Runtimes.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Select(static r => "+" + r).Prepend("sdk-" + Sdk));

    /// <summary>The toolchain for reports, such as <c>SDK 10.0.401 + 8.0.31 (osx-arm64)</c>.</summary>
    public string Description =>
        $"SDK {Sdk}{string.Concat(Runtimes.Order(StringComparer.Ordinal).Select(static r => " + " + r))} ({Rid})";

    /// <summary>
    /// Checks the versions and the architecture. They become directory names and URLs, so only plain
    /// version numbers pass.
    /// </summary>
    /// <param name="error">What is wrong, for the user.</param>
    public bool IsValid(out string? error)
    {
        foreach (var version in Runtimes.Prepend(Sdk))
        {
            if (!VersionPattern().IsMatch(version))
            {
                error = $"'{version}' is not a .NET version such as 10.0.401";
                return false;
            }
        }

        if (Architecture is not (null or "arm64" or "x64"))
        {
            error = $"architecture must be arm64 or x64, not '{Architecture}'";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>A released version: three numbers, maybe a prerelease label.</summary>
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$")]
    private static partial Regex VersionPattern();
}
