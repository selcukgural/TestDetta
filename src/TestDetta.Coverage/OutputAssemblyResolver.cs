using Mono.Cecil;

namespace TestDetta.Coverage;

/// <summary>
/// Resolves the references of an assembly in a test output the way its own runtime would: from the output's folder,
/// then from the framework the assembly targets. A .NET Framework assembly resolves its framework from .NET Framework
/// (on Windows its runtime folder, elsewhere Mono's), never from the .NET runtime TestDetta itself runs on: the
/// .NET <c>mscorlib.dll</c> is a shim that forwards to <c>System.Runtime</c>, and a .NET Framework output holds a
/// <c>System.Runtime.dll</c> facade that forwards back to <c>mscorlib</c>, so resolving a forwarded type went round
/// that loop until the stack overflowed and took <c>td</c> down (Castle.Core and log4net on Windows). Every module
/// this resolver reads also gets a <see cref="CycleSafeMetadataResolver"/>, so a forwarding loop of any other origin
/// ends as an unresolved type instead of a crash.
/// </summary>
internal sealed class OutputAssemblyResolver : DefaultAssemblyResolver
{
    /// <summary>Folders that hold .NET Framework's own assemblies on this machine (real <c>mscorlib</c>, not a facade).</summary>
    private static readonly Lazy<List<string>> NetFrameworkDirectories = new(FindNetFrameworkDirectories);

    /// <summary>Creates a resolver for <paramref name="assembly"/>.</summary>
    /// <param name="assembly">The assembly whose references are resolved; its folder is searched first.</param>
    /// <param name="netFramework">
    /// True when the output runs on .NET Framework (its test assembly targets it), so that a netstandard library in it
    /// resolves as it will run; null takes the assembly's own target.
    /// </param>
    public OutputAssemblyResolver(string assembly, bool? netFramework = null)
    {
        MetadataResolver = new CycleSafeMetadataResolver(this);
        AddSearchDirectory(Path.GetDirectoryName(assembly)!);
        var framework = (netFramework ?? RuntimeDeployment.IsNetFramework(assembly)) ? NetFrameworkDirectories.Value : [];

        // Framework assemblies (System.Threading.RateLimiting, Microsoft.AspNetCore.*) live in the shared framework,
        // not in the test output, and Cecil resolves some of them while writing. Without a .NET Framework on the
        // machine, a .NET Framework assembly keeps the shared framework too: the cycle guard stops what that loops.
        foreach (var directory in framework.Count > 0 ? framework : AssemblyInstrumenter.SharedFrameworkDirectories.Value)
        {
            AddSearchDirectory(directory);
        }
    }

    /// <summary>The metadata resolver every module read through this resolver uses; give it to the first module too.</summary>
    public IMetadataResolver MetadataResolver { get; }

    /// <summary>Reading parameters for an assembly of the output, with this resolver and its cycle-safe metadata resolver.</summary>
    /// <param name="readSymbols">True to read the assembly's PDB.</param>
    public ReaderParameters Parameters(bool readSymbols = false) => new()
    {
        InMemory = true,
        ReadSymbols = readSymbols,
        AssemblyResolver = this,
        MetadataResolver = MetadataResolver,
    };

    /// <summary>Resolves a referenced assembly, reading it with the cycle-safe metadata resolver.</summary>
    /// <param name="name">The reference.</param>
    /// <param name="parameters">Reading parameters.</param>
    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        parameters.MetadataResolver ??= MetadataResolver;
        return base.Resolve(name, parameters);
    }

    /// <summary>
    /// .NET Framework 4's runtime folder on Windows (64-bit first), else Mono's 4.5 profile, which hold the real
    /// <c>mscorlib</c>; empty when neither is installed.
    /// </summary>
    private static List<string> FindNetFrameworkDirectories()
    {
        List<string> candidates = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework", "v4.0.30319"),
            ]
            :
            [
                "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5",
                "/opt/homebrew/lib/mono/4.5",
                "/usr/local/lib/mono/4.5",
                "/usr/lib/mono/4.5",
            ];

        // One is enough: the 64-bit and 32-bit folders hold the same assemblies.
        return [.. candidates.Where(static d => File.Exists(Path.Combine(d, "mscorlib.dll"))).Take(1)];
    }
}
