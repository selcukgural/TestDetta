using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Sift.Core;
using Sift.Coverage.Runtime;

namespace Sift.Coverage;

/// <summary>Result of instrumenting a set of assemblies.</summary>
/// <param name="Methods">Instrumented methods; the index of each entry is the id passed to <see cref="SiftTracker.Hit"/>.</param>
/// <param name="Backups">Original file to backup copy, for <see cref="AssemblyInstrumenter.Restore"/>.</param>
internal sealed record InstrumentationResult(IReadOnlyList<CoveredMethod> Methods, IReadOnlyDictionary<string, string> Backups);

/// <summary>
/// Rewrites assemblies in place so that every method with a body starts with a call to
/// <see cref="SiftTracker.Hit"/>. In test assemblies, the entry points of test classes
/// (constructors, test and lifecycle methods) also tell the tracker which class is running.
/// Originals are backed up first and must be put back with <see cref="Restore"/>, also when the
/// test run fails.
/// </summary>
/// <param name="logger">Receives per-assembly counts.</param>
internal sealed partial class AssemblyInstrumenter(ILogger logger)
{
    /// <summary>Attribute names (without the <c>Attribute</c> suffix) that mark test methods in xUnit, NUnit and MSTest.</summary>
    private static readonly FrozenSet<string> TestAttributes = FrozenSet.Create(
        StringComparer.Ordinal,
        "Fact", "Theory", "SkippableFact", "SkippableTheory", "Test", "TestCase", "TestCaseSource",
        "TestMethod", "DataTestMethod");

    /// <summary>Lifecycle methods that frameworks call on a test class instance without an attribute.</summary>
    private static readonly FrozenSet<string> LifecycleMethods =
        FrozenSet.Create(StringComparer.Ordinal, "Dispose", "DisposeAsync", "InitializeAsync");

    /// <summary>
    /// The newest version directory of every shared framework (Microsoft.NETCore.App,
    /// Microsoft.AspNetCore.App, ...) of the dotnet installation Sift runs on.
    /// </summary>
    internal static readonly Lazy<List<string>> SharedFrameworkDirectories = new(FindSharedFrameworkDirectories);

    /// <summary>Instruments <paramref name="assemblies"/>, numbering methods across all of them.</summary>
    /// <param name="assemblies">Paths of the assemblies to rewrite.</param>
    /// <param name="testAssemblies">The subset of <paramref name="assemblies"/> that contain tests.</param>
    /// <param name="backupDirectory">Where the originals (and their .pdb files) are copied first.</param>
    public InstrumentationResult Instrument(
        IReadOnlyList<string> assemblies,
        IReadOnlySet<string> testAssemblies,
        string backupDirectory)
    {
        var methods = new List<CoveredMethod>();
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        Directory.CreateDirectory(backupDirectory);

        var result = new InstrumentationResult(methods, backups);
        try
        {
            foreach (var assembly in assemblies)
            {
                BackUp(assembly, backupDirectory, backups);
                BackUp(Path.ChangeExtension(assembly, ".pdb"), backupDirectory, backups);

                var before = methods.Count;
                InstrumentOne(assembly, testAssemblies.Contains(assembly), methods);
                LogInstrumented(assembly, methods.Count - before);
            }
        }
        catch
        {
            // Never leave a half-instrumented output behind: the caller only restores what it got back.
            Restore(result);
            throw;
        }

        return result;
    }

    /// <summary>Lists the shared framework directories next to the runtime Sift itself runs on.</summary>
    private static List<string> FindSharedFrameworkDirectories()
    {
        // .../dotnet/shared/Microsoft.NETCore.App/10.0.x/System.Private.CoreLib.dll -> .../dotnet/shared
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var shared = runtimeDirectory is null ? null : Directory.GetParent(runtimeDirectory)?.Parent;
        if (shared is null || !shared.Exists)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var framework in shared.EnumerateDirectories())
        {
            var newest = framework.EnumerateDirectories()
                .Select(static d => (Directory: d.FullName, Version: Version.TryParse(d.Name.Split('-')[0], out var v) ? v : null))
                .Where(static d => d.Version is not null)
                .MaxBy(static d => d.Version);

            if (newest.Directory is not null)
            {
                result.Add(newest.Directory);
            }
        }

        return result;
    }

    /// <summary>Copies every backed-up file back over its instrumented version.</summary>
    /// <param name="result">Result of <see cref="Instrument"/>.</param>
    public void Restore(InstrumentationResult result)
    {
        foreach (var (original, backup) in result.Backups)
        {
            File.Copy(backup, original, overwrite: true);
        }

        LogRestored(result.Backups.Count);
    }

    /// <summary>Copies <paramref name="file"/> into <paramref name="backupDirectory"/> when it exists.</summary>
    /// <param name="file">File to back up.</param>
    /// <param name="backupDirectory">Backup directory.</param>
    /// <param name="backups">Original to backup mapping; updated in place.</param>
    private static void BackUp(string file, string backupDirectory, Dictionary<string, string> backups)
    {
        if (!File.Exists(file))
        {
            return;
        }

        var backup = Path.Combine(backupDirectory, $"{backups.Count}-{Path.GetFileName(file)}");
        File.Copy(file, backup, overwrite: true);
        backups[file] = backup;
    }

    /// <summary>Rewrites one assembly, appending its methods to <paramref name="methods"/>.</summary>
    /// <param name="path">Assembly file.</param>
    /// <param name="containsTests">True for a test assembly, whose test classes get entry markers.</param>
    /// <param name="methods">Method table shared by all assemblies; updated in place.</param>
    private static void InstrumentOne(string path, bool containsTests, List<CoveredMethod> methods)
    {
        var hasSymbols = File.Exists(Path.ChangeExtension(path, ".pdb"));
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);

        // Framework assemblies (System.Threading.RateLimiting, Microsoft.AspNetCore.*) live in the
        // shared framework, not in the test output, and Cecil resolves some of them while writing.
        foreach (var directory in SharedFrameworkDirectories.Value)
        {
            resolver.AddSearchDirectory(directory);
        }

        // InMemory so the file is not locked while it is written back to the same path.
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = hasSymbols,
            AssemblyResolver = resolver,
        });

        var probes = new Probes(
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.Hit))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterTestInstance))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterTestClass))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterFixtureInstance))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterTypeInitializer))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.ExitTypeInitializer))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterCache))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.ExitCache))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterAmbient))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterSetUpScope))!));
        var fixtures = containsTests ? FixtureTypes(module) : [];
        var scopes = containsTests ? SetUpScopes(module) : [];
        var dataSources = containsTests ? DataSources.Find(module, static type => IsTestClass(type, depth: 0)) : [];

        foreach (var type in module.GetTypes())
        {
            // Top-level compiler infrastructure (<PrivateImplementationDetails>, <>z__ReadOnlyArray`1)
            // has no source member a change could name.
            if (MethodNames.IsCompilerInfrastructure(type))
            {
                continue;
            }

            var isTestClass = containsTests && IsTestClass(type, depth: 0);
            var isFixture = fixtures.Contains(type);

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body.Instructions.Count == 0)
                {
                    continue;
                }

                var id = methods.Count;
                methods.Add(MethodNames.Map(method));
                var dataUser = dataSources.GetValueOrDefault(method);
                // A data source serves the classes that name it, whatever class declares it.
                var scope = scopes.GetValueOrDefault(method);
                var marker = scope is not null ? Marker.SetUpScope
                    : dataSources.ContainsKey(method) ? Marker.DataSource
                    : isTestClass && IsTestClassEntryPoint(method) ? Marker.TestClass
                    : isFixture && IsFixtureSetup(method) ? Marker.Fixture
                    : Marker.None;
                InsertProbes(method, probes, new ProbeSite(id, marker, scope ?? dataUser));
            }
        }

        module.Write(path, new WriterParameters { WriteSymbols = hasSymbols });
    }

    /// <summary>Imported references to the tracker methods the probes call.</summary>
    /// <param name="Hit"><see cref="SiftTracker.Hit"/>.</param>
    /// <param name="EnterTestInstance"><see cref="SiftTracker.EnterTestInstance"/>.</param>
    /// <param name="EnterTestClass"><see cref="SiftTracker.EnterTestClass"/>.</param>
    /// <param name="EnterFixtureInstance"><see cref="SiftTracker.EnterFixtureInstance"/>.</param>
    /// <param name="EnterTypeInitializer"><see cref="SiftTracker.EnterTypeInitializer"/>.</param>
    /// <param name="ExitTypeInitializer"><see cref="SiftTracker.ExitTypeInitializer"/>.</param>
    /// <param name="EnterCache"><see cref="SiftTracker.EnterCache"/>.</param>
    /// <param name="ExitCache"><see cref="SiftTracker.ExitCache"/>.</param>
    /// <param name="EnterAmbient"><see cref="SiftTracker.EnterAmbient"/>.</param>
    /// <param name="EnterSetUpScope"><see cref="SiftTracker.EnterSetUpScope"/>.</param>
    private sealed record Probes(
        MethodReference Hit,
        MethodReference EnterTestInstance,
        MethodReference EnterTestClass,
        MethodReference EnterFixtureInstance,
        MethodReference EnterTypeInitializer,
        MethodReference ExitTypeInitializer,
        MethodReference EnterCache,
        MethodReference ExitCache,
        MethodReference EnterAmbient,
        MethodReference EnterSetUpScope);

    /// <summary>What the probes of one method record.</summary>
    /// <param name="Id">Method id.</param>
    /// <param name="Marker">Bucket marker to insert, if any.</param>
    /// <param name="DataUser">
    /// For a data source, the runtime name of the test class it serves; null when it serves several,
    /// and its code then counts as running outside every test. For a setup scope, its fixture name.
    /// </param>
    private readonly record struct ProbeSite(int Id, Marker Marker, string? DataUser);

    /// <summary>Which bucket marker, if any, a method starts with.</summary>
    private enum Marker
    {
        /// <summary>Only the hit probe.</summary>
        None,

        /// <summary>Entry point of a test class.</summary>
        TestClass,

        /// <summary>Setup of a test fixture.</summary>
        Fixture,

        /// <summary>A member that supplies test data, see <see cref="DataSources"/>.</summary>
        DataSource,

        /// <summary>Setup scoped to a namespace or assembly, see <see cref="SetUpScopes"/>.</summary>
        SetUpScope,
    }

    /// <summary>
    /// Inserts the probes before the first instruction: for a test class entry point or fixture
    /// setup first the bucket marker (so the hit already counts for that bucket), then
    /// <c>ldc.i4 id; call Hit</c>.
    /// </summary>
    /// <param name="method">Method to instrument.</param>
    /// <param name="probes">Tracker references.</param>
    /// <param name="site">Method id and marker.</param>
    /// <remarks>
    /// Short branch forms are expanded before and re-optimized after the insertion, so offsets that
    /// no longer fit in a byte do not corrupt the method. In a constructor the marker runs before
    /// the base constructor call; it only reads the object's type, which is set at allocation.
    /// </remarks>
    private static void InsertProbes(MethodDefinition method, Probes probes, ProbeSite site)
    {
        var body = method.Body;
        body.SimplifyMacros();

        var il = body.GetILProcessor();
        var first = body.Instructions[0];

        switch (site.Marker)
        {
            case Marker.DataSource when site.DataUser is { } user:
                il.InsertBefore(first, il.Create(OpCodes.Ldstr, user));
                il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterTestClass));
                break;

            case Marker.DataSource:
                il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterAmbient));
                break;

            case Marker.TestClass when method.IsStatic:
                il.InsertBefore(first, il.Create(OpCodes.Ldstr, method.DeclaringType.FullName.Replace('/', '+')));
                il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterTestClass));
                break;

            case Marker.TestClass:
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterTestInstance));
                break;

            case Marker.Fixture:
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterFixtureInstance));
                break;

            case Marker.SetUpScope when site.DataUser is { } scope:
                il.InsertBefore(first, il.Create(OpCodes.Ldstr, scope));
                il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterSetUpScope));
                break;
        }

        var isTypeInitializer = method.IsConstructor && method.IsStatic;
        if (isTypeInitializer)
        {
            il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterTypeInitializer));
        }

        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, site.Id));
        il.InsertBefore(first, il.Create(OpCodes.Call, probes.Hit));

        if (isTypeInitializer)
        {
            InsertBeforeReturns(body, il, probes.ExitTypeInitializer);
        }

        MarkCacheFills(body, il, probes);
        body.OptimizeMacros();
    }

    /// <summary>
    /// Methods that fill a cache through a factory on a miss and skip it on a hit, by declaring type
    /// (generic definition) and name. Code a factory runs is credited to the first test class that
    /// misses, so the tracker is told when such a call runs.
    /// </summary>
    private static readonly FrozenSet<string> CacheFills = FrozenSet.Create(
        StringComparer.Ordinal,
        "System.Collections.Concurrent.ConcurrentDictionary`2::GetOrAdd",
        "System.Collections.Concurrent.ConcurrentDictionary`2::AddOrUpdate",
        "System.Lazy`1::get_Value",
        "System.Threading.LazyInitializer::EnsureInitialized",
        "System.Runtime.CompilerServices.ConditionalWeakTable`2::GetValue",
        "System.Runtime.CompilerServices.ConditionalWeakTable`2::GetOrAdd",
        "Microsoft.Extensions.Caching.Memory.CacheExtensions::GetOrCreate",
        "Microsoft.Extensions.Caching.Memory.CacheExtensions::GetOrCreateAsync");

    /// <summary>
    /// True for <c>Create</c> on an object pool policy (<c>IPooledObjectPolicy&lt;T&gt;</c>,
    /// <c>PooledObjectPolicy&lt;T&gt;</c>), which a pool calls only when it is empty, so the code it
    /// runs is credited to the first class that empties the pool. Matched by type name in any
    /// namespace: libraries copy Microsoft.Extensions.ObjectPool (Meziantou.Analyzer's Internals).
    /// </summary>
    /// <param name="target">Called method.</param>
    private static bool IsPoolPolicyCreate(MethodReference target) =>
        target.Name == "Create" && target.DeclaringType.GetElementType().Name is "IPooledObjectPolicy`1" or "PooledObjectPolicy`1";

    /// <summary>
    /// Surrounds every call of <see cref="CacheFills"/> and <see cref="IsPoolPolicyCreate"/> with <see cref="SiftTracker.EnterCache"/> and
    /// <see cref="SiftTracker.ExitCache"/>. The calls take and leave nothing on the stack, so the call
    /// in between keeps its arguments and result. Branches and handler boundaries that pointed at the
    /// call move to the inserted enter. A call after a <c>tail.</c> or <c>constrained.</c> prefix is
    /// left alone: nothing may come between the prefix and its call, or after a tail call.
    /// </summary>
    /// <param name="body">Method body with short branches already expanded.</param>
    /// <param name="il">Processor of the body.</param>
    /// <param name="probes">Tracker references.</param>
    private static void MarkCacheFills(MethodBody body, ILProcessor il, Probes probes)
    {
        var calls = body.Instructions
            .Where(static i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                               && i.Operand is MethodReference target
                               && (CacheFills.Contains($"{target.DeclaringType.GetElementType().FullName}::{target.Name}") || IsPoolPolicyCreate(target))
                               && i.Previous?.OpCode.OpCodeType != OpCodeType.Prefix)
            .ToList();

        foreach (var call in calls)
        {
            var enter = il.Create(OpCodes.Call, probes.EnterCache);
            il.InsertBefore(call, enter);
            il.InsertAfter(call, il.Create(OpCodes.Call, probes.ExitCache));
            Retarget(body, call, enter);
        }

        // Hand-rolled lazy initialisation, Interlocked.CompareExchange(ref _x, new X(), null): the
        // value is built before the call, so the mark starts where its arguments start.
        var exchanges = body.Instructions
            .Where(static i => i.OpCode == OpCodes.Call && i.Operand is MethodReference target
                               && target.DeclaringType.FullName == "System.Threading.Interlocked" && target.Name == "CompareExchange"
                               && i.Previous?.OpCode.OpCodeType != OpCodeType.Prefix)
            .ToList();

        foreach (var exchange in exchanges)
        {
            if (ArgumentStart(body, exchange) is not { } start)
            {
                continue;
            }

            var enter = il.Create(OpCodes.Call, probes.EnterCache);
            il.InsertBefore(start, enter);
            il.InsertAfter(exchange, il.Create(OpCodes.Call, probes.ExitCache));
            Retarget(body, start, enter);
        }
    }

    /// <summary>
    /// The first instruction that pushes the arguments of <paramref name="call"/>, when they are built
    /// in straight-line code that constructs or calls something (a lazily built value, not a counter);
    /// null otherwise. Code with a branch, a branch target or a handler boundary inside is left alone.
    /// </summary>
    /// <param name="body">Method body with short branches already expanded.</param>
    /// <param name="call">Call whose arguments are wanted.</param>
    private static Instruction? ArgumentStart(MethodBody body, Instruction call)
    {
        var targets = new HashSet<Instruction>();
        foreach (var instruction in body.Instructions)
        {
            if (instruction.Operand is Instruction target)
            {
                targets.Add(target);
            }
            else if (instruction.Operand is Instruction[] table)
            {
                targets.UnionWith(table);
            }
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            targets.UnionWith(new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart }.OfType<Instruction>());
        }

        // A jump straight to the call would skip the enter mark but not the exit.
        if (targets.Contains(call))
        {
            return null;
        }

        var needed = Pops(call) ?? 0;
        var buildsValue = false;
        for (var (instruction, steps) = (call.Previous, 0); instruction is not null && steps < 64; (instruction, steps) = (instruction.Previous, steps + 1))
        {
            if (instruction.OpCode.FlowControl is not (FlowControl.Next or FlowControl.Call) || instruction.OpCode.OpCodeType == OpCodeType.Prefix
                || Pops(instruction) is not { } pops || Pushes(instruction) is not { } pushes)
            {
                return null;
            }

            buildsValue |= instruction.OpCode == OpCodes.Newobj || instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt;
            needed += pops - pushes;
            if (needed == 0)
            {
                return buildsValue ? instruction : null;
            }

            if (needed < 0 || targets.Contains(instruction))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Values <paramref name="instruction"/> takes off the stack; null when unknown (<c>ret</c>, <c>leave</c>).</summary>
    /// <param name="instruction">Instruction.</param>
    private static int? Pops(Instruction instruction) => instruction.OpCode.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.Varpop when instruction.Operand is MethodReference method =>
            method.Parameters.Count + (method.HasThis && instruction.OpCode != OpCodes.Newobj ? 1 : 0),
        _ => null,
    };

    /// <summary>Values <paramref name="instruction"/> leaves on the stack; null when unknown.</summary>
    /// <param name="instruction">Instruction.</param>
    private static int? Pushes(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Varpush when instruction.Operand is MethodReference method =>
            instruction.OpCode == OpCodes.Newobj || method.ReturnType.MetadataType != MetadataType.Void ? 1 : 0,
        _ => null,
    };

    /// <summary>Points branches, switch tables and exception handler boundaries at <paramref name="to"/> instead of <paramref name="from"/>.</summary>
    /// <param name="body">Method body.</param>
    /// <param name="from">Instruction that was a target.</param>
    /// <param name="to">Instruction inserted before it.</param>
    private static void Retarget(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var instruction in body.Instructions)
        {
            if (instruction.Operand == from && instruction != to)
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

    /// <summary>
    /// Inserts a call before every <c>ret</c> of <paramref name="body"/>. Branches, switch tables and
    /// exception handler boundaries that pointed at a <c>ret</c> are moved to the call, so no path
    /// returns without it.
    /// </summary>
    /// <param name="body">Method body with short branches already expanded.</param>
    /// <param name="il">Processor of the body.</param>
    /// <param name="target">Method to call; takes and returns nothing.</param>
    private static void InsertBeforeReturns(MethodBody body, ILProcessor il, MethodReference target)
    {
        var returns = body.Instructions.Where(static i => i.OpCode == OpCodes.Ret).ToList();
        foreach (var ret in returns)
        {
            var call = il.Create(OpCodes.Call, target);
            il.InsertBefore(ret, call);
            Retarget(body, ret, call);
        }
    }

    /// <summary>
    /// True for classes that declare test methods or inherit them from a base class in the same
    /// module. Abstract bases count too: their tests run with the derived instance as <c>this</c>.
    /// </summary>
    /// <param name="type">Type definition.</param>
    /// <param name="depth">Recursion depth; stops at 16 so a broken hierarchy cannot loop.</param>
    private static bool IsTestClass(TypeDefinition type, int depth)
    {
        if (!type.IsClass || type.IsValueType || type.Name.StartsWith('<') || depth >= 16)
        {
            return false;
        }

        foreach (var method in type.Methods)
        {
            if (HasTestAttribute(method))
            {
                return true;
            }
        }

        return type.BaseType is { } baseType
               && baseType.Module == type.Module
               && baseType.Resolve() is { } resolved
               && IsTestClass(resolved, depth + 1);
    }

    /// <summary>
    /// Types of the module used as xUnit class or collection fixtures (<c>IClassFixture&lt;T&gt;</c>,
    /// <c>ICollectionFixture&lt;T&gt;</c>, and <c>IAssemblyFixture&lt;T&gt;</c> of frameworks that add
    /// one), plus their base classes in the same module, whose constructors and <c>InitializeAsync</c>
    /// run as part of the fixture. The interface may be declared by a base class in another library,
    /// with the fixture as its type argument: FastEndpoints.Testing's <c>TestBase&lt;TAppFixture&gt;</c>
    /// implements <c>IClassFixture&lt;TAppFixture&gt;</c> for every test class derived from it.
    /// </summary>
    /// <param name="module">Test module.</param>
    private static HashSet<TypeDefinition> FixtureTypes(ModuleDefinition module)
    {
        var fixtures = new HashSet<TypeDefinition>();
        foreach (var type in module.GetTypes())
        {
            TypeReference? current = type;
            for (var depth = 0; current is not null && depth < 16; depth++)
            {
                if (TryResolve(current) is not { } definition)
                {
                    break;
                }

                foreach (var implementation in definition.Interfaces)
                {
                    if (implementation.InterfaceType is GenericInstanceType { ElementType.Name: "IClassFixture`1" or "ICollectionFixture`1" or "IAssemblyFixture`1" } fixture)
                    {
                        AddFixture(Bind(fixture.GenericArguments[0], current), module, fixtures);
                    }
                }

                current = definition.BaseType is { } baseType ? Bind(baseType, current) : null;
            }
        }

        return fixtures;
    }

    /// <summary>Adds the fixture type, when it is declared in <paramref name="module"/>, and its base classes there.</summary>
    /// <param name="argument">Type argument of the fixture interface, generic arguments already bound.</param>
    /// <param name="module">Test module.</param>
    /// <param name="fixtures">Fixture types found so far; updated in place.</param>
    private static void AddFixture(TypeReference argument, ModuleDefinition module, HashSet<TypeDefinition> fixtures)
    {
        var element = argument is GenericInstanceType generic ? generic.ElementType : argument;
        for (var definition = element.Module == module ? TryResolve(element) : null;
             definition is not null && fixtures.Add(definition);
             definition = definition.BaseType is { } baseType && baseType.Module == module ? TryResolve(baseType) : null)
        {
        }
    }

    /// <summary>
    /// <paramref name="type"/> with the generic parameters of the type it is declared on replaced by
    /// <paramref name="context"/>'s arguments: <c>IClassFixture&lt;TAppFixture&gt;</c> seen from
    /// <c>TestBase&lt;Fixture&gt;</c> is <c>IClassFixture&lt;Fixture&gt;</c>.
    /// </summary>
    /// <param name="type">A type as written inside a generic type definition.</param>
    /// <param name="context">The generic instance it is seen through; other references bind nothing.</param>
    private static TypeReference Bind(TypeReference type, TypeReference context)
    {
        if (context is not GenericInstanceType instance)
        {
            return type;
        }

        switch (type)
        {
            case GenericParameter { Type: GenericParameterType.Type } parameter when parameter.Position < instance.GenericArguments.Count:
                return instance.GenericArguments[parameter.Position];

            case GenericInstanceType generic:
                var bound = new GenericInstanceType(generic.ElementType);
                foreach (var argument in generic.GenericArguments)
                {
                    bound.GenericArguments.Add(Bind(argument, context));
                }

                return bound;

            default:
                return type;
        }
    }

    /// <summary>
    /// Setup methods whose failure fails a whole namespace or assembly, with the fixture name they
    /// record under: the constructor and <c>[OneTimeSetUp]</c> methods of an NUnit <c>[SetUpFixture]</c>
    /// (scoped to its namespace; none means the assembly), and MSTest <c>[AssemblyInitialize]</c>
    /// methods (the assembly). Their code would otherwise count as running outside every test, or for
    /// the one test class that declares it.
    /// </summary>
    /// <param name="module">Test module.</param>
    private static Dictionary<MethodDefinition, string> SetUpScopes(ModuleDefinition module)
    {
        var scopes = new Dictionary<MethodDefinition, string>();
        foreach (var type in module.GetTypes())
        {
            var outer = type;
            while (outer.DeclaringType is { } declaring)
            {
                outer = declaring;
            }

            var isSetUpFixture = HasAttribute(type.CustomAttributes, "NUnit.Framework.SetUpFixtureAttribute");
            foreach (var method in type.Methods.Where(static m => m.HasBody))
            {
                if (isSetUpFixture && ((method.IsConstructor && !method.IsStatic) || HasAttribute(method.CustomAttributes, "NUnit.Framework.OneTimeSetUpAttribute")))
                {
                    scopes[method] = CoverageMap.SetUpScopePrefix + outer.Namespace;
                }
                else if (HasAttribute(method.CustomAttributes, "Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyInitializeAttribute"))
                {
                    scopes[method] = CoverageMap.SetUpScopePrefix;
                }
            }
        }

        return scopes;
    }

    /// <summary>True when one of <paramref name="attributes"/> is of the type named <paramref name="fullName"/>.</summary>
    /// <param name="attributes">Custom attributes.</param>
    /// <param name="fullName">Full name of the attribute type.</param>
    private static bool HasAttribute(IEnumerable<CustomAttribute> attributes, string fullName) =>
        attributes.Any(a => a.AttributeType.FullName == fullName);

    /// <summary>Constructors and <c>InitializeAsync</c> of a fixture type: the code that sets the fixture up.</summary>
    /// <param name="method">Method of a fixture type.</param>
    private static bool IsFixtureSetup(MethodDefinition method) =>
        (method.IsConstructor && !method.IsStatic) || method.Name == "InitializeAsync";

    /// <summary>
    /// Methods a framework calls directly on a test class: constructors, attributed methods
    /// (tests, setup, teardown) and the xUnit/IDisposable lifecycle methods.
    /// </summary>
    /// <param name="method">Method of a test class.</param>
    private static bool IsTestClassEntryPoint(MethodDefinition method) =>
        (method.IsConstructor && !method.IsStatic) || method.HasCustomAttributes || LifecycleMethods.Contains(method.Name);

    /// <summary>True when the method has a test attribute, directly or through a custom attribute derived from one.</summary>
    /// <param name="method">Method definition.</param>
    private static bool HasTestAttribute(MethodDefinition method)
    {
        foreach (var attribute in method.CustomAttributes)
        {
            var type = attribute.AttributeType;
            for (var depth = 0; type is not null && depth < 8; depth++)
            {
                var name = type.Name.EndsWith("Attribute", StringComparison.Ordinal) ? type.Name[..^"Attribute".Length] : type.Name;
                if (TestAttributes.Contains(name))
                {
                    return true;
                }

                type = TryResolve(type)?.BaseType;
            }
        }

        return false;
    }

    /// <summary>Resolves a type reference, or null when its assembly cannot be found.</summary>
    /// <param name="type">Type reference.</param>
    private static TypeDefinition? TryResolve(TypeReference type)
    {
        try
        {
            return type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Instrumented {Assembly}: {Count} method(s)")]
    private partial void LogInstrumented(string assembly, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Restored {Count} original file(s)")]
    private partial void LogRestored(int count);
}
