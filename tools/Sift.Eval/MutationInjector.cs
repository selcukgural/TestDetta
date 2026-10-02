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
    public void Dispose() => MutationInjector.RestoreAll(Backups);
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

/// <summary>A mutant of a schema: its switch value, member and kind.</summary>
/// <param name="Id">Value of <c>SIFT_MUTANT</c> that turns it on; positive.</param>
/// <param name="Member">Member (or field) to break.</param>
/// <param name="Kind">How to break it.</param>
internal sealed record SchemaTarget(int Id, CoveredMethod Member, MutantKind Kind);

/// <summary>What a schema did for one of its mutants.</summary>
/// <param name="MutatedMethods">Compiled methods (or field stores) the mutant changes.</param>
/// <param name="Assemblies">Rewritten assembly files that contain it.</param>
internal sealed record SchemaEntry(int MutatedMethods, IReadOnlySet<string> Assemblies);

/// <summary>
/// Assemblies rewritten with every mutant of a sample behind a switch (see
/// <see cref="MutationInjector.InjectSchema"/>), and how to put them back.
/// </summary>
/// <param name="Backups">Rewritten file (assembly or its .pdb) to its backup copy.</param>
/// <param name="Entries">Per mutant id, what it changed; ids that changed nothing are absent.</param>
internal sealed record Schema(IReadOnlyDictionary<string, string> Backups, IReadOnlyDictionary<int, SchemaEntry> Entries) : IDisposable
{
    /// <summary>Environment variable a test process reads to turn one mutant on.</summary>
    public const string Variable = "SIFT_MUTANT";

    /// <summary>Copies the originals back.</summary>
    public void Dispose() => MutationInjector.RestoreAll(Backups);
}

/// <summary>
/// Makes a source member fail: by default every compiled method that belongs to it (its overloads,
/// and the lambdas, iterators and async state machines it contains) starts by throwing; other
/// <see cref="MutantKind"/>s return a default, invert a branch or change a field's initial value. Working on the
/// compiled assemblies keeps the mutant independent of the member's syntax and of warnings-as-errors.
/// A mutant is either written in unconditionally (<see cref="Inject"/>), or a whole sample is written
/// in once behind a switch (<see cref="InjectSchema"/>) so that every test process picks its mutant
/// from <see cref="Schema.Variable"/>; both produce the same change once switched on.
/// </summary>
internal static class MutationInjector
{
    /// <summary>Message of the injected exception, so a failure can be traced to the mutant.</summary>
    private const string Message = "Sift mutant";

    /// <summary>Name of the switch type added to every assembly a schema rewrites.</summary>
    private const string SwitchTypeName = "<SiftMutantSwitch>";

    /// <summary>Rewrites every copy of the assembly that declares <paramref name="target"/>.</summary>
    /// <param name="assemblies">Copies of the assembly in the test outputs.</param>
    /// <param name="target">Member (or field) to break.</param>
    /// <param name="backupDirectory">Where the originals are copied first.</param>
    /// <param name="kind">How to break it.</param>
    public static Mutation Inject(IReadOnlyList<string> assemblies, CoveredMethod target, string backupDirectory, MutantKind kind = MutantKind.Throw)
    {
        var (backups, entries) = Rewrite(assemblies, [new SchemaTarget(1, target, kind)], backupDirectory, guarded: false);
        return new Mutation(backups, entries.TryGetValue(1, out var entry) ? entry.MutatedMethods : 0);
    }

    /// <summary>
    /// Rewrites every copy of the assemblies once with all <paramref name="targets"/>, each behind
    /// <c>if (switch == id)</c>, the switch being read once per process from
    /// <see cref="Schema.Variable"/>. Without the variable every method behaves as before.
    /// </summary>
    /// <param name="assemblies">Copies of the repository's assemblies in the test outputs.</param>
    /// <param name="targets">Mutants, with distinct positive ids.</param>
    /// <param name="backupDirectory">Where the originals are copied first.</param>
    public static Schema InjectSchema(IReadOnlyList<string> assemblies, IReadOnlyList<SchemaTarget> targets, string backupDirectory)
    {
        if (targets.Any(static t => t.Id <= 0) || targets.Select(static t => t.Id).Distinct().Count() != targets.Count)
        {
            throw new ArgumentException("Schema mutant ids must be distinct and positive.", nameof(targets));
        }

        var (backups, entries) = Rewrite(assemblies, targets, backupDirectory, guarded: true);
        return new Schema(backups, entries);
    }

    /// <summary>Copies every backup over its original and deletes the backup.</summary>
    /// <param name="backups">Rewritten file to its backup copy.</param>
    internal static void RestoreAll(IReadOnlyDictionary<string, string> backups)
    {
        foreach (var (original, backup) in backups)
        {
            File.Copy(backup, original, overwrite: true);
            File.Delete(backup);
        }
    }

    /// <summary>
    /// Backs up and rewrites each assembly (and its .pdb) that declares one of the targets; restores
    /// everything already rewritten when one of them fails.
    /// </summary>
    /// <param name="assemblies">Assembly copies.</param>
    /// <param name="targets">Mutants.</param>
    /// <param name="backupDirectory">Where the originals are copied first.</param>
    /// <param name="guarded">True to put each mutant behind the switch.</param>
    private static (Dictionary<string, string> Backups, Dictionary<int, SchemaEntry> Entries) Rewrite(
        IReadOnlyList<string> assemblies,
        IReadOnlyList<SchemaTarget> targets,
        string backupDirectory,
        bool guarded)
    {
        Directory.CreateDirectory(backupDirectory);
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var counts = new Dictionary<int, int>();
        var copies = new Dictionary<int, HashSet<string>>();
        try
        {
            foreach (var assembly in assemblies)
            {
                var backup = Path.Combine(backupDirectory, $"{backups.Count}-{Path.GetFileName(assembly)}");
                File.Copy(assembly, backup, overwrite: true);

                // The .pdb is rewritten with the assembly (see RewriteOne), so it is backed up and lifted too.
                var pdb = Path.ChangeExtension(assembly, ".pdb");
                var pdbBackup = File.Exists(pdb) ? Path.ChangeExtension(backup, ".pdb") : null;
                if (pdbBackup is not null)
                {
                    File.Copy(pdb, pdbBackup, overwrite: true);
                }

                var changed = RewriteOne(assembly, targets, guarded);
                if (changed.Count == 0)
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

                foreach (var (id, count) in changed)
                {
                    counts[id] = counts.GetValueOrDefault(id) + count;
                    if (!copies.TryGetValue(id, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        copies[id] = set;
                    }

                    set.Add(assembly);
                }
            }
        }
        catch
        {
            RestoreAll(backups);
            throw;
        }

        var entries = counts.ToDictionary(static c => c.Key, c => new SchemaEntry(c.Value, copies[c.Key]));
        return (backups, entries);
    }

    /// <summary>References a rewritten module needs.</summary>
    /// <param name="ExceptionConstructor"><c>InvalidOperationException(string)</c>.</param>
    /// <param name="Active">The switch field, or null when mutants are written in unconditionally.</param>
    private sealed record Emitters(MethodReference ExceptionConstructor, FieldReference? Active);

    /// <summary>Rewrites one assembly; returns, per mutant id, the number of methods (or field stores) changed.</summary>
    /// <param name="path">Assembly file.</param>
    /// <param name="targets">Mutants.</param>
    /// <param name="guarded">True to put each mutant behind the switch.</param>
    private static Dictionary<int, int> RewriteOne(string path, IReadOnlyList<SchemaTarget> targets, bool guarded)
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

        var byMember = targets.ToLookup(static t => t.Member);
        var fieldTargets = targets.Where(static t => t.Kind == MutantKind.FieldValue).ToList();
        var types = module.GetTypes().Where(static t => !MethodNames.IsCompilerInfrastructure(t)).ToList();

        // The switch is added first, so the guards can name it; it is removed again if nothing changed.
        var switchType = guarded ? AddSwitch(module) : null;
        var emitters = new Emitters(constructor, switchType?.Fields[0]);

        var changed = new Dictionary<int, int>();
        foreach (var type in types)
        {
            foreach (var target in fieldTargets)
            {
                var stores = ChangeFieldValue(type, target, emitters);
                if (stores > 0)
                {
                    changed[target.Id] = changed.GetValueOrDefault(target.Id) + stores;
                }
            }

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body.Instructions.Count == 0)
                {
                    continue;
                }

                foreach (var target in byMember[MethodNames.Map(method)])
                {
                    if (target.Kind == MutantKind.FieldValue)
                    {
                        continue;
                    }

                    // A value or branch change inside a state machine or closure breaks the machinery
                    // (an await that never completes) rather than the member's logic.
                    if (target.Kind != MutantKind.Throw && method.DeclaringType.Name.StartsWith('<'))
                    {
                        continue;
                    }

                    var body = method.Body;
                    body.SimplifyMacros();
                    var done = target.Kind switch
                    {
                        MutantKind.ReturnDefault => ReturnDefault(method, emitters, target.Id),
                        MutantKind.FlipBranch => FlipFirstBranch(body, emitters, target.Id),
                        _ => Throw(body, emitters, target),
                    };

                    body.OptimizeMacros();
                    if (done)
                    {
                        changed[target.Id] = changed.GetValueOrDefault(target.Id) + 1;
                    }
                }
            }
        }

        if (changed.Count == 0)
        {
            return changed;
        }

        module.Write(path, new WriterParameters { WriteSymbols = hasSymbols });
        return changed;
    }

    /// <summary>
    /// Adds <c>internal static class &lt;SiftMutantSwitch&gt; { static readonly int Active; }</c>, whose
    /// static constructor sets <c>Active</c> from <see cref="Schema.Variable"/> (0 when missing or not a
    /// number). Read once per process, the guard is one static field compare.
    /// </summary>
    /// <param name="module">Module to add it to.</param>
    private static TypeDefinition AddSwitch(ModuleDefinition module)
    {
        var type = new TypeDefinition(
            string.Empty,
            SwitchTypeName,
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class,
            module.TypeSystem.Object);
        var active = new FieldDefinition("Active", FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly, module.TypeSystem.Int32);
        type.Fields.Add(active);

        var environment = new TypeReference("System", nameof(Environment), module, module.TypeSystem.CoreLibrary);
        var getVariable = new MethodReference(nameof(Environment.GetEnvironmentVariable), module.TypeSystem.String, environment);
        getVariable.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var tryParse = new MethodReference(nameof(int.TryParse), module.TypeSystem.Boolean, module.TypeSystem.Int32);
        tryParse.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        tryParse.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.TypeSystem.Int32)) { IsOut = true });

        var cctor = new MethodDefinition(
            ".cctor",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var il = cctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, Schema.Variable);
        il.Emit(OpCodes.Call, getVariable);
        il.Emit(OpCodes.Ldsflda, active);
        il.Emit(OpCodes.Call, tryParse);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(cctor);

        module.Types.Add(type);
        return type;
    }

    /// <summary>
    /// Inserts <paramref name="mutation"/> before <paramref name="first"/>, behind the switch when the
    /// emitters have one: <c>ldsfld Active; ldc id; bne.un first; mutation</c>. Nothing that pointed at
    /// <paramref name="first"/> moves: the code runs once on entry, and a try block that starts there
    /// stays after it, so the method's own handlers cannot catch the mutant's exception (as always).
    /// </summary>
    /// <param name="body">Body with short forms expanded.</param>
    /// <param name="first">Instruction the mutation goes before.</param>
    /// <param name="emitters">References of the module.</param>
    /// <param name="id">Mutant id.</param>
    /// <param name="mutation">Instructions of the mutation.</param>
    private static void InsertGuarded(MethodBody body, Instruction first, Emitters emitters, int id, IReadOnlyList<Instruction> mutation)
    {
        var il = body.GetILProcessor();
        var inserted = new List<Instruction>();
        if (emitters.Active is { } active)
        {
            inserted.Add(il.Create(OpCodes.Ldsfld, active));
            inserted.Add(il.Create(OpCodes.Ldc_I4, id));
            inserted.Add(il.Create(OpCodes.Bne_Un, first));
        }

        inserted.AddRange(mutation);
        foreach (var instruction in inserted)
        {
            il.InsertBefore(first, instruction);
        }
    }

    /// <summary>Makes the body start by throwing <see cref="InvalidOperationException"/>.</summary>
    /// <param name="body">Body with short forms expanded.</param>
    /// <param name="emitters">References of the module.</param>
    /// <param name="target">The mutant, for the message and the switch.</param>
    private static bool Throw(MethodBody body, Emitters emitters, SchemaTarget target)
    {
        var il = body.GetILProcessor();
        InsertGuarded(body, body.Instructions[0], emitters, target.Id,
        [
            il.Create(OpCodes.Ldstr, $"{Message}: {target.Member.TypeName}.{target.Member.MemberName}"),
            il.Create(OpCodes.Newobj, emitters.ExceptionConstructor),
            il.Create(OpCodes.Throw),
        ]);
        return true;
    }

    /// <summary>
    /// Makes the body start by returning the default of the return type (a fresh local), or by
    /// returning at once when it is void. By-reference returns are left alone: they have no default.
    /// </summary>
    /// <param name="method">Method with short forms expanded.</param>
    /// <param name="emitters">References of the module.</param>
    /// <param name="id">Mutant id.</param>
    private static bool ReturnDefault(MethodDefinition method, Emitters emitters, int id)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        var returnType = method.ReturnType;
        if (returnType.IsByReference)
        {
            return false;
        }

        var mutation = new List<Instruction>();
        if (returnType.MetadataType != MetadataType.Void)
        {
            var local = new VariableDefinition(returnType);
            body.Variables.Add(local);
            mutation.Add(il.Create(OpCodes.Ldloca, local));
            mutation.Add(il.Create(OpCodes.Initobj, returnType));
            mutation.Add(il.Create(OpCodes.Ldloc, local));
        }

        mutation.Add(il.Create(OpCodes.Ret));
        InsertGuarded(body, body.Instructions[0], emitters, id, mutation);
        return true;
    }

    /// <summary>
    /// Inverts the first two-way conditional branch; false when the body has none. Behind the switch
    /// the inverted branch is a second copy taken only when the mutant is on; its operands are already
    /// on the stack, so both copies see the same values and no operand type matters.
    /// </summary>
    /// <param name="body">Body with short forms expanded.</param>
    /// <param name="emitters">References of the module.</param>
    /// <param name="id">Mutant id.</param>
    private static bool FlipFirstBranch(MethodBody body, Emitters emitters, int id)
    {
        foreach (var branch in body.Instructions)
        {
            if (Inverse(branch.OpCode) is not { } inverted)
            {
                continue;
            }

            if (emitters.Active is not { } active)
            {
                branch.OpCode = inverted;
                return true;
            }

            // ldsfld Active; ldc id; beq flipped; <original branch>; br next; flipped: <inverted branch>; next:
            var il = body.GetILProcessor();
            var next = branch.Next;
            var flipped = il.Create(inverted, (Instruction)branch.Operand);
            var check = il.Create(OpCodes.Ldsfld, active);
            var inserted = new List<Instruction> { check, il.Create(OpCodes.Ldc_I4, id), il.Create(OpCodes.Beq, flipped) };
            foreach (var instruction in inserted)
            {
                il.InsertBefore(branch, instruction);
            }

            var skip = il.Create(OpCodes.Br, next);
            il.InsertAfter(branch, skip);
            il.InsertAfter(skip, flipped);
            Retarget(body, branch, check, [.. inserted, skip, flipped]);
            return true;
        }

        return false;
    }

    /// <summary>The inverse of a two-way conditional branch, or null for any other instruction.</summary>
    /// <param name="opCode">Instruction's opcode, short forms expanded.</param>
    private static OpCode? Inverse(OpCode opCode) => opCode.Code switch
    {
        Code.Brtrue => OpCodes.Brfalse,
        Code.Brfalse => OpCodes.Brtrue,
        Code.Beq => OpCodes.Bne_Un,
        Code.Bne_Un => OpCodes.Beq,
        _ => null,
    };

    /// <summary>
    /// Changes the constant stored into the target field by the type's constructors: an integer by
    /// one (booleans flip), a floating-point number by one, a string gets a suffix. Behind the switch
    /// the changed constant is loaded only when the mutant is on. Returns the number of stores changed;
    /// 0 when no constructor stores a constant.
    /// </summary>
    /// <param name="type">Type that may declare the field.</param>
    /// <param name="target">The mutant: declaring type and field name.</param>
    /// <param name="emitters">References of the module.</param>
    private static int ChangeFieldValue(TypeDefinition type, SchemaTarget target, Emitters emitters)
    {
        if (MethodNames.TypeName(type) != target.Member.TypeName || type.Fields.FirstOrDefault(f => f.Name == target.Member.MemberName) is not { } field)
        {
            return 0;
        }

        var count = 0;
        foreach (var constructor in type.Methods.Where(static m => m.IsConstructor && m.HasBody))
        {
            var body = constructor.Body;
            body.SimplifyMacros();
            foreach (var store in body.Instructions.ToList())
            {
                if (store.OpCode.Code is not (Code.Stsfld or Code.Stfld) || store.Operand is not FieldReference stored
                    || stored.Resolve() != field || store.Previous is not { } value
                    || ChangedConstant(value, field.FieldType) is not { } operand)
                {
                    continue;
                }

                if (emitters.Active is not { } active)
                {
                    value.Operand = operand;
                    count++;
                    continue;
                }

                // ldsfld Active; ldc id; beq changed; <original constant>; br store; changed: <changed constant>; store:
                var il = body.GetILProcessor();
                var changed = Instruction.Create(OpCodes.Nop);
                changed.OpCode = value.OpCode;
                changed.Operand = operand;
                var check = il.Create(OpCodes.Ldsfld, active);
                var inserted = new List<Instruction> { check, il.Create(OpCodes.Ldc_I4, target.Id), il.Create(OpCodes.Beq, changed) };
                foreach (var instruction in inserted)
                {
                    il.InsertBefore(value, instruction);
                }

                var skip = il.Create(OpCodes.Br, store);
                il.InsertAfter(value, skip);
                il.InsertAfter(skip, changed);
                Retarget(body, value, check, [.. inserted, skip, changed]);
                count++;
            }

            body.OptimizeMacros();
        }

        return count;
    }

    /// <summary>The changed operand of a constant-loading instruction; null when it loads something else.</summary>
    /// <param name="value">Instruction before the store.</param>
    /// <param name="fieldType">Type of the field, to flip booleans instead of adding one.</param>
    private static object? ChangedConstant(Instruction value, TypeReference fieldType) => value.OpCode.Code switch
    {
        Code.Ldc_I4 when value.Operand is int number => fieldType.MetadataType == MetadataType.Boolean ? (number == 0 ? 1 : 0) : number + 1,
        Code.Ldc_I8 when value.Operand is long number => number + 1,
        Code.Ldc_R4 when value.Operand is float number => number + 1,
        Code.Ldc_R8 when value.Operand is double number => number + 1,
        Code.Ldstr when value.Operand is string text => text + "~mutant",
        _ => null,
    };

    /// <summary>
    /// Points branches, switch tables and exception handler boundaries at <paramref name="to"/>
    /// instead of <paramref name="from"/>, except the inserted instructions themselves, whose own
    /// targets (the guard's skip to the original) must stay.
    /// </summary>
    /// <param name="body">Method body.</param>
    /// <param name="from">Instruction that was a target.</param>
    /// <param name="to">First inserted instruction.</param>
    /// <param name="inserted">The instructions inserted before or around <paramref name="from"/>.</param>
    private static void Retarget(MethodBody body, Instruction from, Instruction to, IReadOnlyCollection<Instruction> inserted)
    {
        foreach (var instruction in body.Instructions)
        {
            if (inserted.Contains(instruction))
            {
                continue;
            }

            if (instruction.Operand == from)
            {
                instruction.Operand = to;
            }
            else if (instruction.Operand is Instruction[] targets)
            {
                for (var i = 0; i < targets.Length; i++)
                {
                    targets[i] = targets[i] == from ? to : targets[i];
                }
            }
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            handler.TryStart = handler.TryStart == from ? to : handler.TryStart;
            handler.TryEnd = handler.TryEnd == from ? to : handler.TryEnd;
            handler.HandlerStart = handler.HandlerStart == from ? to : handler.HandlerStart;
            handler.HandlerEnd = handler.HandlerEnd == from ? to : handler.HandlerEnd;
            handler.FilterStart = handler.FilterStart == from ? to : handler.FilterStart;
        }
    }
}
