using Mono.Cecil;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Attribute types of the repository that its tests put on test classes, test methods or their parameters: test
/// framework extensions the repository ships and uses on itself (AutoFixture's <c>[AutoData]</c>, FsCheck's
/// <c>[Property]</c>, a test project's own <c>[TheoryEx]</c>). The framework runs their code while it discovers and
/// builds the tests, outside every test, so coverage could not answer for it or for what it ran (AutoFixture's
/// <c>Fixture</c>, built by <c>AutoDataAttribute.get_Fixture</c> while NUnit builds the test cases). Their code serves the
/// classes that use them: each gets a shared bucket (<see cref="CoverageMap.DataSourcePrefix"/> and the attribute type),
/// whose users are those classes and every concrete class deriving from one (<see cref="CoverageMap.DataSourceUsers"/>).
/// </summary>
internal static class ExtensionAttributes
{
    /// <summary>
    /// The shared bucket of each repository attribute type the test assemblies use, by the type's full name (Cecil's,
    /// nested types with '/'), with its users added to <paramref name="sharedUsers"/>. An attribute a generic class uses
    /// is left out, as for data sources: its runtime names may differ from the map's.
    /// </summary>
    /// <param name="testAssemblies">Paths of the test assemblies.</param>
    /// <param name="ownAssemblies">Names of the repository's assemblies.</param>
    /// <param name="isTestClass">True for a test class.</param>
    /// <param name="sharedUsers">Users of each shared bucket; updated in place.</param>
    public static Dictionary<string, string> Find(
        IEnumerable<string> testAssemblies,
        IReadOnlySet<string> ownAssemblies,
        Func<TypeDefinition, bool> isTestClass,
        Dictionary<string, List<string>> sharedUsers)
    {
        var owners = new Dictionary<string, HashSet<TypeDefinition>>(StringComparer.Ordinal);
        var derived = new Dictionary<TypeDefinition, List<TypeDefinition>>();
        var modules = new List<ModuleDefinition>();
        var resolvers = new List<DefaultAssemblyResolver>();
        try
        {
            foreach (var path in testAssemblies)
            {
                // Telling a test class needs its base types and attributes, which live in the other assemblies of the
                // output and the shared framework, as for instrumenting.
                var resolver = new DefaultAssemblyResolver();
                resolvers.Add(resolver);
                resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
                foreach (var directory in AssemblyInstrumenter.SharedFrameworkDirectories.Value)
                {
                    resolver.AddSearchDirectory(directory);
                }

                var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
                modules.Add(module);
                Collect(module, ownAssemblies, isTestClass, owners, derived);
            }

            var buckets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (attribute, classes) in owners)
            {
                if (Users(classes, derived) is not { Count: > 0 } users)
                {
                    continue;
                }

                var bucket = CoverageMap.DataSourcePrefix + attribute.Replace('/', '+');
                if (!sharedUsers.TryGetValue(bucket, out var known))
                {
                    sharedUsers[bucket] = known = [];
                }

                known.AddRange(users.Where(u => !known.Contains(u, StringComparer.Ordinal)));
                buckets[attribute] = bucket;
            }

            return buckets;
        }
        finally
        {
            foreach (var module in modules)
            {
                module.Dispose();
            }

            foreach (var resolver in resolvers)
            {
                resolver.Dispose();
            }
        }
    }

    /// <summary>Adds the repository attributes on the test classes of <paramref name="module"/>, and its types' subclasses.</summary>
    /// <param name="module">A test module.</param>
    /// <param name="ownAssemblies">Names of the repository's assemblies.</param>
    /// <param name="isTestClass">True for a test class.</param>
    /// <param name="owners">Test classes using each attribute type; updated in place.</param>
    /// <param name="derived">Each type's direct subclasses; updated in place.</param>
    private static void Collect(
        ModuleDefinition module,
        IReadOnlySet<string> ownAssemblies,
        Func<TypeDefinition, bool> isTestClass,
        Dictionary<string, HashSet<TypeDefinition>> owners,
        Dictionary<TypeDefinition, List<TypeDefinition>> derived)
    {
        foreach (var type in module.GetTypes())
        {
            if (type.BaseType is { Scope: ModuleDefinition } baseType && baseType.Resolve() is { } resolved)
            {
                if (!derived.TryGetValue(resolved, out var children))
                {
                    derived[resolved] = children = [];
                }

                children.Add(type);
            }

            if (!isTestClass(type))
            {
                continue;
            }

            Add(type.CustomAttributes, type, ownAssemblies, owners);
            foreach (var method in type.Methods)
            {
                Add(method.CustomAttributes, type, ownAssemblies, owners);
                foreach (var parameter in method.Parameters)
                {
                    Add(parameter.CustomAttributes, type, ownAssemblies, owners);
                }
            }
        }
    }

    /// <summary>Adds <paramref name="owner"/> as a user of each repository attribute type among <paramref name="attributes"/>.</summary>
    /// <param name="attributes">Attributes of a test class, method or parameter.</param>
    /// <param name="owner">The test class.</param>
    /// <param name="ownAssemblies">Names of the repository's assemblies.</param>
    /// <param name="owners">Test classes using each attribute type; updated in place.</param>
    private static void Add(
        IEnumerable<CustomAttribute> attributes,
        TypeDefinition owner,
        IReadOnlySet<string> ownAssemblies,
        Dictionary<string, HashSet<TypeDefinition>> owners)
    {
        foreach (var attribute in attributes)
        {
            // Declared in the test module, or in another assembly of the repository. The compiler embeds attributes of its own
            // into the module (System.Runtime.CompilerServices.NullableAttribute, Microsoft.CodeAnalysis.EmbeddedAttribute):
            // they are no extension, and reflection builds them everywhere.
            var type = attribute.AttributeType;
            if (type.Namespace.StartsWith("System", StringComparison.Ordinal) || type.Namespace.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                || !(type.Scope is ModuleDefinition || type.Scope is AssemblyNameReference reference && ownAssemblies.Contains(reference.Name)))
            {
                continue;
            }

            if (!owners.TryGetValue(type.FullName, out var classes))
            {
                owners[type.FullName] = classes = [];
            }

            classes.Add(owner);
        }
    }

    /// <summary>The concrete classes among <paramref name="classes"/> and those deriving from them; null when one is generic.</summary>
    /// <param name="classes">Test classes using an attribute.</param>
    /// <param name="derived">Each type's direct subclasses.</param>
    private static List<string>? Users(HashSet<TypeDefinition> classes, Dictionary<TypeDefinition, List<TypeDefinition>> derived)
    {
        var users = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<TypeDefinition>(classes);
        var seen = new HashSet<TypeDefinition>();
        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            if (type.HasGenericParameters)
            {
                return null;
            }

            if (!type.IsAbstract)
            {
                users.Add(type.FullName.Replace('/', '+'));
            }

            foreach (var child in derived.GetValueOrDefault(type) ?? [])
            {
                pending.Push(child);
            }
        }

        return [.. users];
    }
}
