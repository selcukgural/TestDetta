using Mono.Cecil;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Resolving references of a test output's assemblies. On Windows a .NET Framework output (Castle.Core, log4net) holds
/// facades that forward to mscorlib, and .NET's own mscorlib forwards back, so resolving a forwarded type recursed
/// until the stack overflowed and td died while recording.
/// </summary>
public sealed class OutputAssemblyResolverTests : IDisposable
{
    private static readonly Version One = new(1, 0, 0, 0);

    private readonly string _directory = Directory.CreateTempSubdirectory("testdetta-resolver-").FullName;

    public void Dispose() => Paths.DeleteTree(_directory);

    [Fact]
    public void A_type_forwarded_round_a_loop_resolves_to_null_instead_of_overflowing_the_stack()
    {
        Forwarder("A", "B");
        Forwarder("B", "A");
        var user = Path.Combine(_directory, "C.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("C", One), "C", ModuleKind.Dll))
        {
            var scope = new AssemblyNameReference("A", One);
            assembly.MainModule.AssemblyReferences.Add(scope);
            assembly.MainModule.Types.Add(new TypeDefinition("C", "Derived", TypeAttributes.Public | TypeAttributes.Class,
                new TypeReference("Loop", "T", assembly.MainModule, scope)));
            assembly.Write(user);
        }

        using var resolver = new OutputAssemblyResolver(user, netFramework: false);
        using var module = ModuleDefinition.ReadModule(user, resolver.Parameters());

        Assert.Null(AssemblyInstrumenter.TryResolve(module.GetType("C.Derived").BaseType));
    }

    [Fact]
    public void A_net_framework_output_resolves_mscorlib_from_net_framework_not_from_the_net_runtime_td_runs_on()
    {
        var assembly = Path.Combine(_directory, "Old.Tests.dll");
        using (var definition = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Old.Tests", One), "Old.Tests", ModuleKind.Dll))
        {
            definition.Write(assembly);
        }

        using var resolver = new OutputAssemblyResolver(assembly, netFramework: true);
        var mscorlib = resolver.Resolve(new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0)));
        if (!OperatingSystem.IsWindows() && !mscorlib.MainModule.FileName.Contains("mono", StringComparison.OrdinalIgnoreCase))
        {
            // Neither .NET Framework nor Mono on this machine: the shared framework stays, and the loop guard covers it.
            return;
        }

        // .NET's mscorlib.dll only forwards System.Object; .NET Framework's (and Mono's) defines it.
        Assert.NotNull(mscorlib.MainModule.GetType("System.Object"));
    }

    /// <summary>Writes <paramref name="name"/>.dll, which forwards Loop.T to <paramref name="target"/>.</summary>
    private void Forwarder(string name, string target)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, One), name, ModuleKind.Dll);
        var scope = new AssemblyNameReference(target, One);
        assembly.MainModule.AssemblyReferences.Add(scope);
        assembly.MainModule.ExportedTypes.Add(new ExportedType("Loop", "T", assembly.MainModule, scope) { Attributes = TypeAttributes.Forwarder });
        assembly.Write(Path.Combine(_directory, name + ".dll"));
    }
}
