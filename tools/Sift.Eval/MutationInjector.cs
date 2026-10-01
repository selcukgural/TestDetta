using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Eval;

/// <summary>Assemblies rewritten for one mutant, and how to put them back.</summary>
/// <param name="Backups">Rewritten file (assembly or its .pdb) to its backup copy.</param>
/// <param name="MutatedMethods">Number of compiled methods that now throw.</param>
internal sealed record Mutation(IReadOnlyDictionary<string, string> Backups, int MutatedMethods) : IDisposable
{
    /// <summary>
    /// Puts the originals in place until the returned object is disposed, then the mutant again, so a
    /// control run can show how tests behave without the mutant while it is still applied.
    /// </summary>
    public IDisposable Lift()
    {
        foreach (var (original, backup) in Backups)
        {
            File.Move(original, original + ".mutant", overwrite: true);
            File.Copy(backup, original, overwrite: true);
        }

        return new Restore(Backups.Keys);
    }

    /// <summary>Copies the originals back.</summary>
    public void Dispose()
    {
        foreach (var (original, backup) in Backups)
        {
            File.Copy(backup, original, overwrite: true);
            File.Delete(backup);
        }
    }
}

/// <summary>Puts lifted mutants back; see <see cref="Mutation.Lift"/>.</summary>
/// <param name="originals">Rewritten files whose mutated copies were set aside.</param>
internal sealed class Restore(IEnumerable<string> originals) : IDisposable
{
    /// <summary>Moves each mutated copy back over the original.</summary>
    public void Dispose()
    {
        foreach (var original in originals)
        {
            File.Move(original + ".mutant", original, overwrite: true);
        }
    }
}

/// <summary>
/// Makes a source member fail: by default every compiled method that belongs to it (its overloads,
/// and the lambdas, iterators and async state machines it contains) starts by throwing; other
/// <see cref="MutantKind"/>s return a default, invert a branch or change a field's initial value. Working on the
/// compiled assemblies keeps the mutant independent of the member's syntax and of warnings-as-errors.
/// </summary>
internal static class MutationInjector
{
    /// <summary>Message of the injected exception, so a failure can be traced to the mutant.</summary>
    private const string Message = "Sift mutant";

    /// <summary>Rewrites every copy of the assembly that declares <paramref name="target"/>.</summary>
    /// <param name="assemblies">Copies of the assembly in the test outputs.</param>
    /// <param name="target">Member (or field) to break.</param>
    /// <param name="backupDirectory">Where the originals are copied first.</param>
    /// <param name="kind">How to break it.</param>
    public static Mutation Inject(IReadOnlyList<string> assemblies, CoveredMethod target, string backupDirectory, MutantKind kind = MutantKind.Throw)
    {
        Directory.CreateDirectory(backupDirectory);
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var mutated = 0;

        foreach (var assembly in assemblies)
        {
            var backup = Path.Combine(backupDirectory, $"{backups.Count}-{Path.GetFileName(assembly)}");
            File.Copy(assembly, backup, overwrite: true);

            // The .pdb is rewritten with the assembly (see InjectOne), so it is backed up and lifted too.
            var pdb = Path.ChangeExtension(assembly, ".pdb");
            var pdbBackup = File.Exists(pdb) ? Path.ChangeExtension(backup, ".pdb") : null;
            if (pdbBackup is not null)
            {
                File.Copy(pdb, pdbBackup, overwrite: true);
            }

            var count = InjectOne(assembly, target, kind);
            if (count == 0)
            {
                File.Delete(backup);
                if (pdbBackup is not null)
                {
                    File.Delete(pdbBackup);
                }

                continue;
            }

            backups[assembly] = backup;
            if (pdbBackup is not null)
            {
                backups[pdb] = pdbBackup;
            }

            mutated += count;
        }

        return new Mutation(backups, mutated);
    }

    /// <summary>Rewrites one assembly; returns the number of mutated methods (or field stores).</summary>
    /// <param name="path">Assembly file.</param>
    /// <param name="target">Member to break.</param>
    /// <param name="kind">How to break it.</param>
    private static int InjectOne(string path, CoveredMethod target, MutantKind kind)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);

        // Writing resolves the types of constants, such as an ASP.NET Core enum as a default
        // parameter value (FastEndpoints), whose assemblies live in the shared framework.
        foreach (var directory in AssemblyInstrumenter.SharedFrameworkDirectories.Value)
        {
            resolver.AddSearchDirectory(directory);
        }
        // A .pdb left next to a rewritten assembly no longer matches it, and tools that read symbols
        // (PublicApiGenerator in approval tests) then fail for every mutant, so it is rewritten too.
        var hasSymbols = File.Exists(Path.ChangeExtension(path, ".pdb"));
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true, ReadSymbols = hasSymbols, AssemblyResolver = resolver });

        var exception = new TypeReference("System", nameof(InvalidOperationException), module, module.TypeSystem.CoreLibrary);
        var constructor = new MethodReference(".ctor", module.TypeSystem.Void, exception) { HasThis = true };
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var count = 0;
        foreach (var type in module.GetTypes())
        {
            if (MethodNames.IsCompilerInfrastructure(type))
            {
                continue;
            }

            if (kind == MutantKind.FieldValue)
            {
                count += ChangeFieldValue(type, target);
                continue;
            }

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body.Instructions.Count == 0 || MethodNames.Map(method) != target)
                {
                    continue;
                }

                // A value or branch change inside a state machine or closure breaks the machinery
                // (an await that never completes) rather than the member's logic.
                if (kind != MutantKind.Throw && method.DeclaringType.Name.StartsWith('<'))
                {
                    continue;
                }

                var body = method.Body;
                body.SimplifyMacros();
                var changed = kind switch
                {
                    MutantKind.ReturnDefault => ReturnDefault(method),
                    MutantKind.FlipBranch => FlipFirstBranch(body),
                    _ => Throw(body, constructor, target),
                };

                body.OptimizeMacros();
                count += changed ? 1 : 0;
            }
        }

        if (count > 0)
        {
            module.Write(path, new WriterParameters { WriteSymbols = hasSymbols });
        }

        return count;
    }

    /// <summary>Makes the body start by throwing <see cref="InvalidOperationException"/>.</summary>
    /// <param name="body">Body with short forms expanded.</param>
    /// <param name="constructor">Exception constructor taking a message.</param>
    /// <param name="target">The mutated member, for the message.</param>
    private static bool Throw(MethodBody body, MethodReference constructor, CoveredMethod target)
    {
        var il = body.GetILProcessor();
        var first = body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, $"{Message}: {target.TypeName}.{target.MemberName}"));
        il.InsertBefore(first, il.Create(OpCodes.Newobj, constructor));
        il.InsertBefore(first, il.Create(OpCodes.Throw));
        return true;
    }

    /// <summary>
    /// Makes the body start by returning the default of the return type (a fresh local), or by
    /// returning at once when it is void. By-reference returns are left alone: they have no default.
    /// </summary>
    /// <param name="method">Method with short forms expanded.</param>
    private static bool ReturnDefault(MethodDefinition method)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        var first = body.Instructions[0];
        var returnType = method.ReturnType;
        if (returnType.IsByReference)
        {
            return false;
        }

        if (returnType.MetadataType != MetadataType.Void)
        {
            var local = new VariableDefinition(returnType);
            body.Variables.Add(local);
            il.InsertBefore(first, il.Create(OpCodes.Ldloca, local));
            il.InsertBefore(first, il.Create(OpCodes.Initobj, returnType));
            il.InsertBefore(first, il.Create(OpCodes.Ldloc, local));
        }

        il.InsertBefore(first, il.Create(OpCodes.Ret));
        return true;
    }

    /// <summary>Inverts the first two-way conditional branch; false when the body has none.</summary>
    /// <param name="body">Body with short forms expanded.</param>
    private static bool FlipFirstBranch(MethodBody body)
    {
        foreach (var instruction in body.Instructions)
        {
            var inverted = instruction.OpCode.Code switch
            {
                Code.Brtrue => OpCodes.Brfalse,
                Code.Brfalse => OpCodes.Brtrue,
                Code.Beq => OpCodes.Bne_Un,
                Code.Bne_Un => OpCodes.Beq,
                _ => (OpCode?)null,
            };

            if (inverted is { } opCode)
            {
                instruction.OpCode = opCode;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Changes the constant stored into the field named <paramref name="target"/> by the type's
    /// constructors: an integer by one (booleans flip), a floating-point number by one, a string gets
    /// a suffix. Returns the number of stores changed; 0 when no constructor stores a constant.
    /// </summary>
    /// <param name="type">Type that may declare the field.</param>
    /// <param name="target">Declaring type and field name.</param>
    private static int ChangeFieldValue(TypeDefinition type, CoveredMethod target)
    {
        if (MethodNames.TypeName(type) != target.TypeName || type.Fields.FirstOrDefault(f => f.Name == target.MemberName) is not { } field)
        {
            return 0;
        }

        var count = 0;
        foreach (var constructor in type.Methods.Where(static m => m.IsConstructor && m.HasBody))
        {
            constructor.Body.SimplifyMacros();
            foreach (var store in constructor.Body.Instructions)
            {
                if (store.OpCode.Code is not (Code.Stsfld or Code.Stfld) || store.Operand is not FieldReference stored
                    || stored.Resolve() != field || store.Previous is not { } value)
                {
                    continue;
                }

                count += ChangeConstant(value, field.FieldType) ? 1 : 0;
            }

            constructor.Body.OptimizeMacros();
        }

        return count;
    }

    /// <summary>Changes a constant-loading instruction in place; false when it loads something else.</summary>
    /// <param name="value">Instruction before the store.</param>
    /// <param name="fieldType">Type of the field, to flip booleans instead of adding one.</param>
    private static bool ChangeConstant(Instruction value, TypeReference fieldType)
    {
        switch (value.OpCode.Code)
        {
            case Code.Ldc_I4 when value.Operand is int number:
                value.Operand = fieldType.MetadataType == MetadataType.Boolean ? (number == 0 ? 1 : 0) : number + 1;
                return true;

            case Code.Ldc_I8 when value.Operand is long number:
                value.Operand = number + 1;
                return true;

            case Code.Ldc_R4 when value.Operand is float number:
                value.Operand = number + 1;
                return true;

            case Code.Ldc_R8 when value.Operand is double number:
                value.Operand = number + 1;
                return true;

            case Code.Ldstr when value.Operand is string text:
                value.Operand = text + "~mutant";
                return true;

            default:
                return false;
        }
    }
}
