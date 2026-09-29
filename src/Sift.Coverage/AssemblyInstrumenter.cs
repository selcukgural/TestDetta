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
    private static readonly Lazy<List<string>> SharedFrameworkDirectories = new(FindSharedFrameworkDirectories);

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
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterFixtureInstance))!));
        var fixtures = containsTests ? FixtureTypes(module) : [];

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
                var marker = isTestClass && IsTestClassEntryPoint(method) ? Marker.TestClass
                    : isFixture && IsFixtureSetup(method) ? Marker.Fixture
                    : Marker.None;
                InsertProbes(method, probes, id, marker);
            }
        }

        module.Write(path, new WriterParameters { WriteSymbols = hasSymbols });
    }

    /// <summary>Imported references to the tracker methods the probes call.</summary>
    /// <param name="Hit"><see cref="SiftTracker.Hit"/>.</param>
    /// <param name="EnterTestInstance"><see cref="SiftTracker.EnterTestInstance"/>.</param>
    /// <param name="EnterTestClass"><see cref="SiftTracker.EnterTestClass"/>.</param>
    /// <param name="EnterFixtureInstance"><see cref="SiftTracker.EnterFixtureInstance"/>.</param>
    private sealed record Probes(
        MethodReference Hit,
        MethodReference EnterTestInstance,
        MethodReference EnterTestClass,
        MethodReference EnterFixtureInstance);

    /// <summary>Which bucket marker, if any, a method starts with.</summary>
    private enum Marker
    {
        /// <summary>Only the hit probe.</summary>
        None,

        /// <summary>Entry point of a test class.</summary>
        TestClass,

        /// <summary>Setup of a test fixture.</summary>
        Fixture,
    }

    /// <summary>
    /// Inserts the probes before the first instruction: for a test class entry point or fixture
    /// setup first the bucket marker (so the hit already counts for that bucket), then
    /// <c>ldc.i4 id; call Hit</c>.
    /// </summary>
    /// <param name="method">Method to instrument.</param>
    /// <param name="probes">Tracker references.</param>
    /// <param name="id">Method id.</param>
    /// <param name="marker">Bucket marker to insert, if any.</param>
    /// <remarks>
    /// Short branch forms are expanded before and re-optimized after the insertion, so offsets that
    /// no longer fit in a byte do not corrupt the method. In a constructor the marker runs before
    /// the base constructor call; it only reads the object's type, which is set at allocation.
    /// </remarks>
    private static void InsertProbes(MethodDefinition method, Probes probes, int id, Marker marker)
    {
        var body = method.Body;
        body.SimplifyMacros();

        var il = body.GetILProcessor();
        var first = body.Instructions[0];

        switch (marker)
        {
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
        }

        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, id));
        il.InsertBefore(first, il.Create(OpCodes.Call, probes.Hit));

        body.OptimizeMacros();
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
    /// <c>ICollectionFixture&lt;T&gt;</c>), plus their base classes in the same module, whose
    /// constructors and <c>InitializeAsync</c> run as part of the fixture.
    /// </summary>
    /// <param name="module">Test module.</param>
    private static HashSet<TypeDefinition> FixtureTypes(ModuleDefinition module)
    {
        var fixtures = new HashSet<TypeDefinition>();
        foreach (var type in module.GetTypes())
        {
            foreach (var implementation in type.Interfaces)
            {
                if (implementation.InterfaceType is not GenericInstanceType { ElementType.Name: "IClassFixture`1" or "ICollectionFixture`1" } fixture)
                {
                    continue;
                }

                var argument = fixture.GenericArguments[0] is GenericInstanceType generic ? generic.ElementType : fixture.GenericArguments[0];
                for (var definition = argument.Module == module ? argument.Resolve() : null;
                     definition is not null && fixtures.Add(definition);
                     definition = definition.BaseType is { } baseType && baseType.Module == module ? baseType.Resolve() : null)
                {
                }
            }
        }

        return fixtures;
    }

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
