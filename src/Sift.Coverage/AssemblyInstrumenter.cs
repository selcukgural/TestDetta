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

        foreach (var assembly in assemblies)
        {
            BackUp(assembly, backupDirectory, backups);
            BackUp(Path.ChangeExtension(assembly, ".pdb"), backupDirectory, backups);

            var before = methods.Count;
            InstrumentOne(assembly, testAssemblies.Contains(assembly), methods);
            LogInstrumented(assembly, methods.Count - before);
        }

        return new InstrumentationResult(methods, backups);
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
            module.ImportReference(typeof(SiftTracker).GetMethod(nameof(SiftTracker.EnterTestClass))!));

        foreach (var type in module.GetTypes())
        {
            // Top-level compiler infrastructure (<PrivateImplementationDetails>, <>z__ReadOnlyArray`1)
            // has no source member a change could name.
            if (MethodNames.IsCompilerInfrastructure(type))
            {
                continue;
            }

            var isTestClass = containsTests && IsTestClass(type, depth: 0);

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body.Instructions.Count == 0)
                {
                    continue;
                }

                var id = methods.Count;
                methods.Add(MethodNames.Map(method));
                InsertProbes(method, probes, id, isTestClass && IsTestClassEntryPoint(method));
            }
        }

        module.Write(path, new WriterParameters { WriteSymbols = hasSymbols });
    }

    /// <summary>Imported references to the tracker methods the probes call.</summary>
    /// <param name="Hit"><see cref="SiftTracker.Hit"/>.</param>
    /// <param name="EnterTestInstance"><see cref="SiftTracker.EnterTestInstance"/>.</param>
    /// <param name="EnterTestClass"><see cref="SiftTracker.EnterTestClass"/>.</param>
    private sealed record Probes(MethodReference Hit, MethodReference EnterTestInstance, MethodReference EnterTestClass);

    /// <summary>
    /// Inserts the probes before the first instruction: for a test class entry point first the
    /// class marker (so the hit already counts for the class), then <c>ldc.i4 id; call Hit</c>.
    /// </summary>
    /// <param name="method">Method to instrument.</param>
    /// <param name="probes">Tracker references.</param>
    /// <param name="id">Method id.</param>
    /// <param name="marksTestClass">True when the method is an entry point of a test class.</param>
    /// <remarks>
    /// Short branch forms are expanded before and re-optimized after the insertion, so offsets that
    /// no longer fit in a byte do not corrupt the method. In a constructor the marker runs before
    /// the base constructor call; it only reads the object's type, which is set at allocation.
    /// </remarks>
    private static void InsertProbes(MethodDefinition method, Probes probes, int id, bool marksTestClass)
    {
        var body = method.Body;
        body.SimplifyMacros();

        var il = body.GetILProcessor();
        var first = body.Instructions[0];

        if (marksTestClass && method.IsStatic)
        {
            il.InsertBefore(first, il.Create(OpCodes.Ldstr, method.DeclaringType.FullName.Replace('/', '+')));
            il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterTestClass));
        }
        else if (marksTestClass)
        {
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, probes.EnterTestInstance));
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
