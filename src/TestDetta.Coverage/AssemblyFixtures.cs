using Mono.Cecil;

namespace TestDetta.Coverage;

/// <summary>
/// Finds the types whose setup an xUnit v3 <c>[assembly: AssemblyFixture(typeof(X))]</c> of a test assembly runs: X and its
/// base types, wherever in the repository they are declared.
/// </summary>
/// <remarks>
/// xUnit creates an assembly fixture before the first test of the assembly, for every class of it, so every class may
/// depend on what the setup did without naming the fixture. The setup methods record under the assembly's setup scope
/// (<see cref="AssemblyInstrumenter"/>), which selects every class. The fixture often lives in a shared test library the
/// test project references rather than in the test assembly, and that library is rewritten on its own, where nothing says
/// its type serves as an assembly fixture; without this lookup its setup counted as code outside every test, which
/// selection could only follow by name, and a class that named nothing of it was missed. The lookup reads the test
/// assemblies before any of them is rewritten, as <see cref="ExtensionAttributes"/> does.
/// </remarks>
internal static class AssemblyFixtures
{
    /// <summary>
    /// The fixture types of the assembly fixtures of <paramref name="testAssemblies"/> and their base types, as (assembly
    /// name, Cecil full name with '/' for nested types). The walk up the base types stops at the first one outside the
    /// repository: its code is not rewritten, and a repository type above it is not expected.
    /// </summary>
    /// <param name="testAssemblies">Paths of the test assemblies.</param>
    /// <param name="ownAssemblies">Names of the repository's assemblies.</param>
    public static HashSet<(string Assembly, string Type)> Find(IEnumerable<string> testAssemblies, IReadOnlySet<string> ownAssemblies)
    {
        var types = new HashSet<(string Assembly, string Type)>();
        foreach (var path in testAssemblies)
        {
            // The fixture and its bases live in the other assemblies of the output, as for instrumenting.
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
            foreach (var directory in AssemblyInstrumenter.SharedFrameworkDirectories.Value)
            {
                resolver.AddSearchDirectory(directory);
            }

            using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            foreach (var attribute in module.Assembly.CustomAttributes)
            {
                if (attribute.AttributeType.FullName != "Xunit.AssemblyFixtureAttribute"
                    || attribute.ConstructorArguments is not [{ Value: TypeReference fixture }])
                {
                    continue;
                }

                // A generic fixture (typeof(AppFixture<Program>)) resolves to its definition, whose methods are the ones rewritten.
                for (var type = AssemblyInstrumenter.TryResolve(fixture);
                     type is not null && ownAssemblies.Contains(type.Module.Assembly.Name.Name);
                     type = type.BaseType is { } baseType ? AssemblyInstrumenter.TryResolve(baseType) : null)
                {
                    types.Add((type.Module.Assembly.Name.Name, type.FullName));
                }
            }
        }

        return types;
    }
}
