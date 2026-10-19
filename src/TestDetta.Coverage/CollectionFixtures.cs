using Mono.Cecil;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Finds the test classes each xUnit collection fixture serves: every class of a collection whose definition implements
/// <c>ICollectionFixture&lt;T&gt;</c>, whether or not it asks for the fixture in its constructor.
/// </summary>
/// <remarks>
/// xUnit creates a collection's fixtures before the first class of the collection runs, for all of them, so a class can
/// depend on what the setup did without naming the fixture: a container it started, an environment variable or a static
/// connection string it set (Wolverine's NATS tests find the server through <c>NATS_URL</c>). Selection finds the users of
/// a fixture by the names their code mentions, which misses such a class; this is the framework's own membership, read
/// from the test assembly, where constants and <c>nameof</c> in collection names are already resolved.
/// A class joins a collection with <c>[Collection("name")]</c> naming a <c>[CollectionDefinition("name")]</c> class, or,
/// in xUnit v3, with <c>[Collection(typeof(T))]</c> or <c>[Collection&lt;T&gt;]</c> naming the definition type itself. The
/// attribute counts on a base class in the module too, so a derived class is a member as well.
/// </remarks>
internal static class CollectionFixtures
{
    /// <summary>
    /// Adds to <paramref name="users"/> each collection fixture of <paramref name="module"/> (its name as the tracker writes
    /// it, <see cref="CoverageMap.CollectionFixtureUsers"/>) with the runtime names of the concrete test classes of the
    /// collections it serves.
    /// </summary>
    /// <param name="module">Test module.</param>
    /// <param name="isTestClass">True for the module's test classes.</param>
    /// <param name="users">Fixture name to its users; updated in place.</param>
    public static void Find(ModuleDefinition module, Func<TypeDefinition, bool> isTestClass, Dictionary<string, List<string>> users)
    {
        var byName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var type in module.GetTypes())
        {
            foreach (var attribute in type.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == "Xunit.CollectionDefinitionAttribute"
                    && attribute.ConstructorArguments is [{ Value: string name }, ..])
                {
                    byName.TryAdd(name, type);
                }
            }
        }

        foreach (var type in module.GetTypes())
        {
            // A static class (abstract and sealed) holds tests of its own; any other abstract class runs through its subclasses.
            if (type.HasGenericParameters || type.IsAbstract && !type.IsSealed || !isTestClass(type)
                || Definition(type, byName) is not { } definition)
            {
                continue;
            }

            var testClass = type.FullName.Replace('/', '+');
            foreach (var fixture in Fixtures(definition))
            {
                if (!users.TryGetValue(fixture, out var classes))
                {
                    users[fixture] = classes = [];
                }

                if (!classes.Contains(testClass, StringComparer.Ordinal))
                {
                    classes.Add(testClass);
                }
            }
        }
    }

    /// <summary>
    /// The collection definition the nearest <c>[Collection]</c> attribute of <paramref name="type"/> or one of its base
    /// classes in the module names; null when there is none or the definition is not in the module.
    /// </summary>
    /// <param name="type">Test class.</param>
    /// <param name="byName">Collection definitions of the module by collection name.</param>
    private static TypeReference? Definition(TypeDefinition type, Dictionary<string, TypeDefinition> byName)
    {
        var current = type;
        for (var depth = 0; current is not null && depth < 16; depth++)
        {
            foreach (var attribute in current.CustomAttributes)
            {
                switch (attribute.AttributeType)
                {
                    case GenericInstanceType { ElementType.FullName: "Xunit.CollectionAttribute`1" } generic:
                        return generic.GenericArguments[0];

                    case { FullName: "Xunit.CollectionAttribute" }:
                        return attribute.ConstructorArguments switch
                        {
                            [{ Value: string name }] => byName.GetValueOrDefault(name),
                            [{ Value: TypeReference definition }] => definition,
                            _ => null,
                        };
                }
            }

            current = current.BaseType is { } baseType && baseType.Module == type.Module ? AssemblyInstrumenter.TryResolve(baseType) : null;
        }

        return null;
    }

    /// <summary>
    /// Names of the fixtures <paramref name="definition"/> declares through <c>ICollectionFixture&lt;T&gt;</c>, on itself or
    /// a base class, in the tracker's spelling: runtime names with '+' for nested types and generic arguments in brackets
    /// (<c>App.Tests.Host`1[App.Tests.Profile]</c>). A fixture still open in a generic parameter has no name to give.
    /// </summary>
    /// <param name="definition">Collection definition type.</param>
    private static IEnumerable<string> Fixtures(TypeReference definition)
    {
        var current = definition;
        for (var depth = 0; current is not null && depth < 16; depth++)
        {
            if (AssemblyInstrumenter.TryResolve(current) is not { } resolved)
            {
                yield break;
            }

            foreach (var implementation in resolved.Interfaces)
            {
                if (implementation.InterfaceType is GenericInstanceType { ElementType.Name: "ICollectionFixture`1" } fixture
                    && FixtureName(AssemblyInstrumenter.Bind(fixture.GenericArguments[0], current)) is { } name)
                {
                    yield return name;
                }
            }

            current = resolved.BaseType is { } baseType ? AssemblyInstrumenter.Bind(baseType, current) : null;
        }
    }

    /// <summary>
    /// <paramref name="type"/> as the tracker names a fixture (<c>TestDettaTracker.FixtureName</c>); null when a generic
    /// parameter is left in it.
    /// </summary>
    /// <param name="type">Fixture type with its generic arguments bound.</param>
    private static string? FixtureName(TypeReference type)
    {
        switch (type)
        {
            case GenericParameter:
                return null;

            case GenericInstanceType generic:
                var arguments = new List<string>(generic.GenericArguments.Count);
                foreach (var argument in generic.GenericArguments)
                {
                    if (FixtureName(argument) is not { } name)
                    {
                        return null;
                    }

                    arguments.Add(name);
                }

                return $"{generic.ElementType.FullName.Replace('/', '+')}[{string.Join(",", arguments)}]";

            default:
                return type.HasGenericParameters ? null : type.FullName.Replace('/', '+');
        }
    }
}
