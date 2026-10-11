using System.Collections.Frozen;
using Mono.Cecil;
using Mono.Cecil.Cil;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Coverage;

/// <summary>
/// Sends the calls of an instrumented module that read a file or list a directory by a path, start a
/// process or load an assembly through a wrapper that tells the tracker the path first
/// (docs/design/runtime-file-reads.md). Each wrapper is a
/// static method of a type added to the module, with the call's own arguments and result: it reports
/// the path argument, then makes the original call. The call instruction becomes a call of the wrapper,
/// which takes and leaves the same stack, so nothing around it moves.
/// </summary>
internal static class FileReadCalls
{
    /// <summary>Name of the type that holds the wrappers.</summary>
    public const string HolderName = "<TestDettaFileReads>";

    /// <summary>
    /// Methods and constructors whose first parameter is a path, by declaring type and name, with
    /// true for a directory. Only overloads whose first parameter is a <c>string</c> are wrapped.
    /// </summary>
    private static readonly FrozenDictionary<string, bool> Readers = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["System.IO.File::ReadAllText"] = false,
        ["System.IO.File::ReadAllTextAsync"] = false,
        ["System.IO.File::ReadAllLines"] = false,
        ["System.IO.File::ReadAllLinesAsync"] = false,
        ["System.IO.File::ReadAllBytes"] = false,
        ["System.IO.File::ReadAllBytesAsync"] = false,
        ["System.IO.File::ReadLines"] = false,
        ["System.IO.File::ReadLinesAsync"] = false,
        ["System.IO.File::OpenRead"] = false,
        ["System.IO.File::OpenText"] = false,
        ["System.IO.File::Open"] = false,
        ["System.IO.File::OpenHandle"] = false,
        ["System.IO.File::Exists"] = false,
        ["System.IO.File::Copy"] = false,
        ["System.IO.FileStream::.ctor"] = false,
        ["System.IO.StreamReader::.ctor"] = false,
        ["System.IO.FileInfo::.ctor"] = false,
        ["System.Xml.Linq.XDocument::Load"] = false,
        ["System.Xml.Linq.XElement::Load"] = false,
        ["System.Xml.XmlReader::Create"] = false,
        ["System.Xml.XmlDocument::Load"] = false,
        ["System.IO.Directory::Exists"] = true,
        ["System.IO.Directory::GetFiles"] = true,
        ["System.IO.Directory::GetDirectories"] = true,
        ["System.IO.Directory::GetFileSystemEntries"] = true,
        ["System.IO.Directory::EnumerateFiles"] = true,
        ["System.IO.Directory::EnumerateDirectories"] = true,
        ["System.IO.Directory::EnumerateFileSystemEntries"] = true,
        ["System.IO.DirectoryInfo::.ctor"] = true,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Methods that start a process or load an assembly, by declaring type and name, every overload:
    /// their first two arguments (the object first, for an instance method) go to
    /// <see cref="TestDettaTracker.OutputUsed"/>, which finds the paths and names among them.
    /// <c>AppDomain.GetAssemblies</c> is one too: code that enumerates the loaded assemblies may use any of
    /// them, so the tracker names every one loaded at that moment (docs/design/loaded-outputs.md).
    /// </summary>
    private static readonly FrozenSet<string> Starters = FrozenSet.Create(
        StringComparer.Ordinal,
        "System.Diagnostics.Process::Start",
        "System.Reflection.Assembly::LoadFrom",
        "System.Reflection.Assembly::UnsafeLoadFrom",
        "System.Reflection.Assembly::LoadFile",
        "System.Reflection.Assembly::Load",
        "System.Runtime.Loader.AssemblyLoadContext::LoadFromAssemblyPath",
        "System.Runtime.Loader.AssemblyLoadContext::LoadFromAssemblyName",
        "System.Runtime.Loader.AssemblyLoadContext::LoadFromNativeImagePath",
        "System.AppDomain::ExecuteAssembly",
        "System.AppDomain::ExecuteAssemblyByName",
        "System.AppDomain::GetAssemblies");

    /// <summary>
    /// The part of <see cref="Starters"/> that loads an assembly by its name rather than by a path: reported through
    /// <see cref="OutputProbes.LoadedByName"/>, so a scanner in the product's code loading every library of the dependency
    /// context (Carter's module catalog) is not taken for a test that loads a project's output to inspect it.
    /// </summary>
    private static readonly FrozenSet<string> NameLoaders = FrozenSet.Create(
        StringComparer.Ordinal,
        "System.Reflection.Assembly::Load",
        "System.Runtime.Loader.AssemblyLoadContext::LoadFromAssemblyName",
        "System.AppDomain::ExecuteAssemblyByName");

    /// <summary>
    /// True when <paramref name="module"/> references a method whose calls <see cref="Wrap"/> reports as
    /// a file read or a directory listing. Reads only the member reference table, no method body, so a
    /// third-party assembly that reads no file costs almost nothing to check.
    /// </summary>
    /// <param name="module">Module to look at.</param>
    public static bool ReferencesReaders(ModuleDefinition module) =>
        module.GetMemberReferences().OfType<MethodReference>().Any(static m => Directory(m) is not null);

    /// <summary>Wraps every file-reading, process-starting and assembly-loading call in the bodies of <paramref name="module"/>.</summary>
    /// <param name="module">Module being instrumented.</param>
    /// <param name="fileRead">Reference to <see cref="TestDettaTracker.FileRead"/> in the module.</param>
    /// <param name="directoryListed">Reference to <see cref="TestDettaTracker.DirectoryListed"/> in the module.</param>
    /// <param name="outputs">
    /// References to the tracker's output probes in the module; null to leave process starts and
    /// assembly loads alone, as in third-party code, where the test platform loading the test assembly would
    /// look like a use of the test project's output.
    /// </param>
    /// <returns>The number of calls wrapped.</returns>
    public static int Wrap(ModuleDefinition module, MethodReference fileRead, MethodReference directoryListed, OutputProbes? outputs)
    {
        TypeDefinition? holder = null;
        var wrappers = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        var wrapped = 0;
        foreach (var method in module.GetTypes().SelectMany(static t => t.Methods).Where(static m => m.HasBody).ToList())
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj)
                    || instruction.Operand is not MethodReference target
                    || instruction.Previous?.OpCode.OpCodeType == OpCodeType.Prefix)
                {
                    continue;
                }

                var starter = outputs is not null && IsStarter(target);
                if (!starter && Directory(target) is null)
                {
                    continue;
                }

                var key = $"{instruction.OpCode.Code}|{target.FullName}";
                if (!wrappers.TryGetValue(key, out var wrapper))
                {
                    holder ??= AddHolder(module);
                    var probe = starter && NameLoaders.Contains($"{target.DeclaringType.FullName}::{target.Name}") ? outputs!.LoadedByName : outputs?.Used;
                    wrapper = starter
                        ? Wrapper(module, holder, target, instruction.OpCode, probe!, wrappers.Count, arguments: 2)
                        : Wrapper(module, holder, target, instruction.OpCode, Directory(target) == true ? directoryListed : fileRead, wrappers.Count, arguments: 1);
                    wrappers[key] = wrapper;
                }

                instruction.OpCode = OpCodes.Call;
                instruction.Operand = wrapper;
                wrapped++;
            }
        }

        return wrapped;
    }

    /// <summary>For a call to wrap, whether its path names a directory; null for any other call.</summary>
    /// <param name="target">The called method.</param>
    private static bool? Directory(MethodReference target) =>
        target is not GenericInstanceMethod
        && target.DeclaringType is not GenericInstanceType
        && target.Parameters.Count > 0
        && target.Parameters[0].ParameterType.FullName == "System.String"
        && Readers.TryGetValue($"{target.DeclaringType.FullName}::{target.Name}", out var directory)
            ? directory
            : null;

    /// <summary>True for a call that starts a process or loads an assembly; see <see cref="Starters"/>.</summary>
    /// <param name="target">The called method.</param>
    private static bool IsStarter(MethodReference target) =>
        target is not GenericInstanceMethod
        && target.DeclaringType is not GenericInstanceType
        && Starters.Contains($"{target.DeclaringType.FullName}::{target.Name}");

    /// <summary>Adds the internal static type that holds the wrappers.</summary>
    /// <param name="module">Module being instrumented.</param>
    private static TypeDefinition AddHolder(ModuleDefinition module)
    {
        var holder = new TypeDefinition(
            string.Empty,
            HolderName,
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(holder);
        return holder;
    }

    /// <summary>
    /// A static method with the arguments of <paramref name="target"/> (the object first, for an
    /// instance method) and its result (the new object, for a constructor) that reports the path
    /// argument through <paramref name="probe"/> and then makes the call as <paramref name="opCode"/> did.
    /// A probe of two arguments gets the first two (boxed, null when there is no second).
    /// </summary>
    /// <param name="module">Module being instrumented.</param>
    /// <param name="holder">Type that holds the wrappers.</param>
    /// <param name="target">The wrapped method.</param>
    /// <param name="opCode">The original call instruction's opcode.</param>
    /// <param name="probe">Tracker method that takes the path.</param>
    /// <param name="number">A number that makes the wrapper's name unique.</param>
    /// <param name="arguments">How many arguments <paramref name="probe"/> takes: the path, or the first two as objects.</param>
    private static MethodDefinition Wrapper(ModuleDefinition module, TypeDefinition holder, MethodReference target, OpCode opCode, MethodReference probe, int number, int arguments)
    {
        var constructor = opCode.Code == Code.Newobj;
        var returnType = constructor ? target.DeclaringType : target.ReturnType;
        var wrapper = new MethodDefinition(
            $"{target.DeclaringType.Name}_{(constructor ? "new" : target.Name)}_{number}",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            returnType);
        if (target.HasThis && !constructor)
        {
            wrapper.Parameters.Add(new ParameterDefinition(target.DeclaringType));
        }

        foreach (var parameter in target.Parameters)
        {
            wrapper.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }

        var il = wrapper.Body.GetILProcessor();
        if (arguments == 1)
        {
            il.Emit(OpCodes.Ldarg, wrapper.Parameters[target.HasThis && !constructor ? 1 : 0]);
        }
        else
        {
            for (var i = 0; i < arguments; i++)
            {
                if (i >= wrapper.Parameters.Count)
                {
                    il.Emit(OpCodes.Ldnull);
                    continue;
                }

                var parameter = wrapper.Parameters[i];
                il.Emit(OpCodes.Ldarg, parameter);
                if (parameter.ParameterType.IsValueType || parameter.ParameterType.IsGenericParameter)
                {
                    il.Emit(OpCodes.Box, parameter.ParameterType);
                }
            }
        }

        il.Emit(OpCodes.Call, probe);
        foreach (var parameter in wrapper.Parameters)
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

        il.Emit(opCode, module.ImportReference(target));
        il.Emit(OpCodes.Ret);
        holder.Methods.Add(wrapper);
        return wrapper;
    }
}

/// <summary>The tracker's probes for process starts and assembly loads, as references in the module being instrumented.</summary>
/// <param name="Used">Reference to <see cref="TestDettaTracker.OutputUsed"/>: starts, loads by a path, scans.</param>
/// <param name="LoadedByName">
/// Reference to <see cref="TestDettaTracker.OutputLoadedByName"/> for loads by name in code that also runs outside the tests;
/// <paramref name="Used"/> again in a test or its helpers.
/// </param>
internal sealed record OutputProbes(MethodReference Used, MethodReference LoadedByName);
