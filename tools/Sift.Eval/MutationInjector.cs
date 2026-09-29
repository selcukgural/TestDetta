using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Eval;

/// <summary>Assemblies rewritten for one mutant, and how to put them back.</summary>
/// <param name="Backups">Rewritten file to its backup copy.</param>
/// <param name="MutatedMethods">Number of compiled methods that now throw.</param>
internal sealed record Mutation(IReadOnlyDictionary<string, string> Backups, int MutatedMethods) : IDisposable
{
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

/// <summary>
/// Makes a source member fail: every compiled method that belongs to it (its overloads, and the
/// lambdas, iterators and async state machines it contains) starts by throwing. Working on the
/// compiled assemblies keeps the mutant independent of the member's syntax and of warnings-as-errors.
/// </summary>
internal static class MutationInjector
{
    /// <summary>Message of the injected exception, so a failure can be traced to the mutant.</summary>
    private const string Message = "Sift mutant";

    /// <summary>Rewrites every copy of the assembly that declares <paramref name="target"/>.</summary>
    /// <param name="assemblies">Copies of the assembly in the test outputs.</param>
    /// <param name="target">Member to break.</param>
    /// <param name="backupDirectory">Where the originals are copied first.</param>
    public static Mutation Inject(IReadOnlyList<string> assemblies, CoveredMethod target, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var mutated = 0;

        foreach (var assembly in assemblies)
        {
            var backup = Path.Combine(backupDirectory, $"{backups.Count}-{Path.GetFileName(assembly)}");
            File.Copy(assembly, backup, overwrite: true);

            var count = InjectOne(assembly, target);
            if (count == 0)
            {
                File.Delete(backup);
                continue;
            }

            backups[assembly] = backup;
            mutated += count;
        }

        return new Mutation(backups, mutated);
    }

    /// <summary>Rewrites one assembly; returns the number of mutated methods.</summary>
    /// <param name="path">Assembly file.</param>
    /// <param name="target">Member to break.</param>
    private static int InjectOne(string path, CoveredMethod target)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });

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

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body.Instructions.Count == 0 || MethodNames.Map(method) != target)
                {
                    continue;
                }

                var body = method.Body;
                body.SimplifyMacros();
                var il = body.GetILProcessor();
                var first = body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldstr, $"{Message}: {target.TypeName}.{target.MemberName}"));
                il.InsertBefore(first, il.Create(OpCodes.Newobj, constructor));
                il.InsertBefore(first, il.Create(OpCodes.Throw));
                body.OptimizeMacros();
                count++;
            }
        }

        if (count > 0)
        {
            module.Write(path);
        }

        return count;
    }
}
