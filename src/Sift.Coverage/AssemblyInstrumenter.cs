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
internal sealed record InstrumentationResult(IReadOnlyList<CoveredMethod> Methods, IReadOnlyDictionary<string, string> Backups)
{
    /// <summary>
    /// Tracked fields (see <c>AssemblyInstrumenter.TrackedFields</c>) by index, as <c>Type::Field</c>;
    /// field <c>i</c> has the bit <c>Methods.Count + i</c> in the tracker's buckets.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = [];
}

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
        var fields = new List<string>();
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        Directory.CreateDirectory(backupDirectory);

        var result = new InstrumentationResult(methods, backups) { Fields = fields };
        try
        {
            foreach (var assembly in assemblies)
            {
                BackUp(assembly, backupDirectory, backups);
                BackUp(Path.ChangeExtension(assembly, ".pdb"), backupDirectory, backups);

                var before = methods.Count;
                InstrumentOne(assembly, testAssemblies.Contains(assembly), methods, fields);
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
    /// <param name="fields">Tracked field table shared by all assemblies; updated in place.</param>
    private static void InstrumentOne(string path, bool containsTests, List<CoveredMethod> methods, List<string> fields)
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
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterSetUpScope))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterDataSource))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.Read))!),
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.Stored))!))
        {
            EnterTestCode = module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterTestCode))!),
            ExitTestCode = module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.ExitTestCode))!),
            ExitTestCodeWhenComplete = module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.ExitTestCodeWhenComplete))!),
            PrepareTestInstance = module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.PrepareTestInstance))!),
        };
        var tracked = new Dictionary<FieldDefinition, int>();
        foreach (var field in TrackedFields(module))
        {
            tracked[field] = fields.Count;
            fields.Add($"{field.DeclaringType.FullName}::{field.Name}");
        }

        var fixtures = containsTests ? FixtureTypes(module) : [];
        var scopes = containsTests ? SetUpScopes(module) : [];
        var dataSources = containsTests ? DataSources.Find(module, static type => IsTestClass(type, depth: 0)) : [];
        probes = probes with { ReusedCollections = ReusedCollections(module) };

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
                InsertFieldProbes(method, probes, id, tracked);
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
    /// <param name="EnterDataSource"><see cref="SiftTracker.EnterDataSource"/>.</param>
    /// <param name="Read"><see cref="SiftTracker.Read"/>.</param>
    /// <param name="Stored"><see cref="SiftTracker.Stored"/>.</param>
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
        MethodReference EnterSetUpScope,
        MethodReference EnterDataSource,
        MethodReference Read,
        MethodReference Stored)
    {
        /// <summary><see cref="SiftTracker.EnterTestCode"/>.</summary>
        public required MethodReference EnterTestCode { get; init; }

        /// <summary><see cref="SiftTracker.ExitTestCode"/>.</summary>
        public required MethodReference ExitTestCode { get; init; }

        /// <summary><see cref="SiftTracker.ExitTestCodeWhenComplete"/>.</summary>
        public required MethodReference ExitTestCodeWhenComplete { get; init; }

        /// <summary><see cref="SiftTracker.PrepareTestInstance"/>.</summary>
        public required MethodReference PrepareTestInstance { get; init; }

        /// <summary>Collection fields of the module that objects are taken back out of, with their element type; see <see cref="ReusedCollections"/>.</summary>
        public Dictionary<FieldDefinition, TypeReference> ReusedCollections { get; init; } = [];
    }

    /// <summary>
    /// Fields whose value may be built once and read by many tests: reference-typed (or generic)
    /// fields that are static, or that some method other than a constructor of their type stores
    /// (<c>_x ??= new X()</c>, a setter of a shared object). Fields only constructors store hold a
    /// per-object value that the constructor rules already cover, and tracking them would put a probe
    /// on every object built. Fields of compiler-generated types (closures, state machines) hold a
    /// flow's locals, and constants have no store.
    /// </summary>
    /// <param name="module">Module being instrumented.</param>
    private static List<FieldDefinition> TrackedFields(ModuleDefinition module)
    {
        var storedOutsideConstructors = new HashSet<FieldDefinition>();
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is Code.Stfld && instruction.Operand is FieldReference reference
                        && reference.Resolve() is { } field && field.Module == module
                        && !(method.IsConstructor && !method.IsStatic && field.DeclaringType == method.DeclaringType))
                    {
                        storedOutsideConstructors.Add(field);
                    }
                }
            }
        }

        var tracked = new List<FieldDefinition>();
        foreach (var type in module.GetTypes())
        {
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            foreach (var field in type.Fields)
            {
                if (!field.IsLiteral && MayHoldReference(field.FieldType) && (field.IsStatic || storedOutsideConstructors.Contains(field)))
                {
                    tracked.Add(field);
                }
            }
        }

        return tracked;
    }

    /// <summary>True for a type the compiler made (a closure, a state machine), or one nested in such a type.</summary>
    /// <param name="type">A type of the module.</param>
    private static bool IsCompilerGenerated(TypeDefinition type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            if (t.Name.StartsWith('<'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a field of this type holds a reference: a class, an array, a generic parameter (which
    /// may be one). A value type is copied, so storing it builds nothing others share. A type that
    /// does not resolve counts as a reference, so nothing is lost.
    /// </summary>
    /// <param name="type">Field type.</param>
    private static bool MayHoldReference(TypeReference type) => type switch
    {
        GenericParameter => true,
        ArrayType => true,
        PointerType or ByReferenceType => false,
        _ when type.IsPrimitive => false,
        _ => type.Resolve() is not { IsValueType: true },
    };

    /// <summary>
    /// After every read of a tracked field, <c>ldc.i4 field; call Read</c>; after every store,
    /// <c>ldc.i4 field; ldc.i4 method; call Stored</c>. Both go after the instruction: a branch that
    /// targets the next instruction did not pass the read or store, and the stored value is complete
    /// by then. Taking a field's address counts as a read.
    /// </summary>
    /// <param name="method">Method already given its entry probes.</param>
    /// <param name="probes">Tracker references.</param>
    /// <param name="methodId">Id of the method.</param>
    /// <param name="tracked">Tracked fields of the module with their index.</param>
    private static void InsertFieldProbes(MethodDefinition method, Probes probes, int methodId, Dictionary<FieldDefinition, int> tracked)
    {
        if (tracked.Count == 0)
        {
            return;
        }

        var body = method.Body;
        body.SimplifyMacros();
        var il = body.GetILProcessor();
        foreach (var instruction in body.Instructions.ToList())
        {
            var code = instruction.OpCode.Code;
            if (code is not (Code.Ldsfld or Code.Ldfld or Code.Ldsflda or Code.Ldflda or Code.Stsfld or Code.Stfld)
                || instruction.Operand is not FieldReference reference
                || reference.Resolve() is not { } field
                || !tracked.TryGetValue(field, out var index))
            {
                continue;
            }

            if (code is Code.Stsfld or Code.Stfld)
            {
                var call = il.Create(OpCodes.Call, probes.Stored);
                il.InsertAfter(instruction, call);
                il.InsertBefore(call, il.Create(OpCodes.Ldc_I4, index));
                il.InsertBefore(call, il.Create(OpCodes.Ldc_I4, methodId));
            }
            else
            {
                var call = il.Create(OpCodes.Call, probes.Read);
                il.InsertAfter(instruction, call);
                il.InsertBefore(call, il.Create(OpCodes.Ldc_I4, index));
            }
        }

        body.OptimizeMacros();
    }

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
                il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterDataSource));
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

        // Before the hit, so the entry point's own hit already counts as test code.
        var testCodeEnd = site.Marker == Marker.TestClass && !isTypeInitializer ? EndOf(method) : (TestCodeEnd?)null;
        if (testCodeEnd is { } beginning)
        {
            il.InsertBefore(first, il.Create(OpCodes.Call, beginning == TestCodeEnd.Unseen ? probes.PrepareTestInstance : probes.EnterTestCode));
        }

        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, site.Id));
        il.InsertBefore(first, il.Create(OpCodes.Call, probes.Hit));

        if (isTypeInitializer)
        {
            InsertBeforeReturns(body, il, probes.ExitTypeInitializer);
        }

        MarkCacheFills(body, il, probes, BindsPoolPolicyCreate(method.DeclaringType));
        MarkPoolFills(body, il, probes);
        if (testCodeEnd is TestCodeEnd.Return or TestCodeEnd.Task)
        {
            TrackTestCode(method, il, probes, first);
        }

        body.OptimizeMacros();
    }

    /// <summary>How the end of a test-class entry point is seen.</summary>
    private enum TestCodeEnd
    {
        /// <summary>When the method returns or throws (<c>finally</c>).</summary>
        Return,

        /// <summary>When the returned task completes; a throw before that ends it at once (<c>fault</c>).</summary>
        Task,

        /// <summary>
        /// Not at all: a constructor (its test method follows), or a method returning <c>ValueTask</c>,
        /// which may be awaited only once, or <c>async void</c>. The tracker counts test code as being
        /// prepared until the next tracked end.
        /// </summary>
        Unseen,
    }

    /// <summary>When the test code that <paramref name="method"/>, a test-class entry point, runs ends.</summary>
    /// <param name="method">Entry point.</param>
    private static TestCodeEnd EndOf(MethodDefinition method)
    {
        var returns = method.ReturnType;
        var name = returns is GenericInstanceType generic ? generic.ElementType.Name : returns.Name;
        if (method.IsConstructor || returns.Namespace == "System.Threading.Tasks" && name is "ValueTask" or "ValueTask`1")
        {
            return TestCodeEnd.Unseen;
        }

        if (returns.Namespace == "System.Threading.Tasks" && name is "Task" or "Task`1")
        {
            return TestCodeEnd.Task;
        }

        var isAsync = method.CustomAttributes.Any(static a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        return isAsync && returns.MetadataType == MetadataType.Void ? TestCodeEnd.Unseen : TestCodeEnd.Return;
    }

    /// <summary>
    /// Lets the tracker see when the test code an entry point runs ends, so code the framework runs
    /// between tests is told apart (docs/design/between-tests-code.md): after <see cref="SiftTracker.EnterTestCode"/>,
    /// inserted with the other probes, the original body goes into a <c>try</c> whose <c>finally</c> (or, for a returned
    /// task, <c>fault</c>) calls <see cref="SiftTracker.ExitTestCode"/>; every <c>ret</c> becomes a
    /// <c>leave</c> to a new return after the handler, keeping the value in a local. A returned task
    /// is handed to <see cref="SiftTracker.ExitTestCodeWhenComplete"/> before leaving.
    /// </summary>
    /// <param name="method">Test-class entry point whose end can be seen, probes inserted, short branches expanded.</param>
    /// <param name="il">Processor of the body.</param>
    /// <param name="probes">Tracker references.</param>
    /// <param name="first">First instruction of the original body: the start of the protected region.</param>
    private static void TrackTestCode(MethodDefinition method, ILProcessor il, Probes probes, Instruction first)
    {
        var end = EndOf(method);
        var body = method.Body;
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void)
        {
            result = new VariableDefinition(method.ReturnType);
            body.Variables.Add(result);
            body.InitLocals = true;
        }

        var exit = result is null ? il.Create(OpCodes.Ret) : il.Create(OpCodes.Ldloc, result);
        foreach (var ret in body.Instructions.Where(static i => i.OpCode == OpCodes.Ret).ToList())
        {
            if (result is not null)
            {
                var store = il.Create(OpCodes.Stloc, result);
                il.InsertBefore(ret, store);
                if (end == TestCodeEnd.Task)
                {
                    il.InsertBefore(ret, il.Create(OpCodes.Ldloc, result));
                    il.InsertBefore(ret, il.Create(OpCodes.Call, probes.ExitTestCodeWhenComplete));
                }

                Retarget(body, ret, store);
            }

            // In place: handler boundaries that named the instruction after a region stay valid.
            ret.OpCode = OpCodes.Leave;
            ret.Operand = exit;
        }

        var handler = il.Create(OpCodes.Call, probes.ExitTestCode);
        il.Append(handler);
        il.Append(il.Create(OpCodes.Endfinally));
        il.Append(exit);
        if (result is not null)
        {
            il.Append(il.Create(OpCodes.Ret));
        }

        body.ExceptionHandlers.Add(new ExceptionHandler(end == TestCodeEnd.Task ? ExceptionHandlerType.Fault : ExceptionHandlerType.Finally)
        {
            TryStart = first,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = exit,
        });
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
    /// True when a method of <paramref name="type"/> turns a pool policy's <c>Create</c> into a
    /// delegate: Microsoft.Extensions.ObjectPool's <c>DefaultObjectPool</c> (and copies of it) stores
    /// <c>policy.Create</c> in a <c>Func&lt;T&gt;</c> field and calls that on a miss.
    /// </summary>
    /// <param name="type">Declaring type of the method being instrumented.</param>
    private static bool BindsPoolPolicyCreate(TypeDefinition type) =>
        type.Methods.Any(static m => m.HasBody && m.Body.Instructions.Any(static i =>
            (i.OpCode == OpCodes.Ldftn || i.OpCode == OpCodes.Ldvirtftn) && i.Operand is MethodReference target && IsPoolPolicyCreate(target)));

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
    /// <param name="createsThroughDelegate">
    /// True when the method's type binds a pool policy's <c>Create</c> to a delegate (see
    /// <see cref="BindsPoolPolicyCreate"/>); its <c>Func&lt;T&gt;.Invoke</c> calls are then fills too.
    /// </param>
    private static void MarkCacheFills(MethodBody body, ILProcessor il, Probes probes, bool createsThroughDelegate)
    {
        var calls = body.Instructions
            .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                        && i.Operand is MethodReference target
                        && (CacheFills.Contains($"{target.DeclaringType.GetElementType().FullName}::{target.Name}")
                            || IsPoolPolicyCreate(target)
                            || createsThroughDelegate && target.Name == "Invoke" && target.DeclaringType.GetElementType().FullName == "System.Func`1")
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

    /// <summary>Collections whose items a pool or hand-rolled cache takes back out, by generic definition; the value is the index of the item type argument.</summary>
    private static readonly FrozenDictionary<string, int> ReusableCollections = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["System.Collections.Generic.Stack`1"] = 0,
        ["System.Collections.Generic.Queue`1"] = 0,
        ["System.Collections.Generic.List`1"] = 0,
        ["System.Collections.Generic.LinkedList`1"] = 0,
        ["System.Collections.Generic.HashSet`1"] = 0,
        ["System.Collections.Generic.Dictionary`2"] = 1,
        ["System.Collections.Generic.SortedDictionary`2"] = 1,
        ["System.Collections.Concurrent.ConcurrentBag`1"] = 0,
        ["System.Collections.Concurrent.ConcurrentStack`1"] = 0,
        ["System.Collections.Concurrent.ConcurrentQueue`1"] = 0,
        ["System.Collections.Concurrent.ConcurrentDictionary`2"] = 1,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Calls that take an object back out of a collection for reuse (a pool's rent, a cache's hit).
    /// <c>Peek</c> and <c>RemoveAt</c> are left out: parsers use them on stacks of objects built and
    /// consumed within one parse, and marking those as run-once would only cost precision.
    /// </summary>
    private static readonly FrozenSet<string> Retrievals = FrozenSet.Create(
        StringComparer.Ordinal, "Pop", "TryPop", "Dequeue", "TryDequeue", "TryTake", "TryGetValue", "TryRemove");

    /// <summary>
    /// Fields of the module holding a collection that some method takes an object back out of
    /// (<see cref="Retrievals"/>), with the type of the collection's items: hand-rolled pools
    /// (<c>Stack&lt;T&gt;</c> rented with <c>TryPop</c>) and caches (<c>Dictionary</c> read with
    /// <c>TryGetValue</c>). The objects in them were built for whichever test first missed, and every
    /// later test reuses them without running that code (docs/design/between-tests-code.md, pools).
    /// </summary>
    /// <param name="module">Module being instrumented.</param>
    private static Dictionary<FieldDefinition, TypeReference> ReusedCollections(ModuleDefinition module)
    {
        var reused = new Dictionary<FieldDefinition, TypeReference>();
        foreach (var method in module.GetTypes().SelectMany(static t => t.Methods).Where(static m => m.HasBody))
        {
            foreach (var call in method.Body.Instructions)
            {
                if ((call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt) || call.Operand is not MethodReference target
                    || !Retrievals.Contains(target.Name) || !ReusableCollections.ContainsKey(target.DeclaringType.GetElementType().FullName)
                    || ReceiverField(call) is not { } field || field.Module != module)
                {
                    continue;
                }

                if (field.FieldType is GenericInstanceType collection
                    && ReusableCollections.TryGetValue(collection.ElementType.FullName, out var item)
                    && item < collection.GenericArguments.Count)
                {
                    reused.TryAdd(field, collection.GenericArguments[item]);
                }
            }
        }

        return reused;
    }

    /// <summary>
    /// The field <paramref name="call"/>'s receiver was loaded from, for the usual shapes:
    /// <c>ldsfld F</c>, or a load (<c>ldarg</c>, <c>ldsfld</c>, <c>ldloc</c>) followed by <c>ldfld</c>s ending in <c>F</c>,
    /// right where the call's arguments start. Null for anything else.
    /// </summary>
    /// <param name="call">A call on an instance.</param>
    private static FieldDefinition? ReceiverField(Instruction call)
    {
        if (call.Operand is not MethodReference { HasThis: true } target)
        {
            return null;
        }

        // Walk back over the other arguments to the instruction that finished pushing the receiver.
        var needed = target.Parameters.Count;
        var instruction = call.Previous;
        for (var steps = 0; instruction is not null && needed > 0 && steps < 32; steps++)
        {
            if (instruction.OpCode.FlowControl is not (FlowControl.Next or FlowControl.Call) || Pops(instruction) is not { } pops || Pushes(instruction) is not { } pushes)
            {
                return null;
            }

            needed -= pushes - pops;
            instruction = instruction.Previous;
        }

        if (needed != 0 || instruction is null)
        {
            return null;
        }

        return instruction.OpCode == OpCodes.Ldfld || instruction.OpCode == OpCodes.Ldsfld || instruction.OpCode == OpCodes.Ldflda || instruction.OpCode == OpCodes.Ldsflda
            ? (instruction.Operand as FieldReference)?.Resolve()
            : null;
    }

    /// <summary>
    /// In a method that loads a reused collection field (<see cref="ReusedCollections"/>), surrounds
    /// every call or <c>newobj</c> that produces the collection's item type with <see cref="SiftTracker.EnterCache"/>
    /// and <see cref="SiftTracker.ExitCache"/>: on a miss the pool or cache builds its item there, and
    /// what that runs serves every later user, so it is run-once evidence. The mark starts where the
    /// arguments start when they are built by calls (<c>new Buffer(Build())</c>).
    /// </summary>
    /// <param name="body">Method body with short branches already expanded.</param>
    /// <param name="il">Processor of the body.</param>
    /// <param name="probes">Tracker references with the module's reused collections.</param>
    private static void MarkPoolFills(MethodBody body, ILProcessor il, Probes probes)
    {
        if (probes.ReusedCollections.Count == 0)
        {
            return;
        }

        var items = body.Instructions
            .Where(static i => i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldsfld || i.OpCode == OpCodes.Ldflda || i.OpCode == OpCodes.Ldsflda)
            .Select(i => (i.Operand as FieldReference)?.Resolve())
            .Where(f => f is not null && probes.ReusedCollections.ContainsKey(f))
            .Select(f => probes.ReusedCollections[f!])
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var builds = body.Instructions
            .Where(i => i.Previous?.OpCode.OpCodeType != OpCodeType.Prefix && Produced(i) is { } produced
                        && items.Exists(item => Produces(produced, item)))
            .ToList();
        foreach (var build in builds)
        {
            var start = ArgumentStart(body, build) ?? build;
            var enter = il.Create(OpCodes.Call, probes.EnterCache);
            il.InsertBefore(start, enter);
            il.InsertAfter(build, il.Create(OpCodes.Call, probes.ExitCache));
            Retarget(body, start, enter);
        }
    }

    /// <summary>The type a call or <c>newobj</c> produces; null for other instructions, for calls on a collection itself, and for void.</summary>
    /// <param name="instruction">Instruction.</param>
    private static TypeReference? Produced(Instruction instruction)
    {
        if (instruction.Operand is not MethodReference target)
        {
            return null;
        }

        if (instruction.OpCode == OpCodes.Newobj)
        {
            return target.DeclaringType;
        }

        if ((instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
            || ReusableCollections.ContainsKey(target.DeclaringType.GetElementType().FullName)
            || target.ReturnType.MetadataType == MetadataType.Void)
        {
            return null;
        }

        return target.ReturnType;
    }

    /// <summary>
    /// True when a value of type <paramref name="produced"/> can be an item of type <paramref name="item"/>:
    /// the same type, a type derived from it or implementing it, or, for an item type that is a generic
    /// parameter (<c>Pool&lt;T&gt;</c>), any generic parameter or <c>object</c>.
    /// </summary>
    /// <param name="produced">Type a call or constructor produces.</param>
    /// <param name="item">Item type of the collection.</param>
    private static bool Produces(TypeReference produced, TypeReference item)
    {
        if (item.IsGenericParameter)
        {
            return produced.IsGenericParameter || produced.MetadataType == MetadataType.Object;
        }

        var wanted = item.GetElementType().FullName;
        var type = TryResolve(produced);
        for (var depth = 0; type is not null && depth < 16; depth++)
        {
            if (type.FullName == wanted || type.Interfaces.Any(i => i.InterfaceType.GetElementType().FullName == wanted))
            {
                return true;
            }

            type = type.BaseType is { } baseType ? TryResolve(baseType) : null;
        }

        return produced.GetElementType().FullName == wanted;
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
    /// Setup methods whose effect reaches a whole namespace or assembly, with the fixture name they
    /// record under: the constructor and <c>[OneTimeSetUp]</c> methods of an NUnit <c>[SetUpFixture]</c>
    /// (scoped to its namespace; none means the assembly); for the assembly, MSTest
    /// <c>[AssemblyInitialize]</c> methods, the constructors and <c>InitializeAsync</c> of an xUnit v3
    /// <c>[assembly: AssemblyFixture(typeof(X))]</c>, <c>[ModuleInitializer]</c> methods, and TUnit hooks
    /// that run for the assembly, the session, discovery, or before every class or test
    /// (<c>[Before(Assembly)]</c>, <c>[BeforeEvery(...)]</c>). Their code would otherwise count as
    /// running outside every test, or for the one test class that declares it.
    /// </summary>
    /// <param name="module">Test module.</param>
    private static Dictionary<MethodDefinition, string> SetUpScopes(ModuleDefinition module)
    {
        var scopes = new Dictionary<MethodDefinition, string>();
        foreach (var attribute in module.Assembly.CustomAttributes.Where(static a => a.AttributeType.FullName == "Xunit.AssemblyFixtureAttribute"))
        {
            if (attribute.ConstructorArguments is not [{ Value: TypeReference fixture }])
            {
                continue;
            }

            for (var type = fixture.Module == module ? TryResolve(fixture) : null;
                 type is not null;
                 type = type.BaseType is { } baseType && baseType.Module == module ? TryResolve(baseType) : null)
            {
                foreach (var method in type.Methods.Where(static m => m.HasBody && IsFixtureSetup(m)))
                {
                    scopes[method] = CoverageMap.SetUpScopePrefix;
                }
            }
        }

        // A [SetUpFixture] may inherit its [OneTimeSetUp] from a base class elsewhere; that code runs
        // for the fixture's scope. A base shared by fixtures of several namespaces serves all of them,
        // so it gets the assembly.
        foreach (var fixture in module.GetTypes().Where(static t => HasAttribute(t.CustomAttributes, "NUnit.Framework.SetUpFixtureAttribute")))
        {
            var scope = CoverageMap.SetUpScopePrefix + Outermost(fixture).Namespace;
            for (var type = fixture.BaseType is { } first && first.Module == module ? TryResolve(first) : null;
                 type is not null;
                 type = type.BaseType is { } baseType && baseType.Module == module ? TryResolve(baseType) : null)
            {
                foreach (var method in type.Methods.Where(static m => m.HasBody
                             && ((m.IsConstructor && !m.IsStatic) || HasAttribute(m.CustomAttributes, "NUnit.Framework.OneTimeSetUpAttribute"))))
                {
                    scopes[method] = scopes.TryGetValue(method, out var other) && other != scope ? CoverageMap.SetUpScopePrefix : scope;
                }
            }
        }

        foreach (var type in module.GetTypes())
        {
            var outer = Outermost(type);
            var isSetUpFixture = HasAttribute(type.CustomAttributes, "NUnit.Framework.SetUpFixtureAttribute");
            foreach (var method in type.Methods.Where(static m => m.HasBody))
            {
                if (isSetUpFixture && ((method.IsConstructor && !method.IsStatic) || HasAttribute(method.CustomAttributes, "NUnit.Framework.OneTimeSetUpAttribute")))
                {
                    scopes[method] = CoverageMap.SetUpScopePrefix + outer.Namespace;
                }
                else if (HasAttribute(method.CustomAttributes, "Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyInitializeAttribute")
                         || HasAttribute(method.CustomAttributes, "System.Runtime.CompilerServices.ModuleInitializerAttribute")
                         || IsAssemblyWideTUnitHook(method))
                {
                    scopes[method] = CoverageMap.SetUpScopePrefix;
                }
            }
        }

        return scopes;
    }

    /// <summary>The top-level type <paramref name="type"/> is nested in, or itself.</summary>
    /// <param name="type">A type.</param>
    private static TypeDefinition Outermost(TypeDefinition type)
    {
        var outer = type;
        while (outer.DeclaringType is { } declaring)
        {
            outer = declaring;
        }

        return outer;
    }

    /// <summary>
    /// True for a TUnit hook that runs for more than its own class: <c>[Before(HookType.Assembly)]</c>,
    /// <c>TestSession</c> and <c>TestDiscovery</c>, and every <c>[BeforeEvery(...)]</c>, which runs before
    /// each test or class of the assembly. <c>[Before(Test)]</c> and <c>[Before(Class)]</c> belong to the
    /// declaring class, which is entered as usual.
    /// </summary>
    /// <param name="method">A method of the test module.</param>
    private static bool IsAssemblyWideTUnitHook(MethodDefinition method)
    {
        foreach (var attribute in method.CustomAttributes)
        {
            switch (attribute.AttributeType.FullName)
            {
                case "TUnit.Core.BeforeEveryAttribute":
                    return true;
                case "TUnit.Core.BeforeAttribute" when attribute.ConstructorArguments is [{ Value: int hook }, ..] && hook >= TUnitAssemblyHook:
                    return true;
            }
        }

        return false;
    }

    /// <summary>Value of TUnit's <c>HookType.Assembly</c>; <c>TestSession</c> and <c>TestDiscovery</c> follow it.</summary>
    private const int TUnitAssemblyHook = 2;

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
        (method.IsConstructor && !method.IsStatic) || method.CustomAttributes.Any(static a => !IsCompilerAttribute(a)) || LifecycleMethods.Contains(method.Name);

    /// <summary>
    /// True for attributes the compiler or analyzers put on any method (nullable metadata, state
    /// machines, suppressions), which say nothing about a test framework calling it. With them, a
    /// static helper of a test class that another class's test calls (OpenTelemetry's
    /// SpanContextShimTests.GetSpanContextShim) became an entry point and took the rest of that
    /// test's code into its own class.
    /// </summary>
    /// <param name="attribute">Attribute of a method.</param>
    private static bool IsCompilerAttribute(CustomAttribute attribute) =>
        attribute.AttributeType.Namespace is "System.Runtime.CompilerServices" or "System.Diagnostics" or "System.Diagnostics.CodeAnalysis";

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
