using Mono.Cecil;
using Mono.Cecil.Cil;
using TestDetta.Core;
using TestDetta.Coverage;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// The recording fingerprint follows the code a recording can run: a change there records again, a change
/// to selection code alone keeps the cached maps (docs/design/eval-result-cache.md).
/// </summary>
[Trait("Category", "Eval")]
public sealed class RecordingFingerprintTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-fingerprint-" + Guid.NewGuid().ToString("N"));

    public RecordingFingerprintTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Real_recorder_reaches_graph_and_map_code_but_not_selection()
    {
        var reached = RecordingFingerprint.ReachedTypes(
            [typeof(CoverageRecorder).Assembly.Location, typeof(TestDetta.Coverage.Runtime.TestDettaTracker).Assembly.Location],
            typeof(ProjectGraphLoader).Assembly.Location,
            [typeof(ProjectGraphLoader).FullName!, typeof(CoverageMapStore).FullName!]);

        Assert.Contains(typeof(ProjectGraphLoader).FullName!, reached);
        Assert.Contains(typeof(CoverageMapStore).FullName!, reached);
        Assert.Contains(typeof(TestFilter).FullName!, reached);
        Assert.DoesNotContain(typeof(TestSelector).FullName!, reached);
        Assert.DoesNotContain(typeof(ImpactPropagation).FullName!, reached);
        Assert.DoesNotContain(typeof(AnalysisPipeline).FullName!, reached);
    }

    [Fact]
    public void Same_assemblies_give_the_same_fingerprint()
    {
        var (root, shared) = Build(reachedValue: 1, unreachedValue: 1, implementationValue: 1);

        Assert.Equal(Fingerprint(root, shared), Fingerprint(root, shared));
    }

    [Fact]
    public void Change_to_a_reached_method_changes_the_fingerprint()
    {
        var (root, shared) = Build(reachedValue: 1, unreachedValue: 1, implementationValue: 1);
        var (_, changed) = Build(reachedValue: 2, unreachedValue: 1, implementationValue: 1, name: "b");

        Assert.NotEqual(Fingerprint(root, shared), Fingerprint(root, changed));
    }

    [Fact]
    public void Change_to_an_unreached_method_keeps_the_fingerprint()
    {
        var (root, shared) = Build(reachedValue: 1, unreachedValue: 1, implementationValue: 1);
        var (_, changed) = Build(reachedValue: 1, unreachedValue: 2, implementationValue: 1, name: "b");

        Assert.Equal(Fingerprint(root, shared), Fingerprint(root, changed));
    }

    [Fact]
    public void Change_to_an_implementation_of_a_reached_interface_changes_the_fingerprint()
    {
        var (root, shared) = Build(reachedValue: 1, unreachedValue: 1, implementationValue: 1);
        var (_, changed) = Build(reachedValue: 1, unreachedValue: 1, implementationValue: 2, name: "b");

        Assert.Contains("Shared.Implementation", RecordingFingerprint.ReachedTypes([root], shared, []));
        Assert.NotEqual(Fingerprint(root, shared), Fingerprint(root, changed));
    }

    [Fact]
    public void Commit_id_in_the_version_of_a_root_assembly_keeps_the_fingerprint()
    {
        // The SDK writes the commit into AssemblyInformationalVersion (1.0.0+<sha>): every TestDetta commit
        // recorded every repository again, though the recorder's code had not changed.
        var (_, shared) = Build(reachedValue: 1, unreachedValue: 1, implementationValue: 1);
        var first = WriteRoot("first", "1.0.0+d73d37a7a8174a3e4c74d30e046e68e35591e94f", constant: 1);
        var second = WriteRoot("second", "1.0.0+d1f815bb5ae20b5c8684a07cf27fd593315f3c91", constant: 1);

        Assert.Equal(Fingerprint(first, shared), Fingerprint(second, shared));
    }

    [Fact]
    public void Change_to_any_method_of_a_root_assembly_changes_the_fingerprint()
    {
        var (_, shared) = Build(reachedValue: 1, unreachedValue: 1, implementationValue: 1);
        var first = WriteRoot("first", "1.0.0", constant: 1);
        var second = WriteRoot("second", "1.0.0", constant: 2);

        Assert.NotEqual(Fingerprint(first, shared), Fingerprint(second, shared));
    }

    private static string Fingerprint(string root, string shared) => RecordingFingerprint.Compute([root], shared, []);

    /// <summary>A root assembly with the given informational version and a method returning <paramref name="constant"/>.</summary>
    private string WriteRoot(string name, string version, int constant)
    {
        var folder = Path.Combine(_directory, "root-" + name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Root.dll");
        using var root = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Root", new Version(1, 0)), "Root", ModuleKind.Dll);
        var module = root.MainModule;
        var attribute = module.ImportReference(typeof(System.Reflection.AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)]));
        root.CustomAttributes.Add(new CustomAttribute(attribute) { ConstructorArguments = { new CustomAttributeArgument(module.TypeSystem.String, version) } });
        module.Types.Add(ClassReturning(module, "Recorder", constant));
        root.Write(path);
        return path;
    }

    /// <summary>
    /// Writes a shared assembly with a reached class, an unreached class, an interface and its
    /// implementation, and a root assembly that calls the reached class and holds the interface.
    /// </summary>
    private (string Root, string Shared) Build(int reachedValue, int unreachedValue, int implementationValue, string name = "a")
    {
        var folder = Path.Combine(_directory, name);
        Directory.CreateDirectory(folder);
        var sharedPath = Path.Combine(folder, "Shared.dll");
        using (var shared = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Shared", new Version(1, 0)), "Shared", ModuleKind.Dll))
        {
            var module = shared.MainModule;
            module.Types.Add(ClassReturning(module, "Reached", reachedValue));
            module.Types.Add(ClassReturning(module, "Unreached", unreachedValue));
            var contract = new TypeDefinition("Shared", "IContract", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
            module.Types.Add(contract);
            var implementation = ClassReturning(module, "Implementation", implementationValue);
            implementation.Interfaces.Add(new InterfaceImplementation(contract));
            module.Types.Add(implementation);
            shared.Write(sharedPath);
        }

        var rootPath = Path.Combine(_directory, "Root.dll");
        if (!File.Exists(rootPath))
        {
            using var shared = AssemblyDefinition.ReadAssembly(sharedPath);
            using var root = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Root", new Version(1, 0)), "Root", ModuleKind.Dll);
            var module = root.MainModule;
            var caller = new TypeDefinition("Root", "Caller", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
            var reached = shared.MainModule.GetType("Shared.Reached").Methods.Single(static m => m.Name == "Value");
            caller.Fields.Add(new FieldDefinition("contract", FieldAttributes.Public, module.ImportReference(shared.MainModule.GetType("Shared.IContract"))));
            var call = new MethodDefinition("Call", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            var il = call.Body.GetILProcessor();
            il.Emit(OpCodes.Call, module.ImportReference(reached));
            il.Emit(OpCodes.Ret);
            caller.Methods.Add(call);
            module.Types.Add(caller);
            root.Write(rootPath);
        }

        return (rootPath, sharedPath);
    }

    /// <summary>A public class with a static method <c>Value</c> that returns <paramref name="value"/>.</summary>
    private static TypeDefinition ClassReturning(ModuleDefinition module, string name, int value)
    {
        var type = new TypeDefinition("Shared", name, TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        var method = new MethodDefinition("Value", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(method);
        return type;
    }
}
